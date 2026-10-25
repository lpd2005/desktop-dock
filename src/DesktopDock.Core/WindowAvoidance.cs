namespace DesktopDock.Core;

public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom);
public static class WindowAvoidance
{
    public static bool ShouldHide(WindowBounds window, WindowBounds monitor, bool maximized, bool hideMaximized)
    {
        // A maximized window on another monitor must not hide this Dock.
        bool overlaps = window.Right > monitor.Left && window.Left < monitor.Right && window.Bottom > monitor.Top && window.Top < monitor.Bottom;
        if (!overlaps) return false;
        bool covers = window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2
            && window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2;
        return covers || maximized && hideMaximized;
    }
}
