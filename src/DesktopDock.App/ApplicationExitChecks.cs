using System.Diagnostics;
using System.IO;

namespace DesktopDock.App;

internal static class ApplicationExitChecks
{
    public static async Task<Dictionary<string, bool>> Run(string data, Process unrelated)
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name)
        {
            checks[name] = value;
            if (!value) throw new InvalidOperationException("Application exit regression: " + name);
        }
        async Task WaitExited(Process process)
        {
            for (int i = 0; i < 60 && !process.HasExited; i++) await Task.Delay(50);
            Require(process.HasExited, "TargetProcessExited");
        }
        ApplicationExit? stale = null;
        foreach (string mode in new[] { "normal", "save-close", "tray-close", "multiple" })
        {
            string childData = Path.Combine(data, mode);
            using var child = RegressionProbe.Start(mode, childData);
            try
            {
                await RegressionProbe.WaitReady(child, childData);
                nint window = RegressionProbe.Handle(childData);
                var target = new ApplicationTarget(Path.GetFileNameWithoutExtension(Environment.ProcessPath), null, true)
                    { ExecutablePath = Environment.ProcessPath };
                var exit = ApplicationExit.Capture(target, [window], (uint)child.Id);
                Require(exit is { Running: true }, mode + "CapturesAssociatedProcess");
                Require(ApplicationExit.ExecutableMatchesWindow(window, Environment.ProcessPath!)
                    && !ApplicationExit.ExecutableMatchesWindow(window, Path.Combine(data, "Other", Path.GetFileName(Environment.ProcessPath)!)),
                    mode + "SameFileNameAtDifferentPathIsRejected");
                if (stale is not null)
                {
                    stale.ForceBackgroundExit(true);
                    Require(!child.HasExited, "StaleMenuDoesNotAffectLaterProcess");
                    stale = null;
                }
                Require(!exit!.ForceBackgroundExit(true) && !child.HasExited, mode + "VisibleWindowPreventsForce");
                if (mode == "multiple") Require(exit.VisibleWindows().Count == 2, "BothApplicationWindowsAreIncluded");
                var state = await exit.RequestNormalExit();
                Require(File.Exists(Path.Combine(childData, "close-request.json")), mode + "ReceivesNormalClose");
                if (mode is "normal" or "multiple")
                {
                    Require(state == ApplicationExitState.Exited, mode + "ExitsNormally");
                    await WaitExited(child);
                    stale = exit;
                }
                else if (mode == "save-close")
                {
                    Require(state == ApplicationExitState.AwaitingApplication && !child.HasExited, "SaveOrCancelKeepsApplicationAlive");
                    Require(!exit.ForceBackgroundExit(true) && !child.HasExited, "SaveWindowStillPreventsForce");
                }
                else
                {
                    Require(state == ApplicationExitState.BackgroundRunning && exit.VisibleWindows().Count == 0, "TrayResidencyIsDetected");
                    Require(!exit.ForceBackgroundExit(false) && !child.HasExited, "DecliningForceKeepsTrayApplicationAlive");
                    Require(exit.ForceBackgroundExit(true), "ConfirmedBackgroundExitIsAccepted");
                    await WaitExited(child);
                }
                Require(!unrelated.HasExited, mode + "UnrelatedApplicationIsUntouched");
            }
            finally
            {
                if (!child.HasExited)
                {
                    nint window = RegressionProbe.Handle(childData);
                    Native.PostMessage(window, 0x8009, 0, 0);
                    for (int i = 0; i < 40 && !child.HasExited; i++) await Task.Delay(50);
                }
            }
        }
        Require(ApplicationExit.Capture(new("explorer", null, false), []) is null, "ExplorerDoesNotOfferProcessExit");
        Require(ApplicationExit.Capture(new(Path.GetFileNameWithoutExtension(Environment.ProcessPath), null, false)
            { ExecutablePath = Environment.ProcessPath }, []) is null, "DockAndRecoveryGuardAreExcluded");
        Require(!ApplicationExit.CloseWindow(0, 0), "StaleWindowIsIgnored");
        nint desktop = Native.FindWindow("Progman", null), taskbar = Native.FindWindow("Shell_TrayWnd", null);
        Require(!ApplicationExit.CanCloseWindow(desktop) && !ApplicationExit.CanCloseWindow(taskbar), "DesktopAndTaskbarDoNotOfferWindowClose");
        return checks;
    }
}
