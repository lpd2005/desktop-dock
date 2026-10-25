namespace DesktopDock.Core;

public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom);
public static class WindowAvoidance
{
    public static bool Overlaps(WindowBounds window, WindowBounds dock)
        => window.Right > dock.Left && window.Left < dock.Right && window.Bottom > dock.Top && window.Top < dock.Bottom;
}
