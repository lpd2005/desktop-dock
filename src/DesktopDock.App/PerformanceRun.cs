using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed record PerformanceOptions(int Count, int Seconds, int Warmup, string Profile, string Label)
{
    internal static PerformanceOptions Parse(string[] args)
    {
        string Value(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        int Number(string name, int fallback, int min, int max) => int.TryParse(Value(name, fallback.ToString()), out int value) && value >= min && value <= max
            ? value : throw new ArgumentException("Invalid performance option: " + name);
        string profile = Value("--perf-profile", "base"), label = Value("--perf-label", "sample");
        if (profile is not ("base" or "widgets" or "previews" or "glass") || label.Length is 0 or > 40 || label.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_'))
            throw new ArgumentException("Invalid performance profile or label.");
        return new(Number("--perf-count", 20, 1, 40), Number("--perf-seconds", 600, 2, 7200), Number("--perf-warmup", 30, 0, 300), profile, label);
    }
    internal void Prepare(string data)
    {
        if (Directory.EnumerateFileSystemEntries(data).Any()) throw new IOException("Performance runs require a new empty data directory.");
        string fixture = Path.Combine(data, "fixture"); Directory.CreateDirectory(fixture);
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string[] targets = [Environment.ProcessPath!, Path.Combine(system, "notepad.exe"), Path.Combine(system, "cmd.exe"), Path.Combine(system, "control.exe"),
            Path.Combine(system, "taskmgr.exe"), Path.Combine(system, "mmc.exe"), Path.Combine(system, "mspaint.exe"), Path.Combine(system, "charmap.exe")];
        var settings = new DockSettings { HideDesktopIcons = false, AutoCheckUpdates = false };
        settings.Widgets.Clock = settings.Widgets.Weather = Profile == "widgets";
        for (int i = 0; i < Count; i++)
        {
            string shortcut = Path.Combine(fixture, $"Example-{i:00}.lnk");
            RegressionProbe.CreateShortcut(shortcut, targets[i % targets.Length], "");
            settings.Items.Add(new("Example " + i, shortcut));
        }
        SettingsStore.Save(Path.Combine(data, "settings.json"), settings);
    }
}

internal sealed partial class Controller
{
    private sealed record PerformanceSample(double Seconds, double CpuPercent, double WorkingSetMiB, double PrivateMiB, long AllocatedBytes,
        int Handles, int Threads, int AppStateScans, long OcclusionScans);

    public async Task RunPerformance(PerformanceOptions options)
    {
        var children = new List<Process>();
        using var process = Process.GetCurrentProcess();
        try
        {
            dock.PreparePerformance();
            if (options.Profile == "widgets") widgets.Apply(); // Preview mode uses no network.
            if (options.Profile == "glass") { settings.TaskbarEnhancement = true; taskbar.Apply(settings); if (!taskbar.Active) throw new InvalidOperationException(taskbar.Status); }
            if (options.Profile == "previews")
            {
                for (int i = 0; i < 2; i++)
                {
                    string directory = Path.Combine(data, "preview-" + i);
                    var child = RegressionProbe.Start("preview", directory, lifetimeSeconds: options.Seconds + options.Warmup + 90); children.Add(child);
                    await RegressionProbe.WaitReady(child, directory);
                }
                dock.PerformancePreview();
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Warmup));
            dock.ClearFrameSamples(); process.Refresh();
            var clock = Stopwatch.StartNew(); var cpu = process.TotalProcessorTime; var startingCpu = cpu; double lastSeconds = 0;
            var startingCounters = dock.PerformanceCounters;
            long allocated = GC.GetTotalAllocatedBytes(false);
            var samples = new List<PerformanceSample>();
            File.WriteAllText(Path.Combine(data, "performance-ready.json"), "{\"Ready\":true}");
            while (clock.Elapsed.TotalSeconds < options.Seconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Seconds - clock.Elapsed.TotalSeconds, .001, 1)));
                process.Refresh(); double seconds = clock.Elapsed.TotalSeconds; var nextCpu = process.TotalProcessorTime;
                samples.Add(new(seconds, (nextCpu - cpu).TotalSeconds / (seconds - lastSeconds) / Environment.ProcessorCount * 100,
                    process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d, GC.GetTotalAllocatedBytes(false) - allocated,
                    process.HandleCount, process.Threads.Count, dock.PerformanceCounters.AppStates - startingCounters.AppStates, dock.PerformanceCounters.Occlusion - startingCounters.Occlusion));
                cpu = nextCpu; lastSeconds = seconds;
            }
            long idleFrames = dock.FrameIntervals.Count;
            dock.ClearFrameSamples();
            for (int i = 0; i < 6; i++) { dock.PreviewWave(i % 2 == 0 ? .25 : .75); await Task.Delay(300); }
            dock.ResetWave(); await Task.Delay(350);
            var frames = dock.FrameIntervals.Skip(2).OrderBy(x => x).ToArray();
            process.Refresh(); double beforeExercise = process.PrivateMemorySize64 / 1048576d;
            for (int i = 0; i < 10; i++) { OpenSettings(); await Task.Delay(60); settingsWindow?.Close(); await Task.Delay(40); }
            await Task.Delay(1000); process.Refresh();
            var report = new
            {
                Version = AppVersion.Current, options.Label, options.Profile, PinnedIcons = options.Count, TotalIcons = dock.IconCount,
                SampleSeconds = lastSeconds, WarmupSeconds = options.Warmup, ShortSample = lastSeconds < 600, LogicalProcessors = Environment.ProcessorCount,
                PrimaryWidth = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width, PrimaryHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height,
                DpiScale = Native.GetDpiForWindow(dock.Handle) / 96d,
                WindowsBuild = Environment.OSVersion.Version.ToString(), dock.HardwareRendering, IdleAnimationUpdates = idleFrames,
                CpuMeanPercent = (cpu - startingCpu).TotalSeconds / lastSeconds / Environment.ProcessorCount * 100,
                WorkingSetMeanMiB = samples.Average(x => x.WorkingSetMiB), PrivateMeanMiB = samples.Average(x => x.PrivateMiB),
                AllocatedBytesPerSecond = samples[^1].AllocatedBytes / lastSeconds,
                PrivateGrowthMiB = samples[^1].PrivateMiB - samples[0].PrivateMiB,
                ExercisePrivateDeltaMiB = process.PrivateMemorySize64 / 1048576d - beforeExercise,
                AnimationUpdateMedianMs = frames.Length == 0 ? (double?)null : frames[frames.Length / 2],
                AnimationUpdateP95Ms = frames.Length == 0 ? (double?)null : frames[(int)((frames.Length - 1) * .95)],
                Samples = samples,
                Notes = "Synthetic shortcuts sharing eight executable identities; preview weather has no network. CPU is normalized to all logical processors. App animation updates are not GPU presentation. Memory after ten settings cycles is not a long-run leak verdict."
            };
            File.WriteAllText(Path.Combine(data, "performance-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { settingsWindow?.Close(); foreach (var child in children) { RegressionProbe.Close(child); child.Dispose(); } }
        Quit();
    }
}

internal sealed partial class DockWindow
{
    private long occlusionScans;
    internal (int AppStates, long Occlusion) PerformanceCounters => (appStateScans, occlusionScans);
    internal void PreparePerformance()
    {
        IsolateInputForChecks(true);
        visibilityForChecks = true; pointerOverForChecks = false; bottomEdgeForChecks = false;
        CheckVisibility();
    }
    internal void PerformancePreview() { pointerOverForChecks = true; previewPointerForChecks = true; hoveredIndex = 1; CheckVisibility(); }
}
