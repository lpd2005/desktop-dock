namespace DesktopDock.Core;

/// <summary>Exact critically damped solution. Independent of frame rate and event frequency.</summary>
public sealed class SpringMotion(double initial = 0)
{
    public double Value { get; private set; } = initial;
    public double Velocity { get; private set; }
    public void Reset(double value) { Value = value; Velocity = 0; }
    public bool Step(double target, double deltaSeconds, double frequency)
    {
        double t = Math.Clamp(deltaSeconds, 0, .05);
        double displacement = Value - target;
        double j = Velocity + frequency * displacement;
        double decay = Math.Exp(-frequency * t);
        Value = target + (displacement + j * t) * decay;
        Velocity = (Velocity - frequency * j * t) * decay;
        bool active = Math.Abs(Value - target) > .0005 || Math.Abs(Velocity) > .005;
        if (!active) Reset(target);
        return active;
    }
}
