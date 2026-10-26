using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using DesktopDock.Core;

namespace DesktopDock.App;

internal static class TaskbarChecks
{
    public static int Run(string data)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new Dictionary<string, bool>();
        using var enhancement = new TaskbarEnhancement(data);
        var before = TaskbarNative.Capture() ?? throw new InvalidOperationException("Taskbar unavailable.");
        var transparencyBefore = TaskbarNative.CaptureTransparency((nint)before.Handle);
        if (!before.IsTop) throw new InvalidOperationException("Move primary taskbar to top before this check.");
        app.Startup += async (_, _) =>
        {
            try
            {
                void Require(bool condition, string description) { checks[description] = condition; if (!condition) throw new InvalidOperationException(description); }
                var settings = new DockSettings { TaskbarEnhancement = true };
                enhancement.InputForChecks = () => (false, false, false, false);
                enhancement.Apply(settings);
                Require(enhancement.Active && enhancement.GuardRunning, "GuardReadyBeforeHiding");
                await Task.Delay(1600);
                enhancement.TickForChecks();
                Require(!TaskbarNative.IsPresented((nint)before.Handle), "ActualTaskbarHidden");
                var released = TaskbarNative.Capture()!;
                Require(released.Frame == before.Frame, "NativeTaskbarNotMoved");
                Require(!before.CanReleaseTop || released.Work == before.ExpandedWork, "TopWorkAreaReleased");
                Require(enhancement.MotionFrames > 0 && !enhancement.MotionActive, "TransitionRendersAndReachesRest");
                foreach (string mode in new[] { "maximized", "fullscreen" })
                {
                    string stableData = Path.Combine(data, "stable-" + mode);
                    using var stableProbe = RegressionProbe.Start(mode, stableData);
                    try
                    {
                        await RegressionProbe.WaitReady(stableProbe, stableData);
                        nint probeWindow = RegressionProbe.Handle(stableData);
                        var original = WindowOcclusion.Frame(probeWindow);
                        string metrics = Path.Combine(stableData, "window-metrics.json");
                        int ResizeEvents()
                        {
                            using var document = JsonDocument.Parse(File.ReadAllText(metrics));
                            return document.RootElement.GetProperty("ResizeEvents").GetInt32();
                        }
                        int initialResizes = ResizeEvents();
                        nint HitRoot()
                        {
                            var point = new Native.Point { X = before.Frame.Left + (before.Frame.Right - before.Frame.Left) * 3 / 4,
                                Y = before.Frame.Top + (before.Frame.Bottom - before.Frame.Top) / 2 };
                            return Native.GetAncestor(Native.WindowFromPoint(point), 2);
                        }
                        Require(HitRoot() != (nint)before.Handle, mode + "HiddenTaskbarDoesNotBlockInput");
                        enhancement.InputForChecks = () => (false, false, false, false);
                        enhancement.Reveal();
                        await Task.Delay(150);
                        Require(HitRoot() == (nint)before.Handle, mode + "RecalledTaskbarAcceptsInputAboveWindow");
                        bool stable = true, stableWork = true;
                        for (int i = 0; i < 230; i++)
                        {
                            enhancement.TickForChecks();
                            stable &= WindowOcclusion.Frame(probeWindow) == original;
                            stableWork &= TaskbarNative.Capture()?.Work == released.Work;
                            await Task.Delay(16);
                        }
                        // A second cycle uses the actual dwell/leave state machine instead of manual recall.
                        for (int i = 0; i < 100; i++)
                        {
                            bool edge = i < 25;
                            enhancement.InputForChecks = () => (edge, false, false, false);
                            enhancement.TickForChecks();
                            stable &= WindowOcclusion.Frame(probeWindow) == original;
                            stableWork &= TaskbarNative.Capture()?.Work == released.Work;
                            await Task.Delay(16);
                        }
                        enhancement.InputForChecks = () => (false, false, false, false);
                        Require(stable && ResizeEvents() == initialResizes, mode + "UnchangedAcrossRecallAndHide");
                        Require(stableWork, mode + "WorkAreaStableAcrossRecallAndHide");
                        Require(Native.IsWindowVisible((nint)before.Handle) == before.WasVisible, mode + "NativeVisibleStyleUnchanged");
                        Require(HitRoot() != (nint)before.Handle, mode + "HiddenAgainTaskbarDoesNotBlockInput");
                    }
                    finally { RegressionProbe.Close(stableProbe); }
                }
                string probeData = Path.Combine(data, "maximize-probe");
                using (var probe = RegressionProbe.Start("maximized", probeData))
                {
                    try
                    {
                        await RegressionProbe.WaitReady(probe, probeData);
                        var frame = WindowOcclusion.Frame(RegressionProbe.Handle(probeData));
                        Require(!before.CanReleaseTop || frame.Top <= before.Monitor.Top + 2, "NewMaximizedWindowUsesReleasedTop");
                    }
                    finally { RegressionProbe.Close(probe); }
                }
                enhancement.InputForChecks = () => (true, false, false, false);
                enhancement.TickForChecks(); await Task.Delay(550); enhancement.TickForChecks();
                Require(TaskbarNative.IsPresented((nint)before.Handle), "EdgeDwellRevealedActualTaskbar");
                enhancement.InputForChecks = () => (false, false, true, false);
                await Task.Delay(1000); enhancement.TickForChecks();
                Require(TaskbarNative.IsPresented((nint)before.Handle), "ShellInteractionKeepsTaskbarVisible");
                enhancement.InputForChecks = () => (false, false, false, true);
                enhancement.TickForChecks();
                Require(!TaskbarNative.IsPresented((nint)before.Handle), "TaskSwitchDoesNotRevealTaskbar");
                enhancement.InputForChecks = () => (false, false, false, false);
                enhancement.Reveal(); enhancement.TickForChecks();
                await Task.Delay(280); enhancement.TickForChecks();
                Require(TaskbarNative.IsPresented((nint)before.Handle), "ManualRecallWorks");
                await Task.Delay(3200); enhancement.TickForChecks();
                for (int i = 0; i < 20 && enhancement.MotionActive; i++) await Task.Delay(25);
                Require(!TaskbarNative.IsPresented((nint)before.Handle), "ManualRecallTimeout");
                int settledFrames = enhancement.MotionFrames;
                await Task.Delay(150);
                Require(!enhancement.MotionActive && enhancement.MotionFrames == settledFrames, "TransitionClockStopsWhenIdle");
                var motionInfo = new { enhancement.MotionFrames, enhancement.SlideUsed };
                Require(enhancement.GlassRunning, "OwnedGlassProcessRunning");
                uint glassPid = 0;
                nint glassWindow = Native.FindWindow("TrayWindow", "TranslucentTB");
                if (glassWindow != 0) Native.GetWindowThreadProcessId(glassWindow, out glassPid);
                Require(glassWindow != 0 && glassPid != 0, "OfficialGlassComponentInitialized");
                using var ownProcess = Process.GetCurrentProcess();
                using var glassProcess = Process.GetProcessById((int)glassPid);
                using var guardProcess = Process.GetProcessById(enhancement.GuardProcessId);
                ownProcess.Refresh(); var startCpu = ownProcess.TotalProcessorTime; var watch = Stopwatch.StartNew();
                var glassCpu = glassProcess.TotalProcessorTime;
                var guardCpu = guardProcess.TotalProcessorTime;
                await Task.Delay(3000); ownProcess.Refresh();
                glassProcess.Refresh(); guardProcess.Refresh();
                double idleCpu = (ownProcess.TotalProcessorTime - startCpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                var workers = new
                {
                    GlassWorkingSetMiB = glassProcess.WorkingSet64 / 1048576d,
                    GuardWorkingSetMiB = guardProcess.WorkingSet64 / 1048576d,
                    GlassCpuPercent = (glassProcess.TotalProcessorTime - glassCpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                    GuardCpuPercent = (guardProcess.TotalProcessorTime - guardCpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
                };
                enhancement.Stop(); await Task.Delay(400);
                var after = TaskbarNative.Capture()!;
                Require(TaskbarNative.IsPresented((nint)before.Handle) == before.WasVisible, "OriginalVisibilityRestored");
                Require(after.Work == before.Work, "OriginalWorkAreaRestored");
                Require(TaskbarNative.CaptureTransparency((nint)before.Handle) == transparencyBefore, "OriginalTransparencyAndStackingRestored");
                Require(!File.Exists(TaskbarRecovery.Journal(data)), "RecoveryJournalCleared");
                Require(!TaskbarNative.SameProcess(glassPid, TaskbarNative.ProcessStart(glassPid)), "OwnedGlassProcessStopped");
                string crashData = Path.Combine(data, "guard-probe");
                Directory.CreateDirectory(crashData);
                using (var crashing = Process.Start(SelfProcess.Create("--taskbar-exit-probe", "--data-dir", crashData))!)
                {
                    await crashing.WaitForExitAsync();
                    Require(crashing.ExitCode == 73, "ProbeExitedWithoutCleanup");
                    for (int i = 0; i < 80 && File.Exists(TaskbarRecovery.Journal(crashData)); i++) await Task.Delay(100);
                    Require(!File.Exists(TaskbarRecovery.Journal(crashData)) && TaskbarNative.Capture()?.Work == before.Work
                        && TaskbarNative.IsPresented((nint)before.Handle) == before.WasVisible, "GuardRestoredAfterParentDied");
                    Require(TaskbarNative.CaptureTransparency((nint)before.Handle) == transparencyBefore, "GuardRestoredTransparencyAndStacking");
                }
                File.WriteAllText(Path.Combine(data, "taskbar-checks.json"), JsonSerializer.Serialize(new
                {
                    Version = "0.1.0-preview.8", Checks = checks, Motion = motionInfo, IdleCpuPercent = idleCpu, Workers = workers,
                    WorkingSetMiB = ownProcess.WorkingSet64 / 1048576d,
                    Notes = "Actual primary top taskbar; controlled edge/menu state inputs, no synthetic mouse or key input. Glass process initialization is not pixel-level visual verification."
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(0);
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(data, "taskbar-check-error.txt"), e + "\n" + JsonSerializer.Serialize(new { Checks = checks, Before = before, Current = TaskbarNative.Capture(), enhancement.Status, enhancement.GlassStatus }));
                app.Shutdown(1);
            }
            finally { enhancement.Stop(); }
        };
        return app.Run();
    }

    public static int RunExitProbe(string data)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var enhancement = new TaskbarEnhancement(data) { InputForChecks = () => (false, false, false, false) };
        app.Startup += async (_, _) =>
        {
            enhancement.Apply(new DockSettings { TaskbarEnhancement = true });
            await Task.Delay(1600);
            if (!enhancement.Active || !enhancement.GuardRunning) { enhancement.Stop(); app.Shutdown(1); return; }
            // Deliberately bypass Dispose/finally, without creating a crash dump or an error dialog.
            Environment.Exit(73);
        };
        return app.Run();
    }
}
