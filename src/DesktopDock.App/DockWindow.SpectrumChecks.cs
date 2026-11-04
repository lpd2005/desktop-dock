using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text;
using System.Text.Json;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class DockWindow
{
    internal async Task<Dictionary<string, bool>> CheckSpectrum(string data, bool inspect)
    {
        Directory.CreateDirectory(data);
        var checks = new Dictionary<string, bool>();
        void Require(bool condition, string name) { checks[name] = condition; if (!condition) throw new InvalidOperationException("Spectrum: " + name); }
        async Task Wait(Func<bool> condition, string name)
        {
            for (int i = 0; i < 160 && !condition(); i++) await Task.Delay(50);
            Require(condition(), name);
        }
        var original = settings.Spectrum; bool reduced = settings.ReducedMotion;
        bool? oldVisibility = visibilityForChecks;
        var oldItems = settings.Items.ToArray();
        try
        {
            PreparePerformance(); settings.ReducedMotion = false; settings.Spectrum = new(); ApplySettings();
            await WaitUntilShownForChecks(); await Task.Delay(350);
            Require(!Spectrum.Sampling && !Spectrum.Ticking && !renderer.Spectrum.Visible, "DefaultOffHasNoCaptureOrDrawLoop");
            var icons = renderer.IconIdentities.ToArray();
            Spectrum.Test(); await Task.Delay(400);
            Require(Spectrum.Testing && Spectrum.Ticking && renderer.Spectrum.Visible && Spectrum.Peak > .05, "SimulatedTestShowsAnimatedWhiteBars");
            Require(!Spectrum.Sampling, "SimulationDoesNotCaptureSystemAudio");
            Require(renderer.Spectrum.Count is >= 8 and <= 48, "BarCountAdaptsToDockWidth");
            var band = renderer.Spectrum.LayoutForChecks;
            double px = band.Left + band.Width / 2, py = band.Baseline - 5;
            Require(!footprint.Any(x => x.Contains(px, py)), "SpectrumDoesNotExpandDockInputRegion");
            nint point = new((((top + (int)(py * scale)) & 0xffff) << 16) | ((left + (int)(px * scale)) & 0xffff));
            Require(Native.SendMessage(visualSource.Handle, 0x0084, 0, point) == new nint(-1), "PresentationWindowRemainsMouseTransparent");
            Require(band.Baseline - band.MaximumHeight >= 0 && band.Baseline <= RestGeometry.Baseline(height / scale) - RestGeometry.IconSize * settings.Magnification - 11,
                "BarsStayAboveMaximumHoverAndInsideDrawingHost");
            await Task.Delay(450);
            Require(!rendering && renderer.IconIdentities.SequenceEqual(icons), "SpectrumDoesNotRunIconLayoutClockOrRecreateIcons");
            visibilityForChecks = false; CheckVisibility(); await Task.Delay(200);
            Require(!Spectrum.Ticking && !renderer.Spectrum.Visible, "HiddenDockStopsSpectrumAnimation");
            visibilityForChecks = true; CheckVisibility(); await Task.Delay(200);
            Require(Spectrum.Testing && renderer.Spectrum.Visible, "RevealResumesUnexpiredSimulation");
            settings.ReducedMotion = true; ApplySettings();
            Require(!Spectrum.Ticking && !Spectrum.Sampling && !renderer.Spectrum.Visible, "ReducedMotionPausesSpectrum");
            Spectrum.EndTest(); settings.ReducedMotion = false; ApplySettings();
            Require(!Spectrum.Testing && !Spectrum.Ticking, "EndingSimulationReturnsToDisabledState");
            settings.Spectrum.Enabled = true; ApplySettings();
            await Wait(() => Spectrum.Sampling, "RealWasapiLoopbackStartsOnDefaultOutput");
            using var tone = SpectrumTestTone(); using var player = new SoundPlayer(tone);
            player.Load(); player.PlayLooping();
            await Wait(() => Spectrum.AudioFrames > 5 && Spectrum.Peak > .05 && renderer.Spectrum.Visible, "ActualOutputAudioDrivesVisibleSpectrum");
            Require(!rendering && renderer.IconIdentities.SequenceEqual(icons), "LiveAudioLeavesIconLayoutIdle");
            int starts = Spectrum.CaptureStarts; Spectrum.ReconnectForChecks();
            await Wait(() => Spectrum.CaptureStarts > starts && Spectrum.Sampling, "CaptureReconnectsAfterSerializedDeviceChangeSignal");
            await Wait(() => Spectrum.Peak > .05, "ReconnectedOutputDrivesSpectrumAgain");
            visibilityForChecks = false; CheckVisibility();
            await Wait(() => !Spectrum.Sampling, "HidingDockReleasesAudioCapture");
            int frames = Spectrum.AudioFrames; await Task.Delay(300);
            Require(Spectrum.AudioFrames == frames && !Spectrum.Ticking, "HiddenDockDoesNoFurtherAnalysisOrDrawing");
            visibilityForChecks = true; CheckVisibility(); await Wait(() => Spectrum.Sampling && Spectrum.Ticking, "RevealingDockRestartsRealCapture");
            SetSuspended(true); await Wait(() => !Spectrum.Sampling, "DisplaySuspendReleasesCapture");
            Require(!Spectrum.Ticking && !renderer.Spectrum.Visible, "DisplaySuspendStopsPresentation");
            SetSuspended(false); await Wait(() => Spectrum.Sampling && Spectrum.Ticking, "DisplayResumeReconnectsAudioWithoutStaleInput");
            menuOpen = true; CheckVisibility(); await Wait(() => !Spectrum.Sampling, "ContextMenuPausesAudioAndSpectrum");
            menuOpen = false; CheckVisibility(); await Wait(() => Spectrum.Sampling, "ClosingMenuResumesCapture");
            settings.Spectrum.EnergySaver = true; ApplySettings();
            await Task.Delay(300); Require(Spectrum.Sampling, "EnergySaverAppliesWithoutLosingAudio");
            var measure = new List<object>();
            async Task Measure(string scenario)
            {
                using var process = Process.GetCurrentProcess();
                await Task.Delay(1000); process.Refresh();
                var startCpu = process.TotalProcessorTime; long allocated = GC.GetTotalAllocatedBytes(false);
                int draws = Spectrum.PresentedFrames, audio = Spectrum.AudioFrames;
                var watch = Stopwatch.StartNew(); var samples = new List<(double Working, double Private)>();
                for (int i = 0; i < 15; i++) { await Task.Delay(1000); process.Refresh(); samples.Add((process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d)); }
                measure.Add(new { Scenario = scenario, Seconds = watch.Elapsed.TotalSeconds,
                    CpuPercent = (process.TotalProcessorTime - startCpu).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
                    WorkingSetMiB = samples.Average(x => x.Working), PrivateMiB = samples.Average(x => x.Private),
                    AllocationBytesPerSecond = (GC.GetTotalAllocatedBytes(false) - allocated) / watch.Elapsed.TotalSeconds,
                    DrawUpdates = Spectrum.PresentedFrames - draws, AudioUpdates = Spectrum.AudioFrames - audio });
            }
            await Measure("live-energy-saver");
            settings.Spectrum.EnergySaver = false; ApplySettings(); await Measure("live-smooth");
            player.Stop(); settings.Spectrum.Enabled = false; ApplySettings(); await Wait(() => !Spectrum.Sampling, "DisablingReleasesCapture");
            await Measure("disabled-after-warmup");
            Require(!Spectrum.Ticking && !renderer.Spectrum.Visible, "DisabledSpectrumStaysIdle");
            File.WriteAllText(Path.Combine(data, "spectrum-resource-sample.json"), JsonSerializer.Serialize(new { ShortSample = true, LogicalProcessors = Environment.ProcessorCount,
                Notes = "Fifteen-second smoke samples in one warmed process; not a ten-minute baseline, GPU/FPS/power or cold dependency-memory measurement.", Samples = measure }, new JsonSerializerOptions { WriteIndented = true }));
            if (inspect)
            {
                long visualStyle = Native.GetWindowLongPtr(visualSource.Handle, -20).ToInt64();
                Native.SetWindowLongPtr(visualSource.Handle, -20, new((visualStyle | 0x40000L) & ~0x80L));
                settings.Items = Enumerable.Range(0, 14).Select(i => new DockItem("Example " + (i + 1), i % 2 == 0 ? "ms-settings:" : "shell:RecycleBinFolder", i.ToString())).ToList();
                await LoadIcons();
                string marker = Path.Combine(data, "visual-inspection.pause"); File.WriteAllText(marker, "Local spectrum and settings inspection.");
                for (int i = 0; i < 600 && File.Exists(marker); i++)
                { if (!Spectrum.Testing) Spectrum.Test(); await Task.Delay(500); }
            }
        }
        finally
        {
            menuOpen = false; SetSuspended(false); Spectrum.EndTest(); settings.Spectrum = original; settings.ReducedMotion = reduced;
            settings.Items = oldItems.ToList(); visibilityForChecks = oldVisibility; ApplySettings();
        }
        return checks;
    }

    private static MemoryStream SpectrumTestTone()
    {
        // One-second, low-level two-tone PCM fixture, looped only during this explicit integration check.
        var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);
        const int rate = 48000, bytes = rate * 4;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes);
        for (int i = 0; i < rate; i++)
        {
            short value = (short)(220 * Math.Sin(2 * Math.PI * 440 * i / rate) + 160 * Math.Sin(2 * Math.PI * 1800 * i / rate));
            writer.Write(value); writer.Write(value);
        }
        memory.Position = 0; return memory;
    }
}
