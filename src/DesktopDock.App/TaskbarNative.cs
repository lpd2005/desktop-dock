using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed record TaskbarSnapshot(long Handle, uint ProcessId, long ProcessStart, WindowBounds Frame,
    WindowBounds Monitor, WindowBounds Work, bool WasVisible, bool NativeAutoHide)
{
    public bool IsTop => Frame.Right - Frame.Left > Frame.Bottom - Frame.Top
        && Math.Abs(Frame.Top - Monitor.Top) <= 3 && Frame.Bottom > Monitor.Top
        && Frame.Bottom - Monitor.Top < (Monitor.Bottom - Monitor.Top) / 3;
    public bool CanReleaseTop => IsTop && Math.Abs(Work.Top - Frame.Bottom) <= 3;
    public WindowBounds ExpandedWork => CanReleaseTop ? Work with { Top = Monitor.Top } : Work;
}

internal static class TaskbarNative
{
    public static WindowBounds Bounds(Native.Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);
    public static Native.Rect Rect(WindowBounds r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
    public static string Class(nint hwnd)
    {
        var value = new StringBuilder(128); Native.GetClassName(hwnd, value, value.Capacity); return value.ToString();
    }
    public static long ProcessStart(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }
    public static bool SameProcess(uint pid, long start) => start != 0 && ProcessStart(pid) == start;
    public static TaskbarSnapshot? Capture()
    {
        nint hwnd = Native.FindWindow("Shell_TrayWnd", null);
        if (hwnd == 0 || !Native.GetWindowRect(hwnd, out var frame)) return null;
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref monitor) || (monitor.Flags & 1) == 0) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var data = new Native.AppBarData { Size = (uint)Marshal.SizeOf<Native.AppBarData>(), Window = hwnd };
        return new(hwnd, pid, ProcessStart(pid), Bounds(frame), Bounds(monitor.Monitor), Bounds(monitor.Work),
            Native.IsWindowVisible(hwnd), (Native.SHAppBarMessage(4, ref data) & 1) != 0);
    }
    public static bool Matches(TaskbarSnapshot s) => Native.IsWindow((nint)s.Handle)
        && Class((nint)s.Handle) == "Shell_TrayWnd"
        && Native.GetWindowThreadProcessId((nint)s.Handle, out uint pid) != 0 && pid == s.ProcessId && SameProcess(pid, s.ProcessStart);
    public static bool SetWork(WindowBounds bounds)
    {
        var rect = Rect(bounds);
        return Native.SystemParametersInfo(0x002F, 0, ref rect, 0); // Transient, without changing persisted system preferences.
    }
    public static bool Contains(WindowBounds r, Native.Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    public static bool ShellInteraction(TaskbarSnapshot s)
    {
        if (Down(0x5B) || Down(0x5C)) return true;
        var gui = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        // Keep a menu usable after the bar was revealed, but do not reveal a hidden
        // taskbar just because an unrelated application's context menu opened.
        if (Native.GetGUIThreadInfo(0, ref gui) && (gui.Flags & 0x1C) != 0 && Native.IsWindowVisible((nint)s.Handle)) return true;
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0) return false;
        Native.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == s.ProcessId) return Class(foreground) is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName is "StartMenuExperienceHost" or "SearchHost" or "ShellExperienceHost" or "ShellHost";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public static bool Down(int key) => Native.GetAsyncKeyState(key) < 0;
}
