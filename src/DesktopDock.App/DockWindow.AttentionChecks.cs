using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class Controller
{
    public async Task RunAttentionTest(bool withTaskbar = false)
    {
        try
        {
            if (withTaskbar)
            {
                taskbar.InputForChecks = () => (false, false, false, false);
                taskbar.Apply(new DockSettings { TaskbarEnhancement = true, TaskbarGlass = false });
                if (!taskbar.Active) throw new InvalidOperationException(taskbar.Status);
            }
            var checks = await dock.CheckAttention(Path.Combine(data, "attention-checks"));
            if (withTaskbar)
            {
                bool hidden = taskbar.Active && taskbar.Snapshot is { } snapshot && !TaskbarNative.IsPresented((nint)snapshot.Handle);
                checks["TaskbarEnhancementStillHidesAfterDockAttention"] = hidden;
                if (!hidden) throw new InvalidOperationException("Attention changed native taskbar behavior.");
            }
            File.WriteAllText(Path.Combine(data, "attention-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (withTaskbar) taskbar.Stop(); }
        Quit();
    }
}

internal sealed partial class DockWindow
{
    internal async Task<Dictionary<string, bool>> CheckAttention(string data)
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name) { checks[name] = value; if (!value) throw new InvalidOperationException("Application attention: " + name); }
        async Task Wait(Func<bool> test, string name) { for (int i = 0; i < 100 && !test(); i++) await Task.Delay(30); Require(test(), name); }
        string directory = Path.Combine(data, "fixture");
        using var probe = RegressionProbe.Start("attention", directory);
        var original = settings.Items.ToList(); bool oldHide = settings.AutoHide, oldMotion = settings.ReducedMotion;
        try
        {
            Require(attentionHook.Registered, "ShellAttentionHookRegistered");
            IsolateInputForChecks(true); checkingInteraction = true; pointerOverForChecks = bottomEdgeForChecks = false;
            settings.AutoHide = settings.AppAttention = true; visibility.Reset();
            await RegressionProbe.WaitReady(probe, directory); nint window = RegressionProbe.Handle(directory);
            var item = new DockItem("Example attention", Environment.ProcessPath!);
            await PinForCheck(item); int index = entries.IndexOf(item);
            Native.ShowWindowAsync(window, 6); await Wait(() => Native.IsIconic(window), "FixtureMinimizedBeforeRequest");
            CheckVisibility(); await Task.Delay(250);
            Require(!IsDockVisible, "AutoHiddenDockStartsHidden");
            Native.SendMessage(Handle, attentionHook.MessageId, 0x8006, Handle);
            Native.SendMessage(Handle, attentionHook.MessageId, 0x8006, 0);
            await Task.Delay(100); Require(attentionRequests.Count == 0, "InvalidAndOwnWindowsDoNotRaiseAttention");
            File.WriteAllText(Path.Combine(directory, "flash.txt"), "start");
            await Wait(() => attentionRequests.ContainsKey(window) && IsDockVisible, "RealFlashWindowExRequestRevealsPinnedApplication");
            Require(renderer.AttentionForChecks(index) == (true, true, true), "OnlyAlertedIconShowsPulsingRedBackground");
            await Task.Delay(300);
            var captureSlot = geometry.Slots[index];
            int captureLeft = left + (int)((captureSlot.X - 5) * scale), captureTop = top + (int)((geometry.Baseline(height / scale) - captureSlot.Size - 5) * scale);
            int captureSize = (int)((captureSlot.Size + 10) * scale);
            var frame = new WindowBounds(captureLeft, captureTop, captureLeft + captureSize, captureTop + captureSize);
            var pixels = BitmapOverlay.CaptureTaskbar(new(Handle, 0, 0, frame, frame, frame, true, false));
            if (pixels is not null)
            {
                using var image = new System.Drawing.Bitmap(captureSize, captureSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var imageData = image.LockBits(new(0, 0, captureSize, captureSize), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, imageData.Scan0, pixels.Length); } finally { image.UnlockBits(imageData); }
                image.Save(Path.Combine(data, "attention-icon.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Require(Enumerable.Range(0, entries.Count).Where(x => x != index).All(x => !renderer.AttentionForChecks(x).Pending), "UnrelatedAndBuiltInIconsRemainUnchanged");
            await Task.Delay(3400); CheckVisibility(); await Task.Delay(250);
            Require(!IsDockVisible && renderer.AttentionForChecks(index) == (true, false, false), "RevealExpiresAndHiddenDockStopsAttentionAnimation");
            Require(!rendering, "AttentionPulseDoesNotKeepUiAnimationClockRunning");
            File.WriteAllText(Path.Combine(directory, "flash.txt"), "stop"); await Task.Delay(200);
            Summon(); await WaitUntilShownForChecks();
            Require(renderer.AttentionForChecks(index).Pulsing, "ManualRevealResumesPendingAttention");
            var slot = RestGeometry.Slots[index];
            int x = (int)((slot.X + slot.Size / 2) * scale), y = (int)((RestGeometry.Baseline(height / scale) - slot.Size / 2) * scale);
            SendCheckMouse(0x0201, 0, new(x | y << 16)); SendCheckMouse(0x0202, 0, new(x | y << 16));
            Require(attentionRequests.Count == 0 && !renderer.AttentionForChecks(index).Pending, "ClickClearsAttentionThroughNormalLaunchPath");
            Require(ReferenceEquals(SettingsIcon(item), SettingsIcon(item)) && ((System.Windows.Media.Imaging.BitmapSource)SettingsIcon(item)).IsFrozen,
                "SettingsReuseFrozenDockIconBitmap");

            attentionDismissed.Clear(); Native.ShowWindowAsync(window, 6); await Task.Delay(200);
            SetSuspended(true);
            File.WriteAllText(Path.Combine(directory, "flash.txt"), "start");
            await Wait(() => attentionRequests.Count > 0, "SuspendedDockRecordsNewAttention");
            Require(!IsDockVisible && !renderer.AttentionForChecks(index).Pulsing, "SuspensionPreventsRevealAndPulse");
            File.WriteAllText(Path.Combine(directory, "flash.txt"), "stop"); await Task.Delay(200);
            SetSuspended(false); await WaitUntilShownForChecks();
            Require(renderer.AttentionForChecks(index).Pending, "ResumePresentsStillPendingAttention");
            settings.ReducedMotion = true; ApplySettings(); Summon(); await WaitUntilShownForChecks();
            Require(renderer.AttentionForChecks(index) == (true, true, false), "ReducedMotionUsesStaticRedBackground");
            Native.SendMessage(Handle, attentionHook.MessageId, 4, window); await Task.Delay(150);
            Require(attentionRequests.Count == 0, "ShellActivationClearsApplicationAttention");
            Native.ShowWindowAsync(window, 6); await Wait(() => Native.IsIconic(window), "FixtureReminimizedBeforeRemoval");
            attentionDismissed.Clear();
            Native.SendMessage(Handle, attentionHook.MessageId, 0x8006, window); await Task.Delay(150);
            RemoveEntry(item);
            Require(attentionRequests.Count == 0 && !revealAttention, "RemovingPinnedEntryDropsItsPendingAttention");
            await PinForCheck(item); index = entries.IndexOf(item);
            Native.SendMessage(Handle, attentionHook.MessageId, 0x8006, window); await Task.Delay(150);
            settings.AppAttention = false; ApplySettings();
            Require(attentionRequests.Count == 0 && !renderer.AttentionForChecks(index).Pending, "DisablingAttentionClearsExistingBackground");
            Native.SendMessage(Handle, attentionHook.MessageId, 0x8006, window); await Task.Delay(100);
            Require(attentionRequests.Count == 0, "DisabledFeatureIgnoresNewRequests");
        }
        finally
        {
            SetSuspended(false); attentionRequests.Clear(); attentionDismissed.Clear(); revealAttention = false;
            settings.Items = original; settings.AutoHide = oldHide; settings.ReducedMotion = oldMotion; settings.AppAttention = true;
            checkingInteraction = false; pointerOverForChecks = bottomEdgeForChecks = null; IsolateInputForChecks(false);
            RegressionProbe.Close(probe); await LoadIcons();
        }
        return checks;
    }
}
