using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed record TaskbarTransparency(bool WasLayered, byte Alpha, uint ColorKey, uint Flags, bool WasTopmost = true);

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
        using var coordinates = new PhysicalCoordinates();
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
        using var coordinates = new PhysicalCoordinates();
        var rect = Rect(bounds);
        return Native.SystemParametersInfo(0x002F, 0, ref rect, 0); // Transient, without changing persisted system preferences.
    }
    public static TaskbarTransparency? CaptureTransparency(nint hwnd)
    {
        long styles = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
        bool layered = (styles & 0x80000) != 0;
        bool topmost = (styles & 8) != 0;
        if (!layered) return new(false, 255, 0, 2, topmost);
        return Native.GetLayeredWindowAttributes(hwnd, out uint key, out byte alpha, out uint flags)
            ? new(true, (flags & 2) != 0 ? alpha : (byte)255, key, flags, topmost) : null;
    }
    public static bool Present(nint hwnd, bool show, TaskbarTransparency original)
    {
        long styles = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
        if ((styles & 0x80000) == 0) Native.SetWindowLongPtr(hwnd, -20, new(styles | 0x80000));
        if (!Native.SetLayeredWindowAttributes(hwnd, original.ColorKey, show ? original.Alpha : (byte)0, original.Flags | 2)) return false;
        // Explorer can lower its taskbar behind a foreground full-screen window.
        // Only change stacking when recalled; never use SWP_SHOWWINDOW or change its rectangle.
        return !show || Native.SetWindowPos(hwnd, new(-1), 0, 0, 0, 0, 0x0013 | 0x0200 | 0x0400);
    }
    public static bool RestoreTransparency(nint hwnd, TaskbarTransparency original)
    {
        long styles = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
        bool restored;
        if (original.WasLayered) restored = Native.SetLayeredWindowAttributes(hwnd, original.ColorKey, original.Alpha, original.Flags);
        else
        {
            if ((styles & 0x80000) != 0) Native.SetWindowLongPtr(hwnd, -20, new(styles & ~0x80000L));
            restored = (Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80000) == 0;
        }
        if (((Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0) != original.WasTopmost)
            restored &= Native.SetWindowPos(hwnd, new(original.WasTopmost ? -1 : -2), 0, 0, 0, 0, 0x0013 | 0x0200 | 0x0400);
        return restored;
    }
    public static bool IsPresented(nint hwnd) => Native.IsWindowVisible(hwnd)
        && (!Native.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) || (flags & 2) == 0 || alpha != 0);
    public static bool Contains(WindowBounds r, Native.Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    public static bool ShellInteraction(TaskbarSnapshot s)
    {
        if (Down(0x5B) || Down(0x5C)) return true;
        var gui = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        // Keep a menu usable after the bar was revealed, but do not reveal a hidden
        // taskbar just because an unrelated application's context menu opened.
        if (Native.GetGUIThreadInfo(0, ref gui) && (gui.Flags & 0x1C) != 0 && IsPresented((nint)s.Handle)) return true;
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
    // CLI recovery and the guard do not start WPF; a dotnet-hosted process otherwise
    // sees virtualized rectangles at 150% scale while its journal contains physical pixels.
    private sealed class PhysicalCoordinates : IDisposable
    {
        private readonly nint previous = Native.SetThreadDpiAwarenessContext(new(-4));
        public void Dispose() { if (previous != 0) Native.SetThreadDpiAwarenessContext(previous); }
    }
}
