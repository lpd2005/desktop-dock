namespace DesktopDock.Core;

// Accumulate high-resolution wheel deltas and limit a trackpad burst to one
// page at a time. Direction follows the usual previous / next page convention.
public sealed class PageWheel
{
    private int remainder;
    private double lastInput = double.NegativeInfinity, lastPage = double.NegativeInfinity;
    public int Update(int delta, double now)
    {
        if (now - lastInput > .4) remainder = 0;
        lastInput = now;
        remainder = (int)Math.Clamp((long)remainder + delta, -480L, 480L);
        if (Math.Abs(remainder) < 120) return 0;
        int direction = -Math.Sign(remainder); remainder = 0;
        if (now - lastPage < .18) return 0;
        lastPage = now; return direction;
    }
}
