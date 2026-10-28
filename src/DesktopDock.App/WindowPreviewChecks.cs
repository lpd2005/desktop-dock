using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class DockWindow
{
    internal async Task<Dictionary<string, bool>> CheckWindowPreviews(string data, bool inspect)
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool condition, string name)
        {
            checks[name] = condition;
            if (!condition) throw new InvalidOperationException("Window previews: " + name);
        }
        async Task WaitFor(Func<bool> condition, string name)
        {
            for (int i = 0; i < 60 && !condition(); i++) await Task.Delay(50);
            Require(condition(), name);
        }
        Directory.CreateDirectory(data);
        var probes = new List<Process>();
        var windows = new List<nint>();
        bool? oldVisibility = visibilityForChecks, oldPointer = pointerOverForChecks;
        bool oldSetting = settings.WindowPreviews, oldReduced = settings.ReducedMotion;
        var item = new DockItem("Example windows", Environment.ProcessPath!);
        try
        {
            IsolateInputForChecks(true); visibilityForChecks = true; pointerOverForChecks = true; settings.WindowPreviews = true;
            previewPointerForChecks = false;
            for (int i = 0; i < 6; i++)
            {
                string folder = Path.Combine(data, "fixture-" + i);
                var process = RegressionProbe.Start(inspect ? "preview" : "normal", folder); probes.Add(process);
                await RegressionProbe.WaitReady(process, folder); windows.Add(RegressionProbe.Handle(folder));
            }
            await PinForCheck(item); await WaitUntilShownForChecks();
            nint foreground = Native.GetForegroundWindow();
            var frames = windows.Select(WindowOcclusion.Frame).ToArray();
            hoveredIndex = entries.IndexOf(item); previewHover.Reset(); previewIndex = -1;
            CheckVisibility();
            Require(windowPreview?.Open != true, "DwellDoesNotOpenImmediately");
            await WaitFor(() => windowPreview?.Open == true, "DockHoverOpensPreview");
            await Task.Delay(220);
            var preview = windowPreview!;
            Require(preview.Targets.Count == 6, "ExistingApplicationWindowsAreGrouped");
            Require(preview.ThumbnailCount is >= 1 and <= 4 && preview.ThumbnailCount == preview.SurfaceHandlesForChecks.Count, "VisiblePageUsesNativeDwmThumbnails");
            Require(Native.GetForegroundWindow() == foreground, "HoverDoesNotStealForeground");
            Require(windows.Select(WindowOcclusion.Frame).SequenceEqual(frames), "PreviewDoesNotResizeOrMoveApplications");
            Require((Native.GetWindowLongPtr(preview.Handle, -20).ToInt64() & (0x80 | 0x08000000)) == (0x80 | 0x08000000), "PreviewIsNoActivateToolWindow");
            Require(preview.SurfaceHandlesForChecks.All(hwnd => (Native.GetWindowLongPtr(hwnd, -20).ToInt64() & (0x80 | 0x08000000)) == (0x80 | 0x08000000)), "ThumbnailsAreExcludedFromAltTab");
            Require(!preview.Animating && !rendering, "SettledPreviewStopsAnimationClocks");
            Require(preview.BodyForChecks.Background is SolidColorBrush { Color.A: 255 }, "PreviewUsesOpaqueBackground");
            var renderedBody = new RenderTargetBitmap((int)Math.Ceiling(preview.BodyForChecks.ActualWidth), (int)Math.Ceiling(preview.BodyForChecks.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            renderedBody.Render(preview.BodyForChecks);
            byte[] bodyPixel = new byte[4];
            renderedBody.CopyPixels(new Int32Rect(renderedBody.PixelWidth / 2, renderedBody.PixelHeight / 2, 1, 1), bodyPixel, 4, 0);
            Require(bodyPixel[3] == 255, "RenderedBodyDoesNotShowUnderlyingWindows");
            preview.HoverForChecks(-1); await Task.Delay(170);
            var restingColors = preview.CardColorsForChecks.ToArray(); var thumbnailHandles = preview.SurfaceHandlesForChecks.ToArray();
            preview.HoverForChecks(0); await Task.Delay(170);
            Require(preview.HoveredCardForChecks == 0 && preview.CardColorsForChecks[0] != restingColors[0]
                && preview.CardColorsForChecks.Skip(1).SequenceEqual(restingColors.Skip(1)), "NativeThumbnailHoverHighlightsOnlyItsOwnCard");
            if (restingColors.Length > 1)
            {
                preview.HoverForChecks(1); await Task.Delay(170);
                Require(preview.CardColorsForChecks[0] == restingColors[0] && preview.CardColorsForChecks[1] != restingColors[1], "MovingBetweenCardsTransfersHighlight");
            }
            preview.HoverForChecks(-1); await Task.Delay(170);
            Require(preview.CardColorsForChecks.SequenceEqual(restingColors) && !preview.Animating, "HoverLeaveRestoresCardsAndStopsClock");
            Require(preview.SurfaceHandlesForChecks.SequenceEqual(thumbnailHandles), "HoverDoesNotRecreateThumbnailsOrChangePageLayout");
            hoveredIndex = -1; pointerOverForChecks = false; previewPointerForChecks = true;
            await Task.Delay(900); CheckVisibility();
            Require(preview.Open && IsDockVisible, "PointerOnPreviewKeepsDockAndPreviewOpen");
            hoveredIndex = entries.IndexOf(item); pointerOverForChecks = true; previewPointerForChecks = false;
            Native.GetWindowRect(preview.Handle, out var bounds);
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            Require(bounds.Left >= work.Left && bounds.Right <= work.Right && bounds.Top >= work.Top && bounds.Bottom <= work.Bottom, "PreviewStaysInsideWorkingArea");
            using (var single = new WindowPreview(_ => { }))
            {
                single.Show("Example window", [windows[0]], IconService.Bitmap(iconCache[item.Target]), (bounds.Left + bounds.Right) / 2d,
                    bounds.Bottom, new() { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Bottom }, scale, reducedMotion: true);
                Require(single.ThumbnailCount == 1 && single.BodyForChecks.ActualHeight < preview.BodyForChecks.ActualHeight,
                    "SingleWindowOmitsPagingSpaceAndKeepsCompactCard");
            }
            preview.WheelForChecks(-120);
            Require(preview.Page == 1, "WheelNavigatesMultipleWindowPages");
            await Task.Delay(200); preview.WheelForChecks(120);
            Require(preview.Page == 0, "WheelNavigatesBack");
            Native.ShowWindowAsync(windows[0], 6);
            await WaitFor(() => Native.IsIconic(windows[0]), "FixtureMinimizes");
            preview.Refresh(launcher.WindowsFor(item));
            Require(preview.Targets.Any(x => x.Window == windows[0]), "MinimizedWindowsRemainSelectable");
            var target = PreviewTarget.Capture(windows[0]);
            Require(!launcher.ActivatePreview(item, target with { Process = target.Process + 1 }), "ReusedOrWrongProcessIdentityIsRejected");
            Require(launcher.ActivatePreview(item, target), "SelectingMinimizedPreviewRequestsRestore");
            await WaitFor(() => !Native.IsIconic(windows[0]) && Native.GetAncestor(Native.GetForegroundWindow(), 3) == windows[0], "SelectionRestoresAndActivatesExactWindow");
            nint surface = preview.SurfaceHandlesForChecks[0];
            nint clickedWindow = preview.Targets[0].Window;
            Native.SendMessage(surface, 0x0201, 1, new(20 | 20 << 16));
            Native.SendMessage(surface, 0x0202, 0, new(3000 | 20 << 16));
            await Task.Delay(100);
            Require(preview.Open, "DraggingAwayFromThumbnailDoesNotActivateWindow");
            Native.SendMessage(surface, 0x0201, 1, new(20 | 20 << 16));
            Native.SendMessage(surface, 0x0202, 0, new(20 | 20 << 16));
            await WaitFor(() => !preview.Open, "NativeThumbnailClickDismissesPreview");
            Require(launcher.LastWindow == clickedWindow && launcher.LastAction == AppClickAction.Activate && !Native.IsIconic(clickedWindow), "PreviewClickActivatesInsteadOfTogglingMinimize");
            hoveredIndex = -1; pointerOverForChecks = false;
            await Task.Delay(180);
            Require(preview.ThumbnailCount == 0 && !Native.IsWindowVisible(preview.Handle), "DismissalReleasesThumbnailsAndHidesWindow");
            async Task Reopen()
            {
                pointerOverForChecks = true; hoveredIndex = entries.IndexOf(item); CheckVisibility();
                await WaitFor(() => preview.Open, "PreviewReopensAfterDwell");
            }
            await Reopen();
            switchingForChecks = true; CheckVisibility();
            Require(!preview.Open && preview.ThumbnailCount == 0, "AltTabDismissesPreviewImmediately");
            switchingForChecks = false; await WaitUntilShownForChecks(); await Reopen();
            PreviewDrop(ShellDropPointForChecks, true);
            Require(!preview.Open, "ExternalDragSuppressesPreview"); LeaveDrop();
            await Reopen(); settings.WindowPreviews = false; CheckVisibility();
            Require(!preview.Open && preview.ThumbnailCount == 0, "SettingDisablesPreviewImmediately");
            settings.WindowPreviews = true; settings.ReducedMotion = true; await Reopen();
            Require(!preview.Animating, "ReducedMotionOpensWithoutAnimation");
            preview.HoverForChecks(0);
            await WaitFor(() => preview.HoveredCardForChecks == 0, "ReducedMotionHoverReceivesNativeInput");
            Require(!preview.Animating && preview.CardColorsForChecks[0] == Colors.White, "ReducedMotionHoverChangesImmediately");
            preview.HoverForChecks(null);
            Native.PostMessage(windows[5], 0x0010, 0, 0);
            await WaitFor(() => !Native.IsWindow(windows[5]), "FixtureWindowCloses");
            preview.Refresh(launcher.WindowsFor(item));
            Require(preview.Targets.Count == 5, "ClosedWindowIsRemovedWithoutStalePreview");
            Require(!launcher.ActivatePreview(item, PreviewTarget.Capture(windows[5])), "ClosedWindowCannotBeActivated");
            if (inspect)
            {
                preview.InspectForChecks();
                string pause = Path.Combine(data, "visual-inspection.pause"); File.WriteAllText(pause, "Synthetic preview fixture only.");
                for (int i = 0; i < 9000 && File.Exists(pause); i++) await Task.Delay(100);
            }
            hoveredIndex = -1; pointerOverForChecks = false; CheckVisibility(); await Task.Delay(500); CheckVisibility();
            Require(!preview.Open && preview.ThumbnailCount == 0 && !preview.Animating, "LeavingClosesAndReleasesAllPreviewResources");
        }
        finally
        {
            CloseWindowPreview(); hoveredIndex = -1; switchingForChecks = null; previewPointerForChecks = null;
            settings.WindowPreviews = oldSetting; settings.ReducedMotion = oldReduced;
            visibilityForChecks = oldVisibility; pointerOverForChecks = oldPointer; IsolateInputForChecks(false);
            foreach (nint window in windows) Native.PostMessage(window, 0x0010, 0, 0);
            foreach (var process in probes) process.Dispose();
        }
        return checks;
    }
}
