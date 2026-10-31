using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class Controller
{
    public async Task RunEnvironmentTest()
    {
        dock.UseOcclusionWindowsForChecks(() => []);
        var checks = await dock.CheckEnvironment(Path.Combine(data, "environment-checks"));
        File.WriteAllText(Path.Combine(data, "environment-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }
    public async Task RunDistributionTest()
    {
        var checks = await DistributionChecks.Run(Path.Combine(data, "distribution-checks"));
        File.WriteAllText(Path.Combine(data, "distribution-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }
    public async Task RunWindowPreviewTest(bool inspect = false)
    {
        dock.UseOcclusionWindowsForChecks(() => []);
        var checks = await dock.CheckWindowPreviews(Path.Combine(data, "preview-checks"), inspect);
        File.WriteAllText(Path.Combine(data, "window-preview-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }

    public async Task RunDragTest(bool holdForInspection = false)
    {
        dock.UseOcclusionWindowsForChecks(() => []);
        await dock.WaitUntilShownForChecks();
        var checks = await dock.CheckDragging(Path.Combine(data, "drag-checks"), holdForInspection);
        File.WriteAllText(Path.Combine(data, "drag-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }

    public async Task RunClickHoverTest()
    {
        dock.UseOcclusionWindowsForChecks(() => []);
        string probeData = Path.Combine(data, "hover-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeData);
        string host = Environment.ProcessPath!;
        string prefix = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + typeof(Program).Assembly.Location + "\" " : "";
        var item = new DockItem("Example hovered app", host, prefix + "--probe-window normal --probe-delay-ms 1000 --data-dir \"" + probeData + "\"");
        try
        {
            await dock.PinForCheck(item);
            var checks = await dock.DoubleClickHoveredForCheck(item);
            File.WriteAllText(Path.Combine(data, "click-hover-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            for (int i = 0; i < 60 && !File.Exists(Path.Combine(probeData, "ready.json")) && dock.LaunchWatcherActive; i++) await Task.Delay(50);
            if (File.Exists(Path.Combine(probeData, "ready.json"))) Native.PostMessage(RegressionProbe.Handle(probeData), 0x0010, 0, 0);
        }
        Quit();
    }

    public async Task RunBrowserWindowTest()
    {
        dock.UseOcclusionWindowsForChecks(() => []);
        var checks = await BrowserWindowChecks.Run(Path.Combine(data, "browser-checks"));
        File.WriteAllText(Path.Combine(data, "browser-window-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }

    public async Task RunFeatureTest(bool inspect = false)
    {
        dock.UseOcclusionWindowsForChecks(() => []);
        var checks = await FeatureChecks.Run(Path.Combine(data, "feature-checks"), inspect);
        File.WriteAllText(Path.Combine(data, "feature-report.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }

    public async Task RunSmokeTest()
    {
        using var process = Process.GetCurrentProcess();
        string probeData = Path.Combine(data, "probe-" + Guid.NewGuid().ToString("N"));
        using var anchor = RegressionProbe.Start("normal", probeData);
        var testWindows = new List<nint>();
        var children = new List<nint>();
        void Require(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
        async Task WaitFor(Func<bool> condition, string description, int attempts = 60)
        {
            for (int i = 0; i < attempts; i++) { if (condition()) return; await Task.Delay(50); }
            throw new InvalidOperationException(description);
        }
        Task Focus(nint window) => WaitFor(() =>
        {
            Native.SetForegroundWindow(window);
            return Native.GetAncestor(Native.GetForegroundWindow(), 3) == window;
        }, "Test window could not acquire foreground.");
        try
        {
            dock.IsolateInputForChecks(true);
            await RegressionProbe.WaitReady(anchor, probeData);
            nint anchorWindow = RegressionProbe.Handle(probeData);
            testWindows.Add(anchorWindow);
            dock.SetBottomEdgeForChecks(false); dock.SetPointerOverForChecks(false);
            dock.UseOcclusionWindowsForChecks(() => testWindows.ToArray());
            await dock.WaitUntilShownForChecks();
            Require(dock.IsDockVisible && dock.ExcludedFromAltTab, "Initial visibility or Alt+Tab exclusion failed.");
            var visibilityMotionChecks = await dock.CheckVisibilityMotion();
            var appStateChecks = new Dictionary<string, bool>();
            void StateCheck(bool value, string name) { appStateChecks[name] = value; Require(value, "Application status: " + name); }

            await RegressionProbe.SetMode(anchor, probeData, "covered");
            dock.RefreshOcclusion();
            Require(!dock.IsDockVisible, "Ordinary overlapping window did not hide Dock.");
            var shellDropPoint = dock.ShellDropPointForChecks;
            dock.SetShellDragForChecks(new(true, true, true, new() { X = shellDropPoint.X, Y = shellDropPoint.Y - 120 }));
            Require(!dock.IsDockVisible, "Ordinary Explorer capture must not override window coverage.");
            dock.SetShellDragForChecks(new(true, false, true, shellDropPoint));
            await dock.WaitUntilShownForChecks();
            Require(dock.IsDockVisible, "Shell drag did not override coverage before OLE enter.");
            await Task.Delay(700); dock.CheckVisibility();
            Require(dock.IsDockVisible, "Dock disappeared during external drag.");
            dock.SetShellDragForChecks(new(false, false, false, new()));
            await Task.Delay(900); dock.CheckVisibility();
            Require(!dock.IsDockVisible, "Coverage hiding did not resume after drag release: " + dock.VisibilityStateForChecks);
            dock.SetShellDragForChecks(null);
            dock.SetBottomEdgeForChecks(true);
            Require(!dock.IsDockVisible, "A brief bottom edge touch must not reveal Dock immediately.");
            await dock.WaitUntilShownForChecks();
            Require(dock.IsDockVisible, "Bottom edge dwell did not reveal covered Dock.");
            dock.SetBottomEdgeForChecks(false);
            await Task.Delay(900); dock.CheckVisibility();
            bool recallTimedOut = !dock.IsDockVisible;
            Require(recallTimedOut, "Dock did not hide after the edge leave delay.");
            dock.SetBottomEdgeForChecks(true); await dock.WaitUntilShownForChecks();
            dock.SetPointerOverForChecks(true); dock.SetBottomEdgeForChecks(false);
            await Task.Delay(1500); dock.CheckVisibility();
            bool interactionResetTimeout = dock.IsDockVisible;
            dock.SetPointerOverForChecks(false); await Task.Delay(900); dock.CheckVisibility();
            Require(interactionResetTimeout && !dock.IsDockVisible, "Hover preservation or leave hiding failed.");

            dock.SetBottomEdgeForChecks(true); await dock.WaitUntilShownForChecks();
            dock.SetBottomEdgeForChecks(false);
            bool menuStaysOpen = await dock.CheckMenuPersistence();
            Require(menuStaysOpen, "Native context menu did not stay open while the Dock was covered.");
            await Task.Delay(900); dock.CheckVisibility();

            dock.SetSwitchingForChecks(true);
            await RegressionProbe.SetMode(anchor, probeData, "normal");
            dock.RefreshOcclusion();
            Require(!dock.IsDockVisible, "Dock flashed while task switching.");
            dock.SetSwitchingForChecks(false);
            bool noImmediateFlash = !dock.IsDockVisible;
            await dock.WaitUntilShownForChecks();
            Require(noImmediateFlash && dock.IsDockVisible, "Task-switch stabilization failed.");
            dock.SetSwitchingForChecks(null);

            dock.InvokeSettingsEntry(); await Task.Delay(200);
            bool settingsOpened = settingsWindow is { IsVisible: true };
            Require(settingsOpened, "Settings did not open.");
            nint settingsHandle = new System.Windows.Interop.WindowInteropHelper(settingsWindow!).Handle;
            bool settingsExcluded = (Native.GetWindowLongPtr(settingsHandle, -20).ToInt64() & (0x80 | 0x40000)) == 0x80;
            testWindows.Add(settingsHandle);
            settingsWindow!.WindowState = WindowState.Maximized;
            await Task.Delay(200); dock.RefreshOcclusion();
            await Task.Delay(350); dock.CheckVisibility();
            bool settingsOverlapsDock = WindowAvoidance.Overlaps(WindowOcclusion.Frame(settingsHandle), WindowOcclusion.Frame(dock.Handle));
            bool settingsKeepsDockVisible = settingsOverlapsDock && dock.IsDockVisible;
            Require(settingsKeepsDockVisible, "Dock settings covering the Dock must not hide it.");
            await Focus(settingsHandle);
            StateCheck(dock.SettingsStateForChecks == new DockAppState(true, true), "SettingsHasRunningAndActiveState");
            await RegressionProbe.SetMode(anchor, probeData, "covered");
            dock.RefreshOcclusion();
            bool otherWindowStillHides = !dock.IsDockVisible;
            Require(otherWindowStillHides, "Ignoring Dock settings must not ignore another application's coverage.");
            await RegressionProbe.SetMode(anchor, probeData, "normal");
            settingsWindow.WindowState = WindowState.Normal;
            dock.RefreshOcclusion(); await dock.WaitUntilShownForChecks();
            await Focus(settingsHandle);
            dock.InvokeSettingsEntry(); await Task.Delay(150);
            bool settingsMinimized = settingsWindow?.WindowState == WindowState.Minimized;
            StateCheck(dock.SettingsStateForChecks == new DockAppState(true, false), "MinimizedSettingsKeepsRunningMarker");
            dock.InvokeSettingsEntry(); await Task.Delay(200);
            bool settingsRestored = settingsWindow is { IsVisible: true, WindowState: WindowState.Normal };
            Require(settingsExcluded && settingsMinimized && settingsRestored, "Settings taskbar toggle or Alt+Tab exclusion failed.");
            testWindows.Remove(settingsHandle);
            settingsWindow?.Close();
            StateCheck(dock.SettingsStateForChecks == default, "ClosedSettingsRemovesRunningMarker");

            string host = Environment.ProcessPath!;
            string prefix = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "\"" + typeof(Program).Assembly.Location + "\" " : "";
            var applicationChecks = new Dictionary<string, bool>();
            var clickHoverChecks = new Dictionary<string, bool>();
            foreach (bool shortcut in new[] { false, true })
            {
                string launchData = Path.Combine(probeData, shortcut ? "shortcut" : "executable");
                Directory.CreateDirectory(launchData);
                string arguments = prefix + "--probe-window normal --probe-delay-ms 1000 --allow-foreground " + Environment.ProcessId + " --data-dir \"" + launchData + "\"";
                DockItem item;
                if (shortcut)
                {
                    string link = Path.Combine(probeData, "Example.lnk");
                    RegressionProbe.CreateShortcut(link, host, arguments);
                    item = new("Example shortcut", link);
                }
                else item = new("Example executable", host, arguments);
                await dock.PinForCheck(item);
                dock.Summon(); await dock.WaitUntilShownForChecks();
                dock.ClickForCheck(item);
                string ready = Path.Combine(launchData, "ready.json");
                bool singleIgnored = dock.LastAppAction == AppClickAction.Ignore && !dock.LaunchWatcherActive;
                await Task.Delay((int)Native.GetDoubleClickTime() + 40);
                Require(singleIgnored && !File.Exists(ready), "A single click launched an unopened application.");
                foreach (var check in await dock.DoubleClickHoveredForCheck(item))
                    clickHoverChecks[(shortcut ? "Shortcut" : "Executable") + check.Key] = check.Value;
                bool doubleLaunched = dock.LastAppAction == AppClickAction.Launch && dock.LaunchWatcherActive;
                dock.ClickForCheck(item);
                dock.ClickForCheck(item, nativeDoubleClick: true);
                bool pendingPreserved = dock.LastAppAction == AppClickAction.Ignore && dock.LaunchWatcherActive;
                Require(doubleLaunched && pendingPreserved, "Double-click launch or duplicate-launch suppression failed.");
                await WaitFor(() => File.Exists(ready), "Application did not launch.");
                await Task.Delay(100);
                nint child = RegressionProbe.Handle(launchData);
                children.Add(child); testWindows.Add(child);
                var originalFrame = WindowOcclusion.Frame(child);
                await WaitFor(() => !dock.LaunchWatcherActive, "App identification watcher did not stop.");
                Require(dock.LastAppWindow == child, "Wrong app window was associated.");
                await Focus(child);
                StateCheck(dock.AppStateForChecks(item) == new DockAppState(true, true), (shortcut ? "Shortcut" : "Executable") + "BoundWindowHasActiveState");
                bool notMoved = WindowOcclusion.Frame(child) == originalFrame;
                dock.ClickForCheck(item);
                var action = dock.LastAppAction;
                await WaitFor(() => Native.IsIconic(child), "Repeated active app click did not minimize: " + action);
                StateCheck(dock.AppStateForChecks(item) == new DockAppState(true, false), (shortcut ? "Shortcut" : "Executable") + "MinimizedWindowHasRunningState");
                dock.ClickForCheck(item);
                bool restoring = dock.LastAppAction == AppClickAction.Restore;
                await WaitFor(() => !Native.IsIconic(child), "App did not restore.");
                await WaitFor(() => Native.GetAncestor(Native.GetForegroundWindow(), 3) == child, "Restored test app did not acquire foreground.");
                dock.ClickForCheck(item, OpenSettings);
                bool pressForegroundToggle = dock.LastAppAction == AppClickAction.Minimize;
                await WaitFor(() => Native.IsIconic(child), "Focus change between mouse down/up broke minimization.");
                Require(pressForegroundToggle, "Mouse-down foreground was not used for app toggle.");
                settingsWindow?.Close();
                dock.ClickForCheck(item);
                await WaitFor(() => !Native.IsIconic(child), "App did not restore after focus-change test.");
                OpenSettings(); await Task.Delay(150);
                settingsWindow!.Activate();
                nint backgroundController = new System.Windows.Interop.WindowInteropHelper(settingsWindow).Handle;
                await WaitFor(() => Native.GetAncestor(Native.GetForegroundWindow(), 3) == backgroundController, "Test controller could not acquire foreground.");
                dock.ClickForCheck(item);
                bool activated = dock.LastAppAction == AppClickAction.Activate;
                await WaitFor(() => Native.GetAncestor(Native.GetForegroundWindow(), 3) == child, "Background app did not activate.");
                settingsWindow?.Close();
                applicationChecks[shortcut ? "ShortcutToggleAndNoMove" : "ExecutableToggleAndNoMove"] = restoring && notMoved;
                applicationChecks[shortcut ? "ShortcutBackgroundActivate" : "ExecutableBackgroundActivate"] = activated;
                applicationChecks[shortcut ? "ShortcutPressForegroundToggle" : "ExecutablePressForegroundToggle"] = pressForegroundToggle;
                applicationChecks[shortcut ? "ShortcutSingleDoesNotLaunch" : "ExecutableSingleDoesNotLaunch"] = singleIgnored;
                applicationChecks[shortcut ? "ShortcutDoubleLaunchesOnce" : "ExecutableDoubleLaunchesOnce"] = doubleLaunched && pendingPreserved;
                if (!shortcut)
                {
                    const string exampleId = "Example.DesktopSuite_0123456789abc!App";
                    var packaged = ApplicationTarget.Resolve("explorer.exe", "shell:AppsFolder\\" + exampleId);
                    var direct = ApplicationTarget.Resolve("shell:AppsFolder\\" + exampleId, "");
                    string appLink = Path.Combine(probeData, "Packaged example.lnk");
                    RegressionProbe.CreateShortcut(appLink, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:AppsFolder\\" + exampleId);
                    var linked = ApplicationTarget.Resolve(appLink, "");
                    bool appActivation = new[] { packaged, direct, linked }.All(x => x.AppId == exampleId && !x.SpecificWindow);
                    using var identityLauncher = new LaunchCoordinator(hwnd => hwnd == child ? exampleId : "Example.OtherSuite_0123456789abc!App");
                    bool identityMatch = identityLauncher.Matches(child, 0, packaged) && !identityLauncher.Matches(anchorWindow, 0, packaged);
                    var packagedItem = new DockItem("Example packaged app", "explorer.exe", "shell:AppsFolder\\" + exampleId);
                    identityLauncher.Launch(packagedItem, foregroundAtPress: child, allowLaunch: false);
                    bool packageMinimized = identityLauncher.LastWindow == child && identityLauncher.LastAction == AppClickAction.Minimize;
                    await WaitFor(() => Native.IsIconic(child), "Packaged app identity did not minimize the matching window.");
                    identityLauncher.Launch(packagedItem, allowLaunch: false);
                    bool packageRestored = identityLauncher.LastWindow == child && identityLauncher.LastAction == AppClickAction.Restore;
                    await WaitFor(() => !Native.IsIconic(child), "Packaged app identity did not restore the matching window.");
                    applicationChecks["AppsFolderAndShortcutIdentifyApplication"] = appActivation;
                    applicationChecks["PackagedIdentityDoesNotMatchOtherApp"] = identityMatch;
                    applicationChecks["PackagedIdentityMinimizesAndRestores"] = packageMinimized && packageRestored;
                    Require(appActivation && identityMatch && packageMinimized && packageRestored, "Packaged application association regression.");
                }
                bool menuClosed = dock.CloseFromMenuForChecks(item);
                await WaitFor(() => !Native.IsWindow(child), "Close-window menu action did not close the application window.");
                applicationChecks[shortcut ? "ShortcutMenuClose" : "ExecutableMenuClose"] = menuClosed;
                Require(menuClosed, "Close-window menu is missing or application-exit entry is still present.");
                StateCheck(dock.AppStateForChecks(item) == default, (shortcut ? "Shortcut" : "Executable") + "ClosedWindowClearsBoundState");
                testWindows.Remove(child);
                Require(restoring && notMoved && activated, "Application regression: " + JsonSerializer.Serialize(new { Shortcut = shortcut, Restored = restoring, NoMove = notMoved, BackgroundActivated = activated, Action = dock.LastAppAction.ToString() }));
            }

            var browserChecks = await BrowserWindowChecks.Run(Path.Combine(probeData, "pre-existing-browser"));
            await RegressionProbe.SetMode(anchor, probeData, "normal");
            dock.RefreshOcclusion(); await dock.WaitUntilShownForChecks();
            foreach (var check in await dock.CheckAppStatePresentation()) appStateChecks[check.Key] = check.Value;
            await dock.WaitUntilShownForChecks();
            var dragChecks = await dock.CheckDragging(Path.Combine(data, "drag-checks"));
            dock.SetBottomEdgeForChecks(false); dock.SetPointerOverForChecks(false);
            dock.ClearFrameSamples();
            for (int i = 0; i < 180; i++) { dock.PreviewWave(.5 + Math.Sin(i * .045) * .42); await Task.Delay(8); }
            var hoveredInput = dock.VerifyInputRegion();
            dock.ResetWave(); await Task.Delay(900);
            var input = dock.VerifyInputRegion();
            Require(dock.IsDockVisible && input.Passed && hoveredInput.Passed, "Shaped input region regression failed.");
            var intervals = dock.FrameIntervals.Skip(2).OrderBy(x => x).ToArray();
            Require(intervals.Length > 0 && intervals[intervals.Length / 2] < 25, "Animation update cadence regression.");

            var avoidance = new Dictionary<string, bool>();
            foreach (string mode in new[] { "covered", "maximized", "fullscreen" })
            {
                await RegressionProbe.SetMode(anchor, probeData, mode);
                dock.RefreshOcclusion();
                avoidance[mode] = !dock.IsDockVisible;
                await RegressionProbe.SetMode(anchor, probeData, "normal");
                dock.RefreshOcclusion(); await dock.WaitUntilShownForChecks();
            }
            Require(avoidance.Values.All(x => x) && dock.IsDockVisible, "Coverage, fullscreen or stable reveal regression: "
                + JsonSerializer.Serialize(avoidance) + " " + dock.VisibilityStateForChecks);
            process.Refresh(); var initialCpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            await Task.Delay(5000); process.Refresh();
            var report = new
            {
                Version = AppVersion.Current, dock.HardwareRendering, dock.HostBackdrop, dock.IconCount,
                BottomEdgeReveals = true, EdgeLeaveHides = recallTimedOut, HoverKeepsVisible = interactionResetTimeout,
                ContextMenuStaysOpen = menuStaysOpen,
                VisibilityMotionChecks = visibilityMotionChecks,
                DockExcludedFromAltTab = dock.ExcludedFromAltTab, SettingsExcludedFromAltTab = settingsExcluded, NoTaskSwitchFlash = noImmediateFlash,
                SettingsOpened = settingsOpened, SettingsMinimized = settingsMinimized, SettingsRestored = settingsRestored,
                SettingsKeepsDockVisible = settingsKeepsDockVisible, OtherWindowStillHides = otherWindowStillHides,
                ApplicationChecks = applicationChecks, ClickHoverChecks = clickHoverChecks, BrowserChecks = browserChecks, AppStateChecks = appStateChecks, Avoidance = avoidance,
                DragChecks = dragChecks,
                InputRegion = input, HoveredInputRegion = hoveredInput,
                IdleCpuPercent = (process.TotalProcessorTime - initialCpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                WorkingSetMiB = process.WorkingSet64 / 1048576d, SampleSeconds = watch.Elapsed.TotalSeconds,
                MotionFrames = intervals.Length, MotionMedianMs = intervals[intervals.Length / 2],
                MotionP95Ms = intervals[(int)((intervals.Length - 1) * .95)],
                Notes = "Controlled test windows; app update cadence is not GPU presentation. Alt+Tab styles and transition policy checked without injecting keys."
            };
            File.WriteAllText(Path.Combine(data, "smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            dock.IsolateInputForChecks(false);
            foreach (nint child in children) if (Native.IsWindow(child)) Native.PostMessage(child, 0x0010, 0, 0);
            RegressionProbe.Close(anchor);
        }
        Quit();
    }
}
