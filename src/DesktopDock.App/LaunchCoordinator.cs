using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class LaunchCoordinator : IDisposable
{
    private readonly DockSettings settings;
    private readonly Func<WindowBounds> available;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private string? hint;
    private uint launchedProcess;
    private HashSet<nint> previous = [];
    private double deadline, candidateSince;
    private nint candidate;
    private nint launchForeground;
    private double started;
    public bool IsWatching => timer.IsEnabled;
    public nint LastCandidate { get; private set; }
    public bool LastFitAccepted { get; private set; }

    public LaunchCoordinator(DockSettings settings, Func<WindowBounds> available)
    {
        this.settings = settings; this.available = available;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Poll();
    }

    public void Launch(DockItem item)
    {
        timer.Stop(); candidate = 0; LastCandidate = 0; LastFitAccepted = false;
        launchForeground = Native.GetForegroundWindow();
        string target = Environment.ExpandEnvironmentVariables(item.Target);
        hint = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(target)
            : target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "SystemSettings"
            : target.StartsWith("microsoft-edge:", StringComparison.OrdinalIgnoreCase) ? "msedge"
            : Directory.Exists(target) || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "explorer" : null;
        var windows = Windows();
        // Folder requests must still navigate to the requested folder.
        nint existing = string.IsNullOrWhiteSpace(item.Arguments) && hint is not null && !hint.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? windows.FirstOrDefault(hwnd => Matches(hwnd, 0, hint)) : 0;
        if (existing != 0)
        {
            Native.ShowWindowAsync(existing, Native.IsIconic(existing) ? 9 : 5);
            Native.SetForegroundWindow(existing);
            if (settings.KeepLaunchedWindowsAboveDock) BeginWatch(0, windows, existing);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo(target, item.Arguments) { UseShellExecute = true });
        if (settings.KeepLaunchedWindowsAboveDock) BeginWatch(process is null ? 0 : (uint)process.Id, windows, 0);
    }

    private void BeginWatch(uint process, HashSet<nint> before, nint initial)
    {
        launchedProcess = process; previous = before; candidate = initial;
        candidateSince = watch.Elapsed.TotalSeconds;
        started = candidateSince;
        deadline = candidateSince + 5;
        if (process == 0 && hint is null && initial == 0) return;
        timer.Start();
    }

    private void Poll()
    {
        double now = watch.Elapsed.TotalSeconds;
        if (now > deadline) { timer.Stop(); return; }
        if (candidate == 0 || !Native.IsWindow(candidate))
        {
            nint foreground = Native.GetForegroundWindow();
            var windows = Windows();
            candidate = launchedProcess == 0 ? 0 : windows.FirstOrDefault(hwnd => !previous.Contains(hwnd) && Matches(hwnd, launchedProcess, null));
            if (candidate == 0 && now - started > .8 && windows.Contains(foreground)
                && (foreground != launchForeground || string.Equals(hint, "explorer", StringComparison.OrdinalIgnoreCase))
                && Matches(foreground, launchedProcess, hint)) candidate = foreground;
            candidateSince = now;
        }
        if (candidate == 0 || now - candidateSince < .18) return;
        LastCandidate = candidate;
        LastFitAccepted = AppWindowPlacement.FitAboveDock(candidate, available());
        // A brief second pass allows the app to finish applying its own startup layout.
        if (now - candidateSince > .55) timer.Stop();
    }

    internal static HashSet<nint> Windows()
    {
        var result = new HashSet<nint>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != 0) return true;
            if (Native.DwmGetIntAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowRect(hwnd, out var rect);
            if (Native.IsIconic(hwnd) || rect.Right - rect.Left >= 160 && rect.Bottom - rect.Top >= 100) result.Add(hwnd);
            return true;
        }, 0);
        return result;
    }

    private static bool Matches(nint hwnd, uint expectedPid, string? expectedName)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (expectedPid != 0 && pid == expectedPid) return true;
        if (expectedName is null) return false;
        try { using var process = Process.GetProcessById((int)pid); return expectedName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public void Dispose() => timer.Stop();
}
