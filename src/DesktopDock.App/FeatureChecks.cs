using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopDock.Core;

namespace DesktopDock.App;

internal static class FeatureChecks
{
    private sealed class WeatherFixture : HttpMessageHandler
    {
        public readonly List<Uri> Requests = [];
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Offline) throw new HttpRequestException("Fixture offline.");
            string json = request.RequestUri!.Host.StartsWith("geocoding-", StringComparison.Ordinal)
                ? """{"results":[{"name":"Example City","admin1":"Example Region","latitude":34.67,"longitude":112.43,"population":1390000}]}"""
                : """{"current":{"temperature_2m":18.4,"apparent_temperature":17.1,"relative_humidity_2m":54,"wind_speed_10m":8.2,"weather_code":2,"is_day":1},"daily":{"temperature_2m_max":[23.1],"temperature_2m_min":[12.5]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 1.5), (int)Math.Ceiling(window.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public static async Task<Dictionary<string, bool>> Run(string data, bool inspect = false)
    {
        Directory.CreateDirectory(data);
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name) { checks[name] = value; if (!value) throw new InvalidOperationException("Desktop feature regression: " + name); }
        async Task WaitFor(Func<bool> condition, string name)
        {
            for (int i = 0; i < 80 && !condition(); i++) await Task.Delay(40);
            Require(condition(), name);
        }
        async Task Inspect(params Window[] windows)
        {
            if (!inspect) return;
            // Inspection tools omit tool windows from their inventory. Expose
            // only the synthetic fixture windows for this optional visual run.
            foreach (var window in windows)
            {
                window.ShowInTaskbar = true;
                nint hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                long style = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
                Native.SetWindowLongPtr(hwnd, -20, new((style | 0x40000L) & ~0x80L));
            }
            string gate = Path.Combine(data, "visual-inspection.pause");
            File.WriteAllText(gate, "Remove this local test marker to continue the fixture checks.");
            for (int i = 0; i < 600 && File.Exists(gate); i++) await Task.Delay(500);
        }
        string fixture = Path.Combine(data, "desktop-fixture"); Directory.CreateDirectory(fixture);
        for (int i = 0; i < 53; i++) File.WriteAllText(Path.Combine(fixture, $"Example {i:00}.txt"), "Desktop Dock fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "Example Folder"));
        var settings = new DockSettings(); int saves = 0, pinned = 0; DockItem? opened = null;
        var group = new DesktopGroup { Name = "项目" }; settings.DesktopGroups.Add(group);
        var panel = new DesktopPanel(settings, () => saves++, () => { }, item => opened = item, _ => pinned++, [fixture]);
        try
        {
            var presenting = panel.Present();
            await WaitFor(() => Children<Button>(panel).Any(x => x.ToolTip as string == "Example 02.txt"), "PanelGridAppearsBeforeIconUploadsFinish");
            var initialTile = Children<Button>(panel).Single(x => x.ToolTip as string == "Example 02.txt");
            await presenting;
            Require(ReferenceEquals(initialTile, Children<Button>(panel).Single(x => x.ToolTip as string == "Example 02.txt")), "IconUploadsPreserveExistingTileButtons");
            Require(panel.Open && panel.ItemsForChecks.Count == 54 && !panel.ShowInTaskbar, "DesktopPanelLoadsOnlyFixtureAndExcludesTaskbar");
            nint handle = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
            Require((Native.GetWindowLongPtr(handle, -20).ToInt64() & (0x80 | 0x40000)) == 0x80, "DesktopPanelExcludedFromAltTab");
            panel.FilterForChecks("Example 02"); Require(panel.FilteredCountForChecks == 1 && panel.PageForChecks == 0, "SearchFiltersAndResetsPage");
            var tile = Children<Button>(panel).Single(x => x.ToolTip as string == "Example 02.txt");
            var pin = (MenuItem)tile.ContextMenu.Items[0]; pin.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(pinned == 1, "PanelPinMenuUsesProductAction");
            var groups = (MenuItem)tile.ContextMenu.Items[1]; ((MenuItem)groups.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(group.Targets.Count == 1 && saves > 0, "PanelGroupMenuAssignsVirtualMembership");
            panel.FilterForChecks("", group.Id); Require(panel.FilteredCountForChecks == 1, "GroupFiltersDesktopEntries");
            panel.FilterForChecks("", "ungrouped"); Require(panel.FilteredCountForChecks == 53, "UngroupedOmitsGroupedEntry");
            panel.FilterForChecks("");
            panel.FilterForChecks("", "preset:documents"); Require(panel.FilteredCountForChecks == 53, "PresetDocumentsFilterUsesCachedCategories");
            panel.FilterForChecks("", "preset:folders"); Require(panel.FilteredCountForChecks == 1, "PresetFoldersFilterDoesNotMoveFiles");
            panel.FilterForChecks("");
            Capture(panel, Path.Combine(data, "desktop-panel.png"));
            var nextPage = Children<Button>(panel).Single(x => x.Content as string == "›"); nextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(panel.PageForChecks == 1, "PanelPaginatesBeyondFirstGrid");
            var appGrid = Children<UniformGrid>(panel).Single();
            appGrid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Require(panel.PageForChecks == 0, "WheelEventNavigatesPreviousPageThroughGrid");
            await Task.Delay(200);
            appGrid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Require(panel.PageForChecks == 1, "WheelEventNavigatesNextPageThroughGrid");
            panel.FilterForChecks("Example 02");
            tile = Children<Button>(panel).Single(x => x.ToolTip as string == "Example 02.txt");
            tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(200);
            var zoom = Children<Grid>(tile).Select(x => x.RenderTransform).OfType<ScaleTransform>().Single();
            Require(zoom.ScaleX > 1.14 && zoom.ScaleY > 1.14, "DesktopTileHoverScalesIconWithoutChangingHitArea");
            tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            await Task.Delay(180);
            Require(zoom.ScaleX == 1 && zoom.ScaleY == 1, "DesktopTileHoverSettlesBackToOriginalSize");
            panel.FilterForChecks("Example 02");
            tile = Children<Button>(panel).Single(x => x.ToolTip as string == "Example 02.txt"); tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !panel.IsVisible, "PanelLaunchDismissesAfterFade");
            Require(opened?.Target == Path.Combine(fixture, "Example 02.txt"), "PanelSingleClickSelectsOriginalDesktopTarget");
            Require(Directory.EnumerateFiles(fixture).Count() == 53, "GroupingAndLaunchLeaveDesktopFilesUntouched");
            await panel.Present(); await Task.Delay(200); panel.Dismiss(); await Task.Delay(40);
            double fading = panel.Opacity;
            Require(fading > 0 && fading < 1, "PanelDismissHasIntermediateOpacity");
            var reversed = panel.Present();
            Require(panel.Opacity > 0, "PanelReversePreservesCurrentOpacity");
            await reversed; await Task.Delay(200);
            Require(panel.Open && panel.Opacity == 1, "PanelReversesDismissWithoutLateHide");
            Require(!panel.AllowsTransparency && Native.DwmGetIntAttribute(new System.Windows.Interop.WindowInteropHelper(panel).Handle, 38, out int panelBackdrop, sizeof(int)) == 0 && panelBackdrop == 3, "PanelRequestsNativeDesktopAcrylic");
            await Inspect(panel);
            panel.Dismiss(); await WaitFor(() => !panel.IsVisible, "PanelToggleCanClose");
        }
        finally { panel.DisposeWindow(); }

        using var fixtureHttp = new WeatherFixture();
        using (var widgets = new DesktopWidgets(settings, () => saves++, data, network: true, new WeatherService(fixtureHttp)))
        {
            widgets.Apply();
            await WaitFor(() => !widgets.WeatherFetchingForChecks, "WeatherFixtureCompletesAsynchronously");
            var windows = widgets.WindowsForChecks.ToArray();
            Require(windows.Length == 2 && windows.All(x => x.Kind != "calendar" && x.IsVisible && !x.Topmost && !x.ShowInTaskbar), "OnlyClockAndWeatherAreVisibleBelowApplications");
            Require(windows.All(x => !x.AllowsTransparency && Native.DwmGetIntAttribute(x.Handle, 38, out int backdrop, sizeof(int)) == 0 && backdrop == 3), "WidgetsRequestNativeDesktopAcrylic");
            var clock = windows.Single(x => x.Kind == "clock");
            var timeText = Children<TextBlock>(clock).Single(x => x.Text.Length == 8 && x.Text.Count(c => c == ':') == 2);
            string beforeTick = timeText.Text; await Task.Delay(1250);
            Require(timeText.Text != beforeTick && timeText.Text.Length == 8, "ClockDisplaysSecondsAndUpdatesWithoutManualRefresh");
            Require(windows.All(x => (Native.GetWindowLongPtr(x.Handle, -20).ToInt64() & (0x80 | 0x08000000 | 0x40000)) == (0x80 | 0x08000000)), "WidgetsDoNotActivateOrEnterAltTab");
            var weather = windows.Single(x => x.Kind == "weather");
            Require(Children<TextBlock>(weather).Any(x => x.Text == "18°") && Children<TextBlock>(weather).Any(x => x.Text.Contains("Example Region")), "WeatherShowsApiValueAndResolvedRegion");
            Require(Uri.UnescapeDataString(fixtureHttp.Requests[0].Query).Contains("洛阳市"), "ChineseCityResolutionAvoidsNamesakeTown");
            Require(fixtureHttp.Requests[1].Query.Contains("latitude=34.67") && fixtureHttp.Requests[1].Query.Contains("longitude=112.43"), "WeatherFetchUsesSelectedCityCoordinates");
            foreach (var window in windows) Capture(window, Path.Combine(data, window.Kind + "-widget.png"));
            await Inspect(windows);
            var ownSettings = new Window { Title = "Desktop Dock · Fixture settings", Width = 400, Height = 320, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
            try
            {
                ownSettings.Show(); widgets.RestackForChecks();
                nint settingsHandle = new System.Windows.Interop.WindowInteropHelper(ownSettings).Handle;
                var zOrder = new List<nint>(); Native.EnumWindows((window, _) => { zOrder.Add(window); return true; }, 0);
                Require(windows.All(x => zOrder.IndexOf(x.Handle) > zOrder.IndexOf(settingsHandle)), "WidgetsRemainBelowOwnSettingsDialog");
            }
            finally { ownSettings.Close(); }
            clock.Left = 50000; clock.Top = 50000; widgets.RememberPositionForChecks(clock);
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea; Native.GetWindowRect(clock.Handle, out var bounds);
            Require(bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right + 2 && bounds.Bottom <= work.Bottom + 2, "DraggedWidgetIsClampedInsideWorkArea");
            Require(settings.Widgets.Positions.ContainsKey("clock"), "WidgetPlacementPersistsAfterMove");
            fixtureHttp.Offline = true; await widgets.RefreshWeather();
            Require(Children<TextBlock>(weather).Any(x => x.Text.Contains("上次更新")) && Children<TextBlock>(weather).Any(x => x.Text == "18°"), "OfflineWeatherRetainsLastRealValue");
            int requestsBeforeRemove = fixtureHttp.Requests.Count;
            widgets.Remove("clock"); Require(widgets.WindowsForChecks.Count == 1 && !settings.Widgets.Clock, "WidgetCanBeRemovedIndependently");
            Require(!widgets.ClockTickingForChecks, "RemovingClockStopsSecondTimer");
            Require(fixtureHttp.Requests.Count == requestsBeforeRemove, "EditingOtherWidgetReusesFreshWeatherCache");
            settings.Widgets.City = "Unknown City"; widgets.Apply(); await WaitFor(() => !widgets.WeatherFetchingForChecks, "FailedWeatherFetchSettles");
            Require(Children<TextBlock>(widgets.WindowsForChecks.Single(x => x.Kind == "weather")).Any(x => x.Text == "天气暂不可用"), "MissingWeatherShowsUnavailableInsteadOfSampleData");
        }
        string probeData = Path.Combine(data, "settings-identity");
        using var probe = RegressionProbe.Start("normal", probeData);
        try
        {
            await RegressionProbe.WaitReady(probe, probeData); nint window = RegressionProbe.Handle(probeData);
            string link = Path.Combine(data, "Settings example.lnk");
            RegressionProbe.CreateShortcut(link, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "ms-settings:");
            var entries = new[] { new DockItem("Settings", "ms-settings:"), new DockItem("Settings page", "ms-settings:display"), new DockItem("Settings shortcut", link), new DockItem("Packaged Settings", "shell:AppsFolder\\" + ApplicationTarget.WindowsSettingsId) };
            foreach (var entry in entries)
            {
                var target = ApplicationTarget.Resolve(entry.Target, entry.Arguments);
                Require(!target.SpecificWindow && target.AppId == ApplicationTarget.WindowsSettingsId, entry.Name + "UsesSharedApplicationIdentity");
                using var launcher = new LaunchCoordinator(hwnd => hwnd == window ? ApplicationTarget.WindowsSettingsId : null);
                launcher.Launch(entry, foregroundAtPress: window, allowLaunch: false);
                await WaitFor(() => Native.IsIconic(window), entry.Name + "RepeatClickMinimizesHostedWindow");
                launcher.Launch(entry, allowLaunch: false);
                await WaitFor(() => !Native.IsIconic(window) && LaunchCoordinator.Windows().Contains(window)
                    && Native.GetAncestor(Native.GetForegroundWindow(), 3) == window, entry.Name + "ClickRestoresSameWindow");
                Require(launcher.LastWindow == window && !launcher.IsWatching, entry.Name + "DoesNotRelaunchSettings");
            }
        }
        finally { RegressionProbe.Close(probe); }
        var sampleSettings = new DockSettings { Items = [new("Example application", "ms-settings:"), new("Example folder", "shell:MyComputerFolder")], Widgets = new() { City = "Example City" } };
        int applied = 0;
        using var taskbar = new TaskbarEnhancement(Path.Combine(data, "settings-ui"));
        var preferences = new SettingsWindow(sampleSettings, () => applied++, () => { }, () => { }, taskbar);
        try
        {
            preferences.Show();
            foreach (var (index, name) in new[] { (0, "appearance"), (1, "widgets"), (2, "taskbar"), (3, "pinned") })
            {
                preferences.SelectPageForChecks(index); preferences.UpdateLayout();
                Capture(preferences, Path.Combine(data, "settings-" + name + ".png"));
                Require(preferences.IsVisible && Children<ScrollViewer>(preferences).Any(), "SettingsPageRenders" + name);
            }
            preferences.SelectPageForChecks(0); preferences.UpdateLayout();
            Children<Slider>(preferences).First().Value = 56;
            Require(sampleSettings.IconSize == 56 && applied == 1, "SettingsSliderStillAppliesImmediately");
            preferences.SelectPageForChecks(1); preferences.UpdateLayout();
            var time = Children<CheckBox>(preferences).Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "时间");
            time.IsChecked = false;
            Require(!sampleSettings.Widgets.Clock && applied == 2, "SettingsSwitchPreservesWidgetAction");
            sampleSettings.Widgets.Clock = true; preferences.RefreshWidgets();
            Require(time.IsChecked == true && applied == 2, "SettingsRefreshDoesNotApplyAgain");
            preferences.SelectPageForChecks(3); preferences.UpdateLayout();
            Children<ListBox>(preferences).Single().SelectedIndex = 0;
            Children<Button>(preferences).Single(x => x.Content as string == "移除所选").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(sampleSettings.Items.Count == 1, "SettingsRemoveOnlyChangesPinnedList");
        }
        finally { preferences.Close(); }
        var brand = new FormatConvertedBitmap(AppBrand.Image(), PixelFormats.Pbgra32, null, 0);
        byte[] brandPixels = new byte[brand.PixelWidth * brand.PixelHeight * 4]; brand.CopyPixels(brandPixels, brand.PixelWidth * 4, 0);
        Require(brandPixels[3] == 0 && brandPixels[^1] == 0, "BrandHasTransparentOutsideBackground");
        int pale = 0;
        for (int i = 0; i < brandPixels.Length; i += 4) if (brandPixels[i + 3] > 245 && brandPixels[i] > 190 && brandPixels[i + 1] > 220 && brandPixels[i + 2] > 230) pale++;
        Require(pale > 1000, "BrandRetainsInternalPaleStripe");
        return checks;
    }
}
