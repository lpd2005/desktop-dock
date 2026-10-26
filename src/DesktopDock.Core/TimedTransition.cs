namespace DesktopDock.Core;

public sealed class TimedTransition(double initial = 0)
{
    private double from = initial, to = initial, started, duration;
    public double Value { get; private set; } = initial;
    public double Target => to;
    public bool Active { get; private set; }
    public void Begin(double target, double now, double seconds)
    {
        Sample(now);
        if (to == target && Active) return;
        from = Value; to = target; started = now;
        duration = Math.Max(.001, seconds * Math.Abs(to - from));
        Active = Math.Abs(to - from) > .00001;
        if (!Active) Value = target;
    }
    public double Sample(double now)
    {
        if (!Active) return Value;
        double t = Math.Clamp((now - started) / duration, 0, 1);
        Value = from + (to - from) * (1 - Math.Pow(1 - t, 3));
        if (t >= 1) { Value = to; Active = false; }
        return Value;
    }
    public void Reset(double value) { Value = from = to = value; Active = false; }
}
