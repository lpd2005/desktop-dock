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
                Require(!Native.IsWindowVisible((nint)before.Handle), "ActualTaskbarHidden");
                var released = TaskbarNative.Capture()!;
                Require(released.Frame == before.Frame, "NativeTaskbarNotMoved");
                Require(!before.CanReleaseTop || released.Work == before.ExpandedWork, "TopWorkAreaReleased");
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
                enhancement.TickForChecks(); await Task.Delay(300); enhancement.TickForChecks();
                Require(Native.IsWindowVisible((nint)before.Handle), "EdgeDwellRevealedActualTaskbar");
                enhancement.InputForChecks = () => (false, false, true, false);
                await Task.Delay(1000); enhancement.TickForChecks();
                Require(Native.IsWindowVisible((nint)before.Handle), "ShellInteractionKeepsTaskbarVisible");
                enhancement.InputForChecks = () => (false, false, false, true);
                enhancement.TickForChecks();
                Require(!Native.IsWindowVisible((nint)before.Handle), "TaskSwitchDoesNotRevealTaskbar");
                enhancement.InputForChecks = () => (false, false, false, false);
                enhancement.Reveal(); enhancement.TickForChecks();
                Require(Native.IsWindowVisible((nint)before.Handle), "ManualRecallWorks");
                await Task.Delay(3200); enhancement.TickForChecks();
                Require(!Native.IsWindowVisible((nint)before.Handle), "ManualRecallTimeout");
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
                Require(Native.IsWindowVisible((nint)before.Handle) == before.WasVisible, "OriginalVisibilityRestored");
                Require(after.Work == before.Work, "OriginalWorkAreaRestored");
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
                        && Native.IsWindowVisible((nint)before.Handle) == before.WasVisible, "GuardRestoredAfterParentDied");
                }
                File.WriteAllText(Path.Combine(data, "taskbar-checks.json"), JsonSerializer.Serialize(new
                {
                    Version = "0.1.0-preview.4", Checks = checks, IdleCpuPercent = idleCpu, Workers = workers,
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
