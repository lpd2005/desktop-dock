using System.Runtime.InteropServices;
using DesktopDock.Core;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

internal static class AppWindowPlacement
{
    public static WindowBounds Frame(nint hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0)
            Native.GetWindowRect(hwnd, out rect);
        return new(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static bool FitAboveDock(nint hwnd, WindowBounds available)
    {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.IsZoomed(hwnd)) return false;
        if (Forms.Screen.FromHandle(hwnd).DeviceName != Forms.Screen.PrimaryScreen!.DeviceName) return false;
        var frame = Frame(hwnd);
        var screen = Forms.Screen.FromHandle(hwnd).Bounds;
        if (WindowAvoidance.ShouldHide(frame, new(screen.Left, screen.Top, screen.Right, screen.Bottom), false, false)) return false;
        var fitted = WindowFitting.Fit(frame, available);
        if (frame == fitted) return true;
        Native.GetWindowRect(hwnd, out var outer);
        return Native.SetWindowPos(hwnd, 0, outer.Left + fitted.Left - frame.Left, outer.Top + fitted.Top - frame.Top,
            outer.Right - outer.Left + fitted.Right - fitted.Left - (frame.Right - frame.Left),
            outer.Bottom - outer.Top + fitted.Bottom - fitted.Top - (frame.Bottom - frame.Top), 0x4000 | 0x0004 | 0x0010 | 0x0200);
    }
}
