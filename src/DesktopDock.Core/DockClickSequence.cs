namespace DesktopDock.Core;

// Records completed clicks without a timer or a delay for already-open applications.
public sealed class DockClickSequence
{
    private string? previousKey;
    private double previousTime;
    private int previousX, previousY;

    public bool Complete(string key, double pressedAt, int x, int y, double interval, int width, int height)
    {
        bool twice = previousKey == key && pressedAt >= previousTime && pressedAt - previousTime <= interval
            && Math.Abs(x - previousX) <= width / 2d && Math.Abs(y - previousY) <= height / 2d;
        if (twice) { Reset(); return true; }
        previousKey = key; previousTime = pressedAt; previousX = x; previousY = y;
        return false;
    }

    public void Reset() => previousKey = null;
}
