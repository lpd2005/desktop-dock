using DesktopDock.Core;
using System.Text.Json;

internal static class SpectrumChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var old = JsonSerializer.Deserialize<DockSettings>("{}"); old!.Normalize();
        check(!old.Spectrum.Enabled && old.Spectrum.Height == 48, "Old configurations keep audio capture disabled");
        var invalid = JsonSerializer.Deserialize<DockSettings>("{\"Spectrum\":null}"); invalid!.Normalize();
        check(invalid.Spectrum is not null, "Missing spectrum options recover independently");
        var options = new SpectrumOptions { Height = double.NaN, Sensitivity = double.PositiveInfinity }; options.Normalize();
        check(options.Height == 48 && options.Sensitivity == 1, "Nonfinite spectrum settings normalize");
        options.Height = 999; options.Sensitivity = -20; options.Normalize();
        check(options.Height == 80 && options.Sensitivity == .5, "Spectrum settings stay bounded");
        float[] silence = new float[48000 * 2], levels = new float[SpectrumAnalyzer.Bands];
        var quiet = new SpectrumAnalyzer(); quiet.Append(silence); quiet.CopyTo(levels);
        check(!quiet.Audible && quiet.Analyses == 0 && levels.All(x => x == 0), "Silence skips FFT and emits no activity");
        var peaks = new List<int>();
        foreach (int frequency in new[] { 110, 440, 1800, 7000 })
        {
            foreach (bool oppositePhase in new[] { false, true })
            {
                var analyzer = new SpectrumAnalyzer(); float[] tone = Tone(frequency, .1f, oppositePhase);
                for (int offset = 0; offset < tone.Length; offset += 960) analyzer.Append(tone.AsSpan(offset, Math.Min(960, tone.Length - offset)));
                analyzer.CopyTo(levels); int peak = Array.IndexOf(levels, levels.Max());
                if (!oppositePhase) peaks.Add(peak);
                check(analyzer.Audible && levels.Max() > .5 && levels.All(x => float.IsFinite(x) && x is >= 0 and <= 1), "Real spectral energy remains finite and visible");
                check(!oppositePhase || peak == peaks[^1], "Opposite-phase stereo retains spectral energy");
                check(analyzer.Analyses is >= 27 and <= 30, "Analysis rate is bounded by audio sample count");
                analyzer.Append(silence); analyzer.CopyTo(levels);
                check(!analyzer.Audible && levels.All(x => x == 0), "Silence clears previous frequency data");
            }
        }
        check(peaks.SequenceEqual(peaks.Order()), "Increasing tones move from low to high frequency bands");
        var saver = new SpectrumAnalyzer { AnalysisRate = 15 }; saver.Append(Tone(440, .1f));
        check(saver.Analyses is >= 13 and <= 15, "Energy saver halves FFT frequency");
        var dirty = new SpectrumAnalyzer(); float[] bad = Enumerable.Repeat(float.NaN, 5000).ToArray();
        dirty.Append(bad); dirty.CopyTo(levels); check(!dirty.Audible && levels.All(x => x == 0), "Nonfinite audio cannot poison the spectrum");
        foreach (double size in new[] { 32d, 48, 80 })
        foreach (double width in new[] { 320d, 1280, 3840 })
        foreach (int count in new[] { 2, 20, 42 })
        {
            var dock = DockLayout.Calculate(count, size, 1.8, null, width);
            var spectrum = SpectrumLayout.Calculate(dock, width, 350, 1.8, 80);
            check(spectrum.Count is >= 8 and <= 48 && spectrum.Left >= 0 && spectrum.Left + spectrum.Width <= width,
                "Spectrum stays centered within the screen");
            check(spectrum.Baseline + 12 <= dock.Baseline(350) - dock.IconSize * 1.8 + .001
                && spectrum.Baseline - spectrum.MaximumHeight >= 0, "Spectrum clears maximum magnification without clipping");
        }
        var allocation = new SpectrumAnalyzer(); float[] warm = Tone(440, .1f); allocation.Append(warm);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) { allocation.Append(warm); allocation.CopyTo(levels); }
        check(GC.GetAllocatedBytesForCurrentThread() == before, "Steady FFT and snapshots allocate no managed objects");
        var envelope = new SpectrumEnvelope(); Array.Fill(levels, .8f);
        envelope.Step(levels, 0, 1d / 30);
        check(envelope.Levels[0] is > 0 and < .8f && envelope.Opacity is > 0 and < 1, "Sound fades in with a bounded attack");
        for (int i = 0; i < 30; i++) envelope.Step(levels, 0, 1d / 30);
        float high = envelope.Levels[0]; envelope.Step(levels, .3, 1d / 30);
        check(envelope.Levels[0] < high && envelope.Levels[0] > 0, "Missing packets start a smooth release");
        for (int i = 0; i < 90; i++) envelope.Step(levels, 10, 1d / 30);
        check(envelope.Settled && envelope.Levels[0] < .001, "No further packets settles without a fake playback stream");
        envelope.Reset(); check(envelope.Opacity == 0 && envelope.Levels[0] == 0, "Hidden or disabled spectra discard stale envelopes");
        for (int count = 8; count <= SpectrumAnalyzer.Bands; count++)
        for (int band = 0; band < SpectrumAnalyzer.Bands; band++)
        {
            Array.Clear(levels); levels[band] = 1; float visible = 0;
            for (int i = 0; i < count; i++) visible = Math.Max(visible, SpectrumLayout.LevelForBar(levels, count, i));
            check(visible >= .7f, "Reducing bar count never discards a frequency band");
        }
    }

    private static float[] Tone(int frequency, float amplitude, bool opposite = false)
    {
        var samples = new float[48000 * 2];
        for (int i = 0; i < samples.Length / 2; i++)
        { float sample = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / 48000); samples[i * 2] = sample; samples[i * 2 + 1] = opposite ? -sample : sample; }
        return samples;
    }
}
