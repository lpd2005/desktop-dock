using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class DockWindow
{
    internal void InvokeSettingsEntry() => Launch(new("Dock 设置", "dock:settings"));
    internal void LaunchForCheck(DockItem item) => Launch(item);
    internal async Task PinForCheck(DockItem item)
    {
        if (!settings.Items.Contains(item)) { settings.Items.Add(item); await LoadIcons(); }
    }
    internal DockAppState AppStateForChecks(DockItem item)
    {
        RefreshAppStates(clock.Elapsed.TotalSeconds, force: true);
        return renderer.StateForChecks(entries.IndexOf(item)).State;
    }
    internal DockAppState SettingsStateForChecks => AppStateForChecks(entries.Single(x => x.Target == "dock:settings"));

    internal async Task<Dictionary<string, bool>> CheckAppStatePresentation()
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name)
        {
            checks[name] = value;
            if (!value) throw new InvalidOperationException("App state presentation: " + name);
        }
        bool? oldVisibility = visibilityForChecks, oldRefresh = stateRefreshForChecks, oldPointer = pointerOverForChecks;
        bool oldSuppression = suppressHover;
        try
        {
            stateRefreshForChecks = false; visibilityForChecks = true; pointerOverForChecks = false; suppressHover = true;
            CancelInteraction(); hover.Reset(0); pointer.Reset(pointerTarget);
            await WaitUntilShownForChecks();
            var states = new DockAppState[entries.Count];
            void Present() { renderer.SetAppStates(states); RenderLayout(); StartRendering(); }
            Present(); await Task.Delay(250);
            Require(!renderer.StateForChecks(0).Visible, "ClosedHasNoMarker");
            var identities = renderer.IconIdentities.ToArray();
            states[0] = new(true, false); Present(); await Task.Delay(230);
            var running = renderer.StateForChecks(0);
            Require(running.Visible && Math.Abs(running.Width / scale - 10) < .01, "RunningUsesShortLine");
            Require(running.Color.R == 255 && running.Color.G == 255 && running.Color.B == 255, "InactiveRunningLineIsWhite");
            states[0] = new(true, true); Present(); await Task.Delay(55);
            var middle = renderer.StateForChecks(0);
            Require(middle.Width > running.Width && middle.Width < 22 * scale && renderer.StateAnimationActive, "ActiveMarkerTransitionsSmoothly");
            await Task.Delay(230);
            var active = renderer.StateForChecks(0);
            Require(Math.Abs(active.Width / scale - 22) < .01 && active.Color.R == 77 && active.Color.G == 170 && active.Color.B == 255, "ActiveUsesLongBlueLine");
            Require(!renderer.StateAnimationActive && !rendering, "SettledStateStopsAnimationClock");
            Require(renderer.IconIdentities.SequenceEqual(identities), "StatusDoesNotRecreateIconSurfaces");
            renderer.Layout(geometry, width, height, scale, hidden: 0);
            Require(!renderer.StateForChecks(0).Visible, "DraggedSourceHidesMarker");
            RenderLayout();
            renderer.MoveIcon(0, 1); RenderLayout();
            Require(renderer.StateForChecks(1).State == states[0] && renderer.IconIdentities[1] == identities[0], "StateFollowsReorderedIcon");
            renderer.MoveIcon(1, 0); RenderLayout();
            await LoadIcons();
            Require(renderer.IconIdentities.SequenceEqual(identities) && renderer.StateForChecks(0).State == states[0], "ReloadRetainsStateAndSurfaces");
            states[0] = new(true, false); Present(); await Task.Delay(230);
            Require(renderer.StateForChecks(0).Visible && renderer.StateForChecks(0).Width < active.Width, "InactiveRetainsRunningMarker");
            renderer.SetTheme(new DockSettings { Reflections = settings.Reflections, ReducedMotion = true });
            states[0] = new(true, true); Present();
            Require(!renderer.StateAnimationActive && Math.Abs(renderer.StateForChecks(0).Width / scale - 22) < .01, "ReducedMotionChangesImmediately");
            states[0] = default; Present();
            Require(!renderer.StateForChecks(0).Visible, "ClosingRemovesMarker");
            renderer.SetTheme(settings);
            stateRefreshForChecks = oldRefresh;
            visibilityForChecks = false; CheckVisibility();
            int scans = appStateScans;
            await Task.Delay(400); CheckVisibility();
            Require(appStateScans == scans, "HiddenDockDoesNotScanAppStates");
        }
        finally
        {
            renderer.SetTheme(settings); stateRefreshForChecks = oldRefresh; visibilityForChecks = oldVisibility;
            pointerOverForChecks = oldPointer; suppressHover = oldSuppression; appStatesDirty = true;
            CheckVisibility();
        }
        return checks;
    }
    internal void ClickForCheck(DockItem item, Action? betweenPressAndRelease = null, bool nativeDoubleClick = false)
    {
        int index = entries.IndexOf(item);
        if (index < 0) throw new InvalidOperationException("Missing test entry.");
        var slot = geometry.Slots[index];
        int x = (int)Math.Round((slot.X + slot.Size / 2) * scale);
        int y = (int)Math.Round((geometry.Baseline(height / scale) - slot.Size / 2) * scale);
        nint point = new((y & 0xffff) << 16 | x & 0xffff);
        bool previous = checkingInteraction; checkingInteraction = true;
        try
        {
            SendCheckMouse(nativeDoubleClick ? 0x0203 : 0x0201, 1, point);
            betweenPressAndRelease?.Invoke();
            SendCheckMouse(0x0202, 0, point);
        }
        finally { checkingInteraction = previous; }
    }
    private void SendCheckMouse(int message, nint wparam, nint lparam)
    {
        bool previous = dispatchingCheckMouse; dispatchingCheckMouse = true;
        try { Native.SendMessage(Handle, message, wparam, lparam); }
        finally { dispatchingCheckMouse = previous; }
    }
    internal async Task WaitUntilShownForChecks()
    {
        for (int i = 0; i < 100; i++)
        {
            CheckVisibility();
            if (IsDockVisible && !motion.Active) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Dock did not finish revealing.");
    }

    internal async Task<Dictionary<string, bool>> DoubleClickHoveredForCheck(DockItem item)
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name)
        {
            checks[name] = value;
            if (!value) throw new InvalidOperationException("Hovered click regression: " + name);
        }
        bool previousInteraction = checkingInteraction, previousSynthetic = syntheticHover;
        bool? previousOver = pointerOverForChecks;
        bool previousSuppression = suppressHover;
        double? previousPointer = pointerTargetForChecks;
        try
        {
            checkingInteraction = true; pointerOverForChecks = true; syntheticHover = false; suppressHover = false;
            clicks.Reset();
            Summon(); await WaitUntilShownForChecks();
            int index = entries.IndexOf(item);
            if (index < 0) throw new InvalidOperationException("Missing hovered-click test entry.");
            var rest = RestGeometry;
            pointerTarget = rest.Slots[index].X + rest.Slots[index].Size / 2;
            pointerTargetForChecks = pointerTarget;
            if (hover.Value < .01) pointer.Reset(pointerTarget);
            StartRendering();
            for (int i = 0; i < 60 && rendering; i++) await Task.Delay(20);
            var original = geometry.Slots.ToArray();
            Require(original[index].Size > rest.IconSize * 1.05, "InitiallyMagnified");
            Require(!rendering, "HoveredRestStopsClock");
            var slot = original[index];
            int x = (int)Math.Round((slot.X + slot.Size / 2) * scale);
            int y = (int)Math.Round((geometry.Baseline(height / scale) - slot.Size / 2) * scale);
            nint point = new((y & 0xffff) << 16 | x & 0xffff);
            int sampleMs = Math.Clamp((int)Native.GetDoubleClickTime() / 8, 12, 50);
            async Task Sample(string name, int? milliseconds = null)
            {
                double until = clock.Elapsed.TotalMilliseconds + (milliseconds ?? sampleMs);
                bool stable = true;
                do
                {
                    await Task.Delay(8);
                    stable &= geometry.Slots.Length == original.Length && geometry.Slots.Zip(original).All(pair =>
                        Math.Abs(pair.First.X - pair.Second.X) < .25 && Math.Abs(pair.First.Size - pair.Second.Size) < .25);
                } while (clock.Elapsed.TotalMilliseconds < until);
                Require(stable, name);
            }
            SendCheckMouse(0x0201, 1, point);
            await Sample("FirstPressKeepsGeometry");
            double hoveredSize = original[index].Size * scale;
            float firstPressedSize = renderer.ImageSizeForChecks(index);
            Require(firstPressedSize < hoveredSize * .995 && firstPressedSize > hoveredSize * .9, "FirstPressHasScaleFeedback");
            int neighbor = index > 0 ? index - 1 : index + 1;
            Require(Math.Abs(renderer.ImageSizeForChecks(neighbor) - original[neighbor].Size * scale) < .25, "NeighborKeepsVisualSize");
            SendCheckMouse(0x0202, 0, point);
            Require(launcher.LastAction == AppClickAction.Ignore && !launcher.IsWatching, "FirstReleaseDoesNotLaunch");
            await Sample("BetweenClicksKeepsGeometry");
            Require(renderer.ImageSizeForChecks(index) > firstPressedSize + .1, "FirstReleaseReturnsTowardHoverSize");
            SendCheckMouse(0x0203, 1, point);
            await Sample("SecondPressKeepsGeometry");
            Require(renderer.ImageSizeForChecks(index) < hoveredSize * .995, "SecondPressHasScaleFeedback");
            SendCheckMouse(0x0202, 0, point);
            Require(launcher.LastAction == AppClickAction.Launch && launcher.IsWatching, "SecondReleaseLaunches");
            await Sample("SecondReleaseKeepsGeometry", 220);
            Require(Math.Abs(renderer.ImageSizeForChecks(index) - hoveredSize) < .25, "ReleaseRestoresHoveredSize");
            Require(!rendering, "ReleasedHoverStopsClock");
            renderer.SetTheme(new DockSettings { Reflections = settings.Reflections, ReducedMotion = true });
            renderer.SetPressed(index, true); RenderLayout();
            Require(Math.Abs(renderer.ImageSizeForChecks(index) - hoveredSize) < .25 && !renderer.PressFeedbackActive,
                "ReducedMotionSkipsPressFeedback");
            renderer.SetPressed(index, false);
        }
        finally
        {
            if (pressedIndex >= 0) CancelInteraction();
            checkingInteraction = previousInteraction; pointerOverForChecks = previousOver;
            syntheticHover = previousSynthetic; suppressHover = previousSuppression; StartRendering();
            pointerTargetForChecks = previousPointer;
            renderer.SetTheme(settings);
        }
        return checks;
    }

    internal async Task<Dictionary<string, bool>> CheckVisibilityMotion()
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name) { checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        bool reduced = settings.ReducedMotion;
        try
        {
            settings.ReducedMotion = false;
            await WaitUntilShownForChecks();
            visibilityForChecks = false; CheckVisibility();
            Require(motion.Active && !Native.IsWindowVisible(Handle) && Native.IsWindowVisible(visualSource.Handle), "HideKeepsPictureAndReleasesInput");
            await Task.Delay(60);
            Native.GetWindowRect(visualSource.Handle, out var partial);
            Native.GetLayeredWindowAttributes(visualSource.Handle, out _, out byte alpha, out _);
            Require(motion.Progress > 0 && motion.Progress < 1 && alpha > 0 && alpha < 255 && partial.Top > top,
                "HideHasIntermediateAlphaAndPosition");
            visibilityForChecks = true; CheckVisibility();
            Require(motion.Active && motion.Progress > 0 && motion.Progress < 1, "ReverseDoesNotSnapToVisible");
            await WaitUntilShownForChecks();
            Native.GetWindowRect(visualSource.Handle, out var shown);
            Require(motion.Progress == 1 && shown.Top == top && Native.IsWindowVisible(Handle), "RevealSettlesBeforeAcceptingInput");
            int frames = motion.Frames; await Task.Delay(140);
            Require(motion.Frames == frames && !motion.Active, "VisibilityClockStopsAtRest");
            visibilityForChecks = false; CheckVisibility(); await Task.Delay(280);
            Require(motion.Progress == 0 && !motion.Active && !Native.IsWindowVisible(Handle)
                && !Native.IsWindowVisible(visualSource.Handle), "HideFinishesWithBothWindowsHidden");
            visibilityForChecks = true; CheckVisibility(); await Task.Delay(60);
            Require(motion.Active && motion.Progress > 0 && motion.Progress < 1 && !Native.IsWindowVisible(Handle),
                "RevealHasIntermediateFramesWithoutInvisibleInput");
            visibilityForChecks = false; CheckVisibility(); await Task.Delay(280);
            Require(motion.Progress == 0 && !motion.Active, "ReverseRevealSettlesHidden");
            settings.ReducedMotion = true;
            visibilityForChecks = true; CheckVisibility();
            Require(motion.Progress == 1 && !motion.Active && IsDockVisible, "ReducedMotionShowsImmediately");
            visibilityForChecks = false; CheckVisibility();
            Require(motion.Progress == 0 && !motion.Active, "ReducedMotionHidesImmediately");
        }
        finally { settings.ReducedMotion = reduced; visibilityForChecks = null; CheckVisibility(); }
        await WaitUntilShownForChecks();
        return checks;
    }
    internal void UseOcclusionWindowsForChecks(Func<IEnumerable<nint>> source) { occlusion.WindowsForChecks = source; RefreshOcclusion(); }
    internal void IsolateInputForChecks(bool enabled) => isolatingInputForChecks = enabled;
    internal void SetSwitchingForChecks(bool? value) { switchingForChecks = value; CheckVisibility(); }
    internal void InteractForChecks() => NoteInteraction();
    internal void SetBottomEdgeForChecks(bool? value) { bottomEdgeForChecks = value; CheckVisibility(); }
    internal void SetPointerOverForChecks(bool? value) { pointerOverForChecks = value; CheckVisibility(); }
    internal void SetShellDragForChecks(ShellDragInput? input)
    {
        shellDrag.InputForChecks = input is null ? null : () => input.Value;
        CheckVisibility();
    }
    internal Native.Point ShellDropPointForChecks => new() { X = (RestDockArea.Left + RestDockArea.Right) / 2, Y = (RestDockArea.Top + RestDockArea.Bottom) / 2 };
    internal bool ExcludedFromAltTab => new[] { Handle, visualSource.Handle }.All(hwnd =>
        (Native.GetWindowLongPtr(hwnd, -20).ToInt64() & (0x80 | 0x40000)) == 0x80);

    internal async Task<bool> CheckMenuPersistence()
    {
        bool stayedOpen = false;
        var finished = new TaskCompletionSource<bool>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); CheckVisibility();
            stayedOpen = menuOpen && menuForChecks != 0 && IsDockVisible;
            Native.EndMenu();
        };
        // Native menu loops pump window messages; the independent fallback
        // cancels only our HWND if a dispatcher problem prevents the observer.
        using var fallback = new System.Threading.Timer(_ => Native.PostMessage(Handle, 0x001F, 0, 0), null, 5000, Timeout.Infinite);
        _ = source.Dispatcher.BeginInvoke(() =>
        {
            try { timer.Start(); ShowMenu(0); finished.SetResult(stayedOpen); }
            catch (Exception error) { finished.SetException(error); }
            finally { timer.Stop(); }
        });
        return await finished.Task.WaitAsync(TimeSpan.FromSeconds(7));
    }

    internal async Task<Dictionary<string, bool>> CheckDragging(string data, bool holdForInspection = false)
    {
        var result = new Dictionary<string, bool>();
        var original = settings.Items.ToArray();
        bool originalAutoHide = settings.AutoHide;
        void Require(bool pass, string description) { result[description] = pass; if (!pass) throw new InvalidOperationException(description); }
        void Mouse(int message, double x, double y)
        {
            int px = (int)Math.Round(x * scale), py = (int)Math.Round(y * scale);
            SendCheckMouse(message, 0, new((py & 0xffff) << 16 | px & 0xffff));
        }
        async Task Rest()
        {
            for (int i = 0; i < 40 && (returning || rendering); i++) await Task.Delay(25);
        }
        try
        {
            checkingInteraction = true;
            pointerOverForChecks = false;
            CheckRestLayoutCache(Require);
            Require(dropTarget.Registered, "NativeOleDropTargetRegistered 0x" + dropTarget.RegistrationResult.ToString("X8"));
            Require((Native.GetWindowLongPtr(visualSource.Handle, -20).ToInt64() & 0x80020) == 0x80020,
                "PresentationWindowPassesPointerInput");
            var inputSlot = RestGeometry.Slots[0];
            Require(Native.WindowFromPoint(new() { X = left + (int)((inputSlot.X + inputSlot.Size / 2) * scale),
                Y = top + (int)((RestGeometry.Baseline(height / scale) - inputSlot.Size / 2) * scale) }) == Handle,
                "IconAreaHitsShapedInputWindow");
            Require(entries[0].Target == "dock:apps" && entries[^1].Target == "dock:settings", "BuiltInEntriesAnchorOppositeEnds");
            var launcherSlot = RestGeometry.Slots[0];
            double launcherX = launcherSlot.X + launcherSlot.Size / 2, launcherY = RestGeometry.Baseline(height / scale) - launcherSlot.Size / 2;
            Mouse(0x0201, launcherX, launcherY); Mouse(0x0200, launcherX, launcherY - 110); Mouse(0x0202, launcherX, launcherY - 110);
            Require(!dragging && settings.Items.SequenceEqual(original) && entries[0].Target == "dock:apps", "DesktopEntryCannotBeDraggedOrRemoved");
            var interruptedSlot = RestGeometry.Slots[1];
            double interruptedX = interruptedSlot.X + interruptedSlot.Size / 2;
            double interruptedY = RestGeometry.Baseline(height / scale) - interruptedSlot.Size / 2;
            Mouse(0x0201, interruptedX, interruptedY);
            dragStartInterruptionForChecks = () => Native.SendMessage(Handle, 0x001F, 0, 0);
            Mouse(0x0200, interruptedX + 10, interruptedY);
            dragStartInterruptionForChecks = null;
            Require(!dragging && ghost is null && pressedIndex == -1 && settings.Items.SequenceEqual(original),
                "NativeCancelDuringDragPreparationDoesNotStartOrRemove");
            Mouse(0x0201, interruptedX, interruptedY);
            dragStartInterruptionForChecks = () => Native.SendMessage(Handle, 0x0215, 0, 0);
            Mouse(0x0200, interruptedX + 10, interruptedY);
            dragStartInterruptionForChecks = null;
            Require(!dragging && ghost is null && pressedIndex == -1 && settings.Items.SequenceEqual(original),
                "NativeCaptureLossDuringDragPreparationDoesNotStartOrRemove");
            Mouse(0x0201, interruptedX, interruptedY);
            dragStartInterruptionForChecks = () =>
            {
                Native.SendMessage(Handle, 0x001F, 0, 0);
                Mouse(0x0201, interruptedX, interruptedY);
            };
            Mouse(0x0200, interruptedX + 10, interruptedY);
            dragStartInterruptionForChecks = null;
            Require(!dragging && ghost is null && pressedIndex == 1 && settings.Items.SequenceEqual(original),
                "NewPressInvalidatesPreviousDragPreparation");
            Native.SendMessage(Handle, 0x001F, 0, 0);
            bool autoHideBefore = settings.AutoHide;
            bottomEdgeForChecks = false;
            settings.AutoHide = true; CheckVisibility();
            for (int i = 0; i < 60 && !autoHidden; i++) { await Task.Delay(25); CheckVisibility(); }
            Require(autoHidden, "AutoHideInitiallyRetractsDock");
            var recallArea = RestDockArea;
            var nearDock = new Native.Point { X = (recallArea.Left + recallArea.Right) / 2, Y = (recallArea.Top + recallArea.Bottom) / 2 };
            foreach (var pointAtClick in new[] { new Native.Point { X = recallArea.Left, Y = recallArea.Top - 250 }, nearDock })
            {
                for (int click = 0; click < 8; click++)
                {
                    SetShellDragForChecks(new(false, false, false, pointAtClick));
                    SetShellDragForChecks(new(true, true, true, pointAtClick));
                    SetShellDragForChecks(new(true, true, true, new() { X = pointAtClick.X + 1, Y = pointAtClick.Y + 1 }));
                    Require(!motion.Target && !shellDrag.Active, "ExplorerClickCaptureDoesNotReveal" + pointAtClick.Y + "-" + click);
                }
            }
            var farFromDock = new Native.Point { X = nearDock.X, Y = recallArea.Top - 250 };
            SetShellDragForChecks(new(false, false, false, farFromDock));
            SetShellDragForChecks(new(true, true, true, farFromDock));
            SetShellDragForChecks(new(true, true, true, new() { X = farFromDock.X + 30, Y = farFromDock.Y + 30 }));
            Require(!motion.Target && !shellDrag.Active, "ExplorerSelectionAwayFromDockDoesNotReveal");
            SetShellDragForChecks(new(true, false, true, nearDock));
            await WaitUntilShownForChecks();
            Require(shellDrag.Active && IsDockVisible && !autoHidden, "DesktopDragWithoutWinEventRevealsDock");
            await Task.Delay(650); CheckVisibility();
            Require(IsDockVisible && !autoHidden, "DragKeepsDockAvailableBeforeOleEnter");
            SetSwitchingForChecks(true);
            Require(!IsDockVisible && !shellDrag.Active, "AltTabStillSuppressesDraggingDock");
            SetShellDragForChecks(new(false, false, false, new()));
            settings.AutoHide = autoHideBefore; SetSwitchingForChecks(false);
            await WaitUntilShownForChecks(); SetSwitchingForChecks(null);
            PreviewWave(.1); pointerTargetForChecks = pointerTarget; await Task.Delay(180);
            var first = geometry.Slots[1];
            Mouse(0x0201, first.X + first.Size / 2, geometry.Baseline(height / scale) - first.Size / 2);
            await Task.Delay(260);
            bool heldHover = hover.Value > .99 && geometry.Slots[1].Size >= first.Size - .1;
            Require(heldHover, "HeldIconKeepsHoverBeforeDrag" + (heldHover ? "" : $": hover={hover.Value:F3}, size={geometry.Slots[1].Size:F2}, before={first.Size:F2}, drag={dragging}, suppression={suppressHover}, shell={shellDrag.Active}"));
            pointerTargetForChecks = null;
            var rest = RestGeometry;
            double x = rest.Slots[3].X + rest.Slots[3].Size / 2 + 2, y = rest.Baseline(height / scale) - rest.IconSize / 2;
            int generation = loadGeneration;
            Mouse(0x0200, x, y); await Task.Delay(100);
            Require(dragging && ghost is not null && dragTo == 3, "FloatingGhostAndLiveReorder");
            Require(geometry.Slots.All(slot => Math.Abs(slot.Size - geometry.IconSize) < .001), "NoMagnificationDuringDrag");
            Require(ghost is not null && (Native.GetWindowLongPtr(ghost.Handle, -20).ToInt64() & (0x80 | 0x20 | 0x08000000)) == (0x80 | 0x20 | 0x08000000), "GhostDoesNotActivateOrTakeInput");
            if (holdForInspection) await Task.Delay(20000);
            Mouse(0x0202, x, y); await Rest();
            Require(settings.Items[2] == original[0] && loadGeneration == generation, "SortCommitsWithoutReloadingIcons");
            Require(ghost is null && !rendering, "DropSettlesAndClockStops");
            var sorted = settings.Items.ToArray();
            rest = RestGeometry; first = rest.Slots[1];
            Mouse(0x0201, first.X + first.Size / 2, y);
            Mouse(0x0200, rest.Slots[4].X + rest.IconSize / 2 + 2, y);
            await Task.Delay(60);
            bool cancelled = false; Message(Handle, 0x001F, 0, 0, ref cancelled); await Rest();
            Require(settings.Items.SequenceEqual(sorted) && ghost is null, "CancelledSortPreservesOrder");

            System.IO.Directory.CreateDirectory(data);
            string shortcut = System.IO.Path.Combine(data, "示例 快捷方式.lnk");
            string unsupported = System.IO.Path.Combine(data, "Unsupported.txt");
            string folder = System.IO.Path.Combine(data, "Example folder");
            System.IO.Directory.CreateDirectory(folder); System.IO.File.WriteAllText(unsupported, "Example");
            RegressionProbe.CreateShortcut(shortcut, Environment.ProcessPath!, "--probe-window normal --data-dir \"" + System.IO.Path.Combine(data, "never-launched") + "\"");
            byte[] before = System.IO.File.ReadAllBytes(shortcut);
            var point = new Native.Point { X = left + (int)(RestGeometry.Slots[2].X * scale), Y = top + (int)(y * scale) };
            int count = settings.Items.Count;
            int effect = dropTarget.DropFilesForChecks([shortcut, folder], point);
            for (int i = 0; i < 80 && entries.Count != settings.Items.Count + 2; i++) await Task.Delay(25);
            Require(effect == 4 && settings.Items.Count == count + 2 && settings.Items[1].Target == shortcut, "OleFileDropInsertsShortcutAndFolder");
            Require(System.IO.File.Exists(shortcut) && before.SequenceEqual(System.IO.File.ReadAllBytes(shortcut)), "OriginalShortcutIsUnchanged");
            Require(!System.IO.File.Exists(System.IO.Path.Combine(data, "never-launched", "ready.json")), "PinningDoesNotLaunchApplication");
            Require(dropTarget.DropFilesForChecks([shortcut], point) == 0 && settings.Items.Count == count + 2, "DuplicateDropAddsNothing");
            Require(dropTarget.DropFilesForChecks([unsupported], point) == 0 && settings.Items.Count == count + 2, "UnsupportedFileIsRejected");
            Require(!externalDrop && insertion == -1, "DropFeedbackClears");

            rest = RestGeometry;
            double removeX = rest.Slots[2].X + rest.Slots[2].Size / 2;
            double removeY = rest.BodyTop(height / scale) - 100;
            Mouse(0x0201, removeX, rest.Baseline(height / scale) - rest.IconSize / 2);
            Mouse(0x0200, removeX, removeY);
            bool cancelFar = false; Message(Handle, 0x001F, 0, 0, ref cancelFar); await Rest();
            Require(settings.Items.Any(item => item.Target == shortcut), "CancelledFarDragKeepsEntry");
            int beforeRemoval = settings.Items.Count;
            Mouse(0x0201, removeX, rest.Baseline(height / scale) - rest.IconSize / 2);
            Mouse(0x0200, removeX, removeY); await Task.Delay(60);
            Require(ghostRemoving && ghostRemovePixels is not null && ghostNormalPixels is not null
                && !ghostRemovePixels.SequenceEqual(ghostNormalPixels), "FarDragShowsCachedRedTrashBadge");
            Mouse(0x0200, removeX, rest.Baseline(height / scale) - rest.IconSize / 2); await Task.Delay(40);
            Require(!ghostRemoving && ghost is not null, "ReturningInsideDockClearsRemovalBadge");
            Mouse(0x0200, removeX, removeY); await Task.Delay(40);
            Require(ghostRemoving, "DraggingOutAgainRestoresRemovalBadge");
            Mouse(0x0202, removeX, removeY);
            Require(settings.Items.Count == beforeRemoval - 1 && !settings.Items.Any(item => item.Target == shortcut)
                && renderer.IconCount == settings.Items.Count + 2 && ghost is null, "FarReleaseRemovesOnlyDockEntry");
            Require(System.IO.File.Exists(shortcut) && before.SequenceEqual(System.IO.File.ReadAllBytes(shortcut)),
                "DragRemovalLeavesOriginalShortcutUnchanged");

            foreach (int initialCount in new[] { 8, 24, 38, 39 })
            {
                settings.AutoHide = false;
                settings.Items = Enumerable.Range(0, initialCount)
                    .Select(i => new DockItem("Example " + i, original[0].Target, "example-" + i)).ToList();
                await LoadIcons();
                SetSwitchingForChecks(false);
                SetShellDragForChecks(new(false, false, false, new()));
                await WaitUntilShownForChecks();
                var oldEntries = entries.ToArray();
                var oldVisuals = renderer.IconIdentities;
                point = new() { X = left + (int)(RestGeometry.Slots[1].X * scale),
                    Y = top + (int)((RestGeometry.Baseline(height / scale) - RestGeometry.IconSize / 2) * scale) };

                // Reproduce a source window clearing during a drag: the reveal
                // timer used to be skipped until DragLeave, causing a hide/show.
                visibility.ShouldShow(true, false, clock.Elapsed.TotalSeconds);
                PreviewDrop(point, true);
                settings.AutoHide = true;
                IconLoadDelayForChecks = 650;
                int hidesBefore = hideTransitions;
                int presentationChangesBefore = visualSource.PositionChanges;
                float probeX = (float)(RestGeometry.Slots[0].X + RestGeometry.IconSize / 2) * scale;
                float probeY = (float)(RestGeometry.Baseline(height / scale) - RestGeometry.IconSize / 2) * scale;
                using var probe = renderer.PresentationProbe(probeX - 8, probeY - 8, 16);
                bool PixelPresent()
                {
                    uint pixel = BitmapOverlay.ReadScreenPixel(left + (int)probeX, top + (int)probeY);
                    return (pixel >> 16 & 255) >= 240 && (pixel & 255) >= 240 && (pixel >> 8 & 255) <= 15;
                }
                bool presented = false;
                for (int i = 0; i < 15 && !(presented = PixelPresent()); i++) await Task.Delay(30);
                Require(presented, "CompositionPresentationPixelIsVisible" + initialCount);
                effect = dropTarget.DropFilesForChecks([shortcut], point);
                Require(effect == 4 && pendingDropGeneration != 0 && IsDockVisible,
                    "DropHandoffKeepsDockVisible" + initialCount);
                if (initialCount == 24)
                {
                    await Task.Delay(100);
                    Require(dropTarget.DropFilesForChecks([folder], point) == 4, "ConsecutiveDropAcceptedWhileFirstLoads");
                }
                bool continuouslyVisible = true;
                bool pixelsStayedVisible = true;
                for (int i = 0; i < 150 && pendingDropGeneration != 0; i++)
                {
                    await Task.Delay(20); CheckVisibility();
                    continuouslyVisible &= IsDockVisible && renderer.IconCount >= oldEntries.Length;
                    pixelsStayedVisible &= PixelPresent();
                }
                Require(pendingDropGeneration == 0 && entries.Count == settings.Items.Count + 2,
                    "SlowDropLayoutCompletes" + initialCount);
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(20); CheckVisibility(); continuouslyVisible &= IsDockVisible;
                    pixelsStayedVisible &= PixelPresent();
                }
                Require(continuouslyVisible && hideTransitions == hidesBefore,
                    "NoHideDuringLoadOrLayoutHandoff" + initialCount);
                Require(visualSource.PositionChanges == presentationChangesBefore,
                    "PresentationWindowUnchangedDuringInputRegionResize" + initialCount);
                Require(pixelsStayedVisible, "PresentationPixelSurvivesNewIconLayout" + initialCount);
                var retained = renderer.IconIdentities.Where((_, i) => oldEntries.Contains(entries[i])).ToArray();
                Require(retained.Length == oldVisuals.Count && retained.Zip(oldVisuals).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    "ExistingIconVisualsSurviveDrop" + initialCount);
                var expandedInput = VerifyInputRegion();
                Require(expandedInput.Passed, "ExpandedDropRowKeepsShapedInput" + initialCount);
                IconLoadDelayForChecks = 0;
                await Task.Delay(1000); CheckVisibility();
                Require(autoHidden && pendingDropGeneration == 0, "AutoHideResumesAfterDrop" + initialCount);
            }
            settings.AutoHide = false; await WaitUntilShownForChecks();
            var fullVisuals = renderer.IconIdentities;
            Require(settings.Items.Count == 40 && dropTarget.DropFilesForChecks([folder], point) == 0
                && renderer.IconIdentities.Zip(fullVisuals).All(pair => ReferenceEquals(pair.First, pair.Second)) && IsDockVisible,
                "CapacityLimitDoesNotClearOrHideDock");
        }
        finally
        {
            checkingInteraction = false; CancelInteraction(); syntheticHover = false; pointerTargetForChecks = null;
            dragStartInterruptionForChecks = null;
            IconLoadDelayForChecks = 0; pointerOverForChecks = bottomEdgeForChecks = null; settings.AutoHide = originalAutoHide;
            SetSwitchingForChecks(null);
            SetShellDragForChecks(null);
            settings.Items = original.ToList(); await LoadIcons(); suppressHover = true;
        }
        return result;
    }

    private void CheckRestLayoutCache(Action<bool, string> require)
    {
        var original = (settings.IconSize, settings.Magnification, width, separatorBefore);
        try
        {
            var cached = RestGeometry;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { _ = RestGeometry; _ = RestDockArea; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            require(ReferenceEquals(cached, RestGeometry) && allocated == 0, "StableRestLayoutReusesGeometryWithoutAllocating");
            void Changed(string name)
            {
                var next = RestGeometry;
                var expected = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification, null, width / scale, separatorBefore: separatorBefore);
                require(!ReferenceEquals(cached, next) && next.Width == expected.Width && next.IconSize == expected.IconSize
                    && next.SeparatorX == expected.SeparatorX && next.Slots.SequenceEqual(expected.Slots), name);
                cached = next;
            }
            settings.IconSize = settings.IconSize < 80 ? settings.IconSize + 1 : settings.IconSize - 1; Changed("IconSizeInvalidatesRestLayout");
            settings.Magnification = settings.Magnification < 1.8 ? settings.Magnification + .01 : settings.Magnification - .01; Changed("MagnificationInvalidatesRestLayout");
            width += 32; Changed("DisplayWidthInvalidatesRestLayout");
            separatorBefore = separatorBefore == 1 ? 2 : 1; Changed("SeparatorInvalidatesRestLayout");
        }
        finally { (settings.IconSize, settings.Magnification, width, separatorBefore) = original; }
    }

    public InputRegionReport VerifyInputRegion()
    {
        nint nativeRegion = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (Native.GetWindowRgn(Handle, nativeRegion) == 0) throw new InvalidOperationException("Missing OS input region.");
            int samples = 0, blockedOutside = 0, visualBlocked = 0;
            for (int x = 0; x < width; x += Math.Max(4, (int)(8 * scale)))
            for (int y = 0; y < height; y += Math.Max(4, (int)(8 * scale)))
            {
                bool near = footprint.Any(shape => shape.Contains(x / scale, y / scale)
                    || shape.Contains((x - 2) / scale, (y - 2) / scale) || shape.Contains((x + 2) / scale, (y + 2) / scale));
                if (near) continue;
                samples++;
                nint hit = Native.WindowFromPoint(new() { X = left + x, Y = top + y });
                if (Native.PtInRegion(nativeRegion, x, y) || hit == Handle || hit == visualSource.Handle) blockedOutside++;
                if (hit == visualSource.Handle) visualBlocked++;
            }
            return new(samples, blockedOutside, visualBlocked);
        }
        finally { Native.DeleteObject(nativeRegion); }
    }

}

internal sealed record InputRegionReport(int OutsideSamples, int BlockedOutside, int PresentationBlockedOutside = 0)
{
    public bool Passed => OutsideSamples > 0 && BlockedOutside == 0;
}
