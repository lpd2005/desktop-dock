namespace DesktopDock.Core;

public static class WindowFitting
{
    public static WindowBounds Fit(WindowBounds window, WindowBounds available)
    {
        int width = Math.Clamp(window.Right - window.Left, 1, Math.Max(1, available.Right - available.Left));
        int height = Math.Clamp(window.Bottom - window.Top, 1, Math.Max(1, available.Bottom - available.Top));
        int x = Math.Clamp(window.Left, available.Left, available.Right - width);
        int y = Math.Clamp(window.Top, available.Top, available.Bottom - height);
        return new(x, y, x + width, y + height);
    }
}

public static class LaunchFeedback
{
    public const double Duration = .38;
    public static double Lift(double elapsed)
    {
        if (elapsed <= 0 || elapsed >= Duration) return 0;
        double t = elapsed / Duration;
        return Math.Sin(Math.PI * t) * Math.Exp(-1.8 * t) * 18;
    }
}
