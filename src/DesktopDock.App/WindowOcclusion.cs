using System.Runtime.InteropServices;
using System.Text;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class WindowOcclusion(nint dock)
{
    internal Func<IEnumerable<nint>>? WindowsForChecks { get; set; }
    public static WindowBounds Frame(nint hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0)
            Native.GetWindowRect(hwnd, out rect);
        return new(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public bool Covered(WindowBounds area)
    {
        if (area.Right <= area.Left || area.Bottom <= area.Top) return false;
        bool result = false;
        bool Inspect(nint hwnd)
        {
            if (hwnd == dock || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
            if (Native.DwmGetIntAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
            long extended = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            // Own menus/tooltips must not hide the Dock; a captioned preferences window can cover it.
            if (pid == Environment.ProcessId && (style & 0x00C00000) == 0) return false;
            if (pid != Environment.ProcessId && (extended & 0x08000000) != 0) return false;
            var name = new StringBuilder(128);
            Native.GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            return WindowAvoidance.Overlaps(Frame(hwnd), area);
        }
        if (WindowsForChecks is not null) return WindowsForChecks().Any(Inspect);
        Native.EnumWindows((hwnd, _) => { result = Inspect(hwnd); return !result; }, 0);
        return result;
    }
}
