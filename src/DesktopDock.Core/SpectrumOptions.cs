namespace DesktopDock.Core;

public sealed class SpectrumOptions
{
    public bool Enabled { get; set; }
    public double Height { get; set; } = 48;
    public double Sensitivity { get; set; } = 1;
    public bool EnergySaver { get; set; }

    public void Normalize()
    {
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 16, 80) : 48;
        Sensitivity = double.IsFinite(Sensitivity) ? Math.Clamp(Sensitivity, .5, 2) : 1;
    }
}

public readonly record struct SpectrumLayout(int Count, double Left, double Baseline, double Width, double MaximumHeight)
{
    public static float LevelForBar(ReadOnlySpan<float> levels, int count, int index)
    {
        if (levels.Length != SpectrumAnalyzer.Bands || count is < 8 or > SpectrumAnalyzer.Bands || index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(count));
        int begin = index * levels.Length / count, end = (index + 1) * levels.Length / count;
        float level = 0;
        for (int band = begin; band < end; band++)
            level = Math.Max(level, levels[band] * .7f + levels[Math.Max(0, band - 1)] * .15f + levels[Math.Min(levels.Length - 1, band + 1)] * .15f);
        return level;
    }

    public static SpectrumLayout Calculate(DockGeometry dock, double availableWidth, double availableHeight,
        double magnification, double requestedHeight)
    {
        double span = Math.Min(Math.Min(720, Math.Max(0, availableWidth - 16)), Math.Max(0, dock.Width - DockGeometry.Padding * 2));
        // The resting baseline and maximum hover envelope are stable, independent of the pointer.
        double baseline = dock.Baseline(availableHeight) - dock.IconSize * magnification - 12;
        return new(Math.Clamp((int)(span / 13), 8, SpectrumAnalyzer.Bands), (availableWidth - span) / 2,
            baseline, span, Math.Max(0, Math.Min(requestedHeight, baseline - 6)));
    }
}
