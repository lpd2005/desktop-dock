using System.Numerics;

namespace DesktopDock.Core;

// Fixed working storage. Both stereo channels contribute energy, including opposite-phase material.
// The caller serializes access. No PCM, titles or device identifiers leave this in-memory analyzer.
public sealed class SpectrumAnalyzer
{
    public const int Bands = 48;
    public const int Size = 2048;
    private readonly float[] left = new float[Size], right = new float[Size], window = new float[Size];
    private readonly Complex[] first = new Complex[Size], second = new Complex[Size];
    private readonly double[] power = new double[Size / 2];
    private readonly float[] levels = new float[Bands];
    private readonly int sampleRate;
    private readonly double windowSum;
    private int position, filled, sinceAnalysis;
    public int Analyses { get; private set; }
    public bool Audible { get; private set; }
    public int AnalysisRate { get; set; } = 30;

    public SpectrumAnalyzer(int sampleRate = 48000)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
        for (int i = 0; i < Size; i++) { window[i] = (float)(.5 - .5 * Math.Cos(2 * Math.PI * i / (Size - 1))); windowSum += window[i]; }
    }

    public bool Append(ReadOnlySpan<float> samples, int channels = 2)
    {
        if (channels is < 1 or > 2 || samples.Length % channels != 0) throw new ArgumentException("Expected complete mono or stereo frames.");
        bool changed = false;
        int hop = sampleRate / Math.Clamp(AnalysisRate, 10, 30);
        for (int i = 0; i < samples.Length; i += channels)
        {
            left[position] = Finite(samples[i]); right[position] = Finite(samples[i + channels - 1]);
            position = (position + 1) % Size; filled = Math.Min(Size, filled + 1);
            if (++sinceAnalysis < hop || filled < Size) continue;
            sinceAnalysis = 0; Analyze(); changed = true;
        }
        return changed;
    }

    private static float Finite(float value) => float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;

    private void Analyze()
    {
        double energy = 0;
        for (int i = 0; i < Size; i++)
        {
            int source = (position + i) % Size;
            float a = left[source], b = right[source]; energy += a * a + b * b;
            first[i] = new(a * window[i], 0); second[i] = new(b * window[i], 0);
        }
        Audible = energy / (Size * 2) > 1e-8;
        if (!Audible) { Array.Clear(levels); return; } // Silence needs no FFT.
        Transform(first); Transform(second); Analyses++;
        double normalization = 2 / (windowSum * windowSum);
        for (int i = 1; i < power.Length; i++)
            power[i] = (first[i].Magnitude * first[i].Magnitude + second[i].Magnitude * second[i].Magnitude) * normalization;
        double upper = Math.Min(16000, sampleRate * .48);
        for (int band = 0; band < Bands; band++)
        {
            int begin = Math.Clamp((int)(50 * Math.Pow(upper / 50, band / (double)Bands) * Size / sampleRate), 1, power.Length - 1);
            int end = Math.Clamp((int)Math.Ceiling(50 * Math.Pow(upper / 50, (band + 1d) / Bands) * Size / sampleRate), begin + 1, power.Length);
            double peak = 0;
            for (int i = begin; i < end; i++) peak = Math.Max(peak, power[i]);
            levels[band] = (float)Math.Clamp((10 * Math.Log10(Math.Max(peak, 1e-12)) + 70) / 62, 0, 1);
        }
    }

    public void CopyTo(Span<float> destination, double sensitivity = 1)
    {
        if (destination.Length < Bands) throw new ArgumentException("A complete spectrum is required.");
        sensitivity = double.IsFinite(sensitivity) ? Math.Clamp(sensitivity, .5, 2) : 1;
        for (int i = 0; i < Bands; i++) destination[i] = (float)Math.Clamp(levels[i] * sensitivity, 0, 1);
    }

    private static void Transform(Complex[] values)
    {
        for (int i = 1, j = 0; i < Size; i++)
        {
            int bit = Size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= Size; length <<= 1)
        {
            var increment = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < Size; start += length)
            {
                Complex factor = Complex.One;
                for (int i = 0; i < length / 2; i++)
                {
                    var a = values[start + i]; var b = values[start + i + length / 2] * factor;
                    values[start + i] = a + b; values[start + i + length / 2] = a - b; factor *= increment;
                }
            }
        }
    }
}
