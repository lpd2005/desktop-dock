namespace DesktopDock.Core;

public sealed class SpectrumEnvelope
{
    private readonly float[] levels = new float[SpectrumAnalyzer.Bands];
    public ReadOnlySpan<float> Levels => levels;
    public float Opacity { get; private set; }
    public bool Settled => Opacity < .005f;

    public void Step(ReadOnlySpan<float> input, double soundAge, double dt)
    {
        dt = double.IsFinite(dt) ? Math.Clamp(dt, .001, .2) : .033;
        for (int i = 0; i < levels.Length; i++)
        {
            float value = soundAge < .18 && float.IsFinite(input[i]) ? Math.Clamp(input[i], 0, 1) : 0;
            levels[i] += (float)((value - levels[i]) * (1 - Math.Exp(-dt / (value > levels[i] ? .035 : .17))));
        }
        Opacity += (float)(((soundAge < .65 ? 1 : 0) - Opacity) * (1 - Math.Exp(-dt / .13)));
    }
    public void Reset() { Opacity = 0; Array.Clear(levels); }
}
