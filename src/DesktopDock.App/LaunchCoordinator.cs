using System.Diagnostics;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class LaunchCoordinator : IDisposable
{
    private sealed record BoundWindow(nint Handle, uint Process);
    private readonly Dictionary<string, ApplicationTarget> targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoundWindow> bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private ApplicationTarget? pendingTarget;
    private string pendingKey = "";
    private uint launchedProcess;
    private HashSet<nint> previous = [];
    private double started;
    private nint launchForeground;
    private readonly Func<nint, string?> readAppId;
    public bool IsWatching => timer.IsEnabled;
    public AppClickAction LastAction { get; private set; }
    public nint LastWindow { get; private set; }

    public LaunchCoordinator(Func<nint, string?>? readAppId = null)
    {
        this.readAppId = readAppId ?? WindowAppId.Read;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Poll();
    }

    public void Launch(DockItem item, bool toggle = true, nint? foregroundAtPress = null, bool allowLaunch = true)
    {
        string key = item.Target + "\n" + item.Arguments;
        if (targets.Count > 100) { targets.Clear(); bound.Clear(); }
        if (!targets.TryGetValue(key, out var info)) targets[key] = info = ApplicationTarget.Resolve(item.Target, item.Arguments);
        var windows = Windows();
        nint foreground = foregroundAtPress ?? Native.GetAncestor(Native.GetForegroundWindow(), 3);
        nint cached = 0;
        if (bound.TryGetValue(key, out var old) && windows.Contains(old.Handle))
        {
            Native.GetWindowThreadProcessId(old.Handle, out uint pid);
            if (pid == old.Process) cached = old.Handle;
        }
        // A URL, folder or argument-specific shortcut uses its associated window after its first launch.
        var matches = info.SpecificWindow ? new List<nint>() : windows.Where(hwnd => Matches(hwnd, 0, info)).ToList();
        nint existing = cached != 0 ? cached : matches.FirstOrDefault();
        if (matches.Contains(foreground)) existing = foreground;
        if (existing != 0)
        {
            StopWatch();
            LastWindow = existing;
            LastAction = AppClick.Decide(true, existing == foreground, Native.IsIconic(existing), toggle);
            Native.GetWindowThreadProcessId(existing, out uint pid);
            bound[key] = new(existing, pid);
            if (LastAction == AppClickAction.Minimize) Native.ShowWindowAsync(existing, 6);
            else
            {
                Native.ShowWindowAsync(existing, LastAction == AppClickAction.Restore ? 9 : 5);
                Native.SetForegroundWindow(existing);
            }
            return;
        }
        LastAction = AppClick.Decide(false, false, false, toggle, allowLaunch && !(IsWatching && key == pendingKey));
        LastWindow = 0;
        if (LastAction == AppClickAction.Ignore) return;
        StopWatch();
        launchForeground = Native.GetForegroundWindow();
        using var process = Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(item.Target), item.Arguments) { UseShellExecute = true });
        pendingKey = key; pendingTarget = info; previous = windows.ToHashSet();
        launchedProcess = process is null ? 0 : (uint)process.Id;
        started = watch.Elapsed.TotalSeconds;
        if (launchedProcess != 0 || info.ProcessName is not null || info.AppId is not null) timer.Start();
    }

    private void Poll()
    {
        double elapsed = watch.Elapsed.TotalSeconds - started;
        if (elapsed > 5) { StopWatch(); return; }
        var windows = Windows();
        nint candidate = launchedProcess == 0 ? 0 : windows.FirstOrDefault(hwnd => !previous.Contains(hwnd) && Matches(hwnd, launchedProcess, null));
        if (candidate == 0 && pendingTarget is not null)
        {
            candidate = windows.FirstOrDefault(hwnd => !previous.Contains(hwnd) && Matches(hwnd, 0, pendingTarget));
            nint foreground = Native.GetAncestor(Native.GetForegroundWindow(), 3);
            if (candidate == 0 && elapsed > .8 && windows.Contains(foreground)
                && (foreground != launchForeground || pendingTarget.ProcessName == "explorer") && Matches(foreground, 0, pendingTarget))
                candidate = foreground;
        }
        if (candidate == 0) return;
        Native.GetWindowThreadProcessId(candidate, out uint pid);
        bound[pendingKey] = new(candidate, pid);
        LastWindow = candidate;
        StopWatch();
    }

    private void StopWatch() { timer.Stop(); previous.Clear(); pendingTarget = null; }

    internal List<nint> WindowsFor(DockItem item)
    {
        string key = item.Target + "\n" + item.Arguments;
        var target = ApplicationTarget.Resolve(item.Target, item.Arguments);
        var windows = target.SpecificWindow ? new List<nint>() : Windows().Where(hwnd => Matches(hwnd, 0, target)).ToList();
        if (bound.TryGetValue(key, out var cached) && Native.IsWindow(cached.Handle))
        {
            Native.GetWindowThreadProcessId(cached.Handle, out uint pid);
            if (pid == cached.Process)
            {
                if (!windows.Contains(cached.Handle)) windows.Insert(0, cached.Handle);
            }
        }
        windows.RemoveAll(hwnd => !WindowClose.CanClose(hwnd));
        if (target.AppId is null && target.ExecutablePath is not null)
            windows.RemoveAll(hwnd => !WindowClose.ExecutableMatches(hwnd, target.ExecutablePath));
        return windows;
    }

    internal static List<nint> Windows()
    {
        var result = new List<nint>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (Native.DwmGetIntAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            long style = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((style & (0x80 | 0x08000000)) != 0) return true;
            Native.GetWindowRect(hwnd, out var rect);
            if (Native.IsIconic(hwnd) || rect.Right - rect.Left >= 160 && rect.Bottom - rect.Top >= 100) result.Add(hwnd);
            return true;
        }, 0);
        return result;
    }

    internal bool Matches(nint hwnd, uint expectedPid, ApplicationTarget? info)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (expectedPid != 0 && pid == expectedPid) return true;
        if (info is null) return false;
        if (LaunchArguments.IsChromiumBrowser(info.ProcessName) && info.ExecutablePath is not null
            && !WindowClose.ExecutableMatches(hwnd, info.ExecutablePath)) return false;
        if (info.AppId is not null)
        {
            string? id = readAppId(hwnd);
            if (!string.IsNullOrEmpty(id)) return info.AppId.Equals(id, StringComparison.OrdinalIgnoreCase);
            // Never fall back to the shell's process name for an app activation shortcut.
            if (info.ProcessName == "explorer" || info.AppId.Contains('!')) return false;
        }
        if (info.ProcessName is null) return false;
        try { using var process = Process.GetProcessById((int)pid); return info.ProcessName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public void Dispose() => StopWatch();
}
