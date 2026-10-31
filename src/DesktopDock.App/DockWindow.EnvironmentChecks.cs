using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class DockWindow
{
    internal async Task<Dictionary<string, bool>> CheckEnvironment(string data)
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name) { checks[name] = value; if (!value) throw new InvalidOperationException("Desktop environment: " + name); }
        async Task Settled()
        {
            for (int i = 0; i < 80 && (environment.Pending || suspended); i++) await Task.Delay(25);
            Require(!environment.Pending && !suspended, "EnvironmentQueueSettles");
        }
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId, callbacks = 0;
        DesktopChange lastChange = DesktopChange.None;
        var oldChanged = EnvironmentChanged;
        EnvironmentChanged = change => { callbacks++; lastChange = change; oldChanged?.Invoke(change); };
        var original = settings.Items.ToArray();
        string probeData = Path.Combine(data, "fixture");
        using var probe = RegressionProbe.Start("normal", probeData);
        try
        {
            Require(environment.SessionRegistered && environment.ShellMessage != 0, "CurrentSessionNotificationsRegistered");
            IsolateInputForChecks(true); visibilityForChecks = true; pointerOverForChecks = true;
            await RegressionProbe.WaitReady(probe, probeData);
            var item = new DockItem("Example window", Environment.ProcessPath!);
            await PinForCheck(item); await WaitUntilShownForChecks();
            await Task.Delay(400); await Settled();
            int before = callbacks;
            Native.SendMessage(Handle, 0x001A, 0, 0);
            Native.SendMessage(Handle, 0x02B1, 7, session + 1);
            await Task.Delay(300);
            Require(callbacks == before && !suspended, "UnrelatedSettingsAndOtherSessionsAreIgnored");
            for (int i = 0; i < 30; i++) Native.SendMessage(Handle, 0x001A, 0x002F, 0);
            Require(callbacks == before, "EnvironmentChangesDoNotRepositionSynchronously");
            await Settled();
            Require(callbacks == before + 1 && lastChange == DesktopChange.WorkArea, "WorkAreaBurstIsCoalesced");
            await Task.Delay(350);
            Require(callbacks == before + 1 && !environment.Pending, "SettledEnvironmentHasNoRepeatingTimer");

            hoveredIndex = entries.IndexOf(item); previewHover.Reset(); previewIndex = -1;
            for (int i = 0; i < 60 && windowPreview?.Open != true; i++) { CheckVisibility(); await Task.Delay(25); }
            Require(windowPreview?.Open == true && windowPreview.ThumbnailCount > 0, "FixturePreviewOpensBeforeSuspension");
            Native.SendMessage(Handle, 0x02B1, 7, session);
            Require(suspended && !IsDockVisible && !Native.IsWindowVisible(visualSource.Handle)
                && !visibilityTimer.IsEnabled && !rendering, "LockImmediatelyHidesBothDockWindowsAndStopsClocks");
            Require(windowPreview?.Open != true && windowPreview?.ThumbnailCount == 0, "LockReleasesPreviewThumbnails");
            SendCheckMouse(0x0201, 0, 0); Summon();
            Require(pressedIndex < 0 && !IsDockVisible, "SuspendedDockRejectsQueuedMouseAndReveal");
            Native.SendMessage(Handle, 0x0218, 4, 0);
            Native.SendMessage(Handle, 0x02B1, 8, session);
            await Task.Delay(350);
            Require(suspended && !IsDockVisible, "UnlockDoesNotResumeWhileStillSleeping");
            before = callbacks;
            Native.SendMessage(Handle, 0x0218, 18, 0); Native.SendMessage(Handle, 0x0218, 7, 0);
            await Settled(); await WaitUntilShownForChecks();
            Require(callbacks == before + 1 && lastChange.HasFlag(DesktopChange.Resume)
                && visibilityTimer.IsEnabled && IsDockVisible, "DuplicateResumeRepositionsOnceAndRestoresInput");
            Require(hoveredIndex == -1 && windowPreview?.Open != true, "ResumeDoesNotReopenStalePreview");

            checkingInteraction = true; pointerOverForChecks = false;
            int index = entries.IndexOf(item); var slot = RestGeometry.Slots[index];
            int x = (int)((slot.X + slot.Size / 2) * scale), y = (int)((RestGeometry.Baseline(height / scale) - slot.Size / 2) * scale);
            SendCheckMouse(0x0201, 0, new((y & 0xffff) << 16 | x & 0xffff));
            SendCheckMouse(0x0200, 0, new((y & 0xffff) << 16 | (x + 14) & 0xffff));
            Require(dragging && ghost is not null, "FixtureDragStartsBeforeEnvironmentChange");
            var itemsBeforeChange = settings.Items.ToArray(); before = callbacks;
            for (int i = 0; i < 15; i++)
            {
                Native.SendMessage(Handle, 0x007E, 32, 0);
                Native.GetWindowRect(Handle, out var suggested);
                nint bounds = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Rect>());
                try
                {
                    Marshal.StructureToPtr(suggested, bounds, false);
                    int dpi = (int)Native.GetDpiForWindow(Handle);
                    Native.SendMessage(Handle, 0x02E0, new(dpi | dpi << 16), bounds);
                }
                finally { Marshal.FreeHGlobal(bounds); }
            }
            Require(suspended && !dragging && ghost is null && pressedIndex < 0 && !IsDockVisible
                && settings.Items.SequenceEqual(itemsBeforeChange), "DisplayChangeCancelsDragWithoutRemovingEntry");
            await Settled(); await WaitUntilShownForChecks();
            Require(callbacks == before + 1 && lastChange.HasFlag(DesktopChange.Display) && lastChange.HasFlag(DesktopChange.Dpi), "MixedDisplayAndDpiBurstRepositionsOnce");
            nint actualRegion = Native.CreateRectRgn(0, 0, 0, 0);
            try
            {
                Require(Native.GetWindowRgn(Handle, actualRegion) > 0 && Native.PtInRegion(actualRegion,
                    (int)((RestGeometry.Slots[0].X + RestGeometry.Slots[0].Size / 2) * scale),
                    (int)((RestGeometry.Baseline(height / scale) - RestGeometry.Slots[0].Size / 2) * scale)), "InputRegionMatchesLayoutAfterResume");
            }
            finally { Native.DeleteObject(actualRegion); }

            Native.SendMessage(Handle, 0x02B1, 4, session);
            Native.SendMessage(Handle, 0x0218, 18, 0); await Task.Delay(300);
            Require(suspended, "PowerResumeDoesNotOverrideDisconnectedSession");
            Native.SendMessage(Handle, 0x02B1, 3, session); await Settled();
            Require(!suspended, "SessionReconnectRestoresInteraction");
            before = callbacks; Native.SendMessage(Handle, environment.ShellMessage, 0, 0);
            Require(suspended && !IsDockVisible, "ShellNotificationImmediatelyWithdrawsStaleInput");
            await Settled(); Require(callbacks == before + 1 && lastChange == DesktopChange.Shell, "ShellNotificationRefreshesWithoutRestartingExplorer");
            Require(ExcludedFromAltTab, "LifecycleHandlingPreservesAltTabExclusion");

            using var temporary = new HwndSource(new HwndSourceParameters("Desktop Dock environment fixture")
            { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
            int afterDispose = 0;
            var monitor = new DesktopEnvironment(temporary, _ => afterDispose++, _ => afterDispose++);
            Native.SendMessage(temporary.Handle, 0x001A, 0x002F, 0); monitor.Dispose();
            Native.SendMessage(temporary.Handle, 0x02B1, 7, session); await Task.Delay(350);
            Require(!monitor.Pending && afterDispose == 0, "DisposalCancelsDeferredCallbacksAndRemovesSessionHook");
        }
        finally
        {
            EnvironmentChanged = oldChanged; checkingInteraction = false;
            visibilityForChecks = pointerOverForChecks = null; IsolateInputForChecks(false);
            settings.Items = original.ToList(); await LoadIcons(); RegressionProbe.Close(probe);
        }
        return checks;
    }
}
