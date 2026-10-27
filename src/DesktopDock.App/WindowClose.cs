using System.Diagnostics;
using System.Text;

namespace DesktopDock.App;

internal static class WindowClose
{
    internal static bool CanClose(nint window)
    {
        if (window == 0 || !Native.IsWindow(window)) return false;
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var name = new StringBuilder(256);
        Native.GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    internal static bool ExecutableMatches(nint window, string expected)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static bool Request(nint window, uint process)
    {
        if (process == Environment.ProcessId || !CanClose(window)) return false;
        Native.GetWindowThreadProcessId(window, out uint current);
        return current == process && Native.PostMessage(window, 0x0010, 0, 0);
    }
}
