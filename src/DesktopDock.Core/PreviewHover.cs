namespace DesktopDock.Core;

// A short dwell avoids opening previews while crossing icons. The leave grace
// covers the physical gap between the Dock and the preview without an input overlay.
public sealed class PreviewHover
{
    private int candidate = -1;
    private double entered, left = double.PositiveInfinity;
    public int Current { get; private set; } = -1;
    public int Update(int hovered, bool overPreview, bool allowed, double now)
    {
        if (!allowed) { Reset(); return Current; }
        if (hovered >= 0)
        {
            left = double.PositiveInfinity;
            if (candidate != hovered) { candidate = hovered; entered = now; }
            if (now - entered >= .4) Current = hovered;
        }
        else if (overPreview && Current >= 0)
        {
            candidate = Current; left = double.PositiveInfinity;
        }
        else
        {
            candidate = -1;
            if (double.IsPositiveInfinity(left)) left = now;
            if (now - left >= .35) Current = -1;
        }
        return Current;
    }
    public void Reset() { candidate = Current = -1; left = double.PositiveInfinity; }
}
