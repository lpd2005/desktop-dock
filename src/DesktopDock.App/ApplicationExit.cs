using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopDock.App;

internal enum ApplicationExitState { Exited, AwaitingApplication, BackgroundRunning }

// One menu action owns a fixed set of process identities. A later launch never
// becomes part of that action, even if Windows reuses a process or window ID.
internal sealed class ApplicationExit
{
    private sealed record Instance(int Id, long Created, string Executable);
    private readonly Instance[] instances;
    private ApplicationExit(Instance[] instances) => this.instances = instances;
    public bool Running => instances.Any(StillRunning);

    internal static ApplicationExit? Capture(ApplicationTarget target, IReadOnlyList<nint> associated, uint boundProcess = 0)
    {
        // Explorer owns the desktop/taskbar as well as folders. Shell hosts and
        // this Dock's background guard must never be treated as an ordinary app.
        if (target.ProcessName is "explorer" && target.AppId is null) return null;
        if (target.SpecificWindow && boundProcess == 0) return null;
        bool packaged = target.AppId?.Contains('!') == true;
        var windowProcesses = associated.Select(hwnd => { Native.GetWindowThreadProcessId(hwnd, out uint pid); return pid; }).ToHashSet();
        var found = new List<Instance>();
        using var current = Process.GetCurrentProcess();
        var candidates = !packaged && target.ProcessName is not null ? Process.GetProcessesByName(target.ProcessName) : Process.GetProcesses();
        foreach (var process in candidates)
        using (process)
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.SessionId != current.SessionId || Protected(process.ProcessName)) continue;
                bool matches;
                if (target.SpecificWindow) matches = boundProcess != 0 && process.Id == boundProcess;
                else if (packaged) matches = string.Equals(WindowAppId.ReadProcess((uint)process.Id), target.AppId, StringComparison.OrdinalIgnoreCase);
                else if (target.ExecutablePath is not null)
                    matches = string.Equals(process.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(process.MainModule?.FileName, target.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                else matches = windowProcesses.Contains((uint)process.Id) && (target.AppId is not null || process.Id == boundProcess);
                if (!matches) continue;
                string? executable = process.MainModule?.FileName;
                if (executable is null || !target.SpecificWindow && string.Equals(executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                using var handle = OpenProcess(0x1000, false, process.Id);
                if (handle.IsInvalid || !IsProcessCritical(handle, out bool critical) || critical) continue;
                found.Add(new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), Path.GetFullPath(executable)));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return found.Count == 0 ? null : new(found.ToArray());
    }

    private static bool Protected(string name) => name.ToLowerInvariant() is "explorer" or "applicationframehost" or "runtimebroker"
        or "shellexperiencehost" or "startmenuexperiencehost" or "searchhost" or "shellhost" or "sihost" or "dwm" or "winlogon"
        or "csrss" or "lsass" or "services" or "svchost" or "system" or "registry" or "smss" or "idle";

    private static bool StillRunning(Instance instance)
    {
        using var handle = OpenProcess(0x1000 | 0x100000, false, instance.Id);
        return !handle.IsInvalid && GetProcessTimes(handle, out long created, out _, out _, out _)
            && created == instance.Created && WaitForSingleObject(handle, 0) == 0x102;
    }

    internal List<nint> VisibleWindows()
    {
        var processes = instances.Where(StillRunning).Select(x => x.Id).ToHashSet();
        var windows = new List<nint>();
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!processes.Contains((int)pid) || !Native.IsWindowVisible(hwnd)) return true;
            if (Native.DwmGetIntAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowRect(hwnd, out var frame);
            if (Native.IsIconic(hwnd) || frame.Right - frame.Left >= 80 && frame.Bottom - frame.Top >= 40) windows.Add(hwnd);
            return true;
        }, 0);
        return windows;
    }

    internal static bool CanCloseWindow(nint window)
    {
        if (window == 0 || !Native.IsWindow(window)) return false;
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var name = new StringBuilder(256);
        Native.GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    internal static bool ExecutableMatchesWindow(nint window, string expected)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static bool CloseWindow(nint window, uint process)
    {
        if (process == Environment.ProcessId || !CanCloseWindow(window)) return false;
        Native.GetWindowThreadProcessId(window, out uint current);
        return current == process && Native.PostMessage(window, 0x0010, 0, 0);
    }

    public async Task<ApplicationExitState> RequestNormalExit()
    {
        foreach (nint hwnd in VisibleWindows().Where(hwnd => Native.GetWindow(hwnd, 4) == 0))
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            CloseWindow(hwnd, pid);
        }
        // Give the application's close/save handlers time to run; no blocking UI wait.
        await Task.Delay(900);
        return !Running ? ApplicationExitState.Exited : VisibleWindows().Count != 0
            ? ApplicationExitState.AwaitingApplication : ApplicationExitState.BackgroundRunning;
    }

    public bool ForceBackgroundExit(bool confirmed)
    {
        if (!confirmed || VisibleWindows().Count != 0) return false;
        bool success = true;
        foreach (var instance in instances)
        {
            if (!StillRunning(instance)) continue;
            using var handle = OpenProcess(0x1000 | 0x100000 | 1, false, instance.Id);
            // Creation time, executable and critical state are checked on the same
            // handle that will be terminated. Never kill an entire child tree.
            var executable = new StringBuilder(32768); uint length = (uint)executable.Capacity;
            if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out _, out _) || created != instance.Created
                || !QueryFullProcessImageName(handle, 0, executable, ref length)
                || !string.Equals(executable.ToString(), instance.Executable, StringComparison.OrdinalIgnoreCase)
                || !IsProcessCritical(handle, out bool critical) || critical || !TerminateProcess(handle, 0)) success = false;
        }
        return success;
    }

    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll")] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
