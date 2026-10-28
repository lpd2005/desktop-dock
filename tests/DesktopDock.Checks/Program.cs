using DesktopDock.Core;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

foreach (double width in new[] { 800d, 1920, 3840 })
foreach (int count in new[] { 1, 8, 20, 40 })
foreach (double? pointer in new double?[] { null, width / 2, 0, width - 1 })
{
    var g = DockLayout.Calculate(count, 52, 1.6, pointer, width);
    Check(g.Slots.All(x => double.IsFinite(x.X) && x.Size > 0), "Valid finite geometry");
    Check(g.Width <= width + .01, "Magnification stays on-screen");
    for (int i = 1; i < count; i++)
        Check(g.Slots[i].X >= g.Slots[i - 1].X + g.Slots[i - 1].Size, "No overlapping hit targets");
    Check(Math.Abs((g.Slots[0].X - DockGeometry.Padding) - (width - g.Width) / 2) < .01, "Centered geometry");
    var footprint = DockFootprint.Create(g, width, 190);
    Check(!footprint.Any(s => s.Contains(0, 150)), "No screen-wide input strip");
    Check(!footprint.Any(s => s.Contains(width / 2, 1)), "No invisible input area above Dock");
    for (int i = 0; i < g.Slots.Length; i++)
        Check(g.HitIcon(g.Slots[i].X + g.Slots[i].Size / 2, g.Baseline(190) - g.Slots[i].Size / 2, 190) == i, "Displayed positions equal hit targets");
}
var rest = DockLayout.Calculate(9, 52, 1.5, null, 1200);
var hover = DockLayout.Calculate(9, 52, 1.5, 600, 1200);
Check(hover.Slots[4].Size > rest.Slots[4].Size, "Hover magnifies the nearest icon");
Check(Math.Abs(hover.Slots[3].Size - hover.Slots[5].Size) < .001, "Symmetric wave");
var near = DockLayout.Calculate(9, 52, 1.5, 600.1, 1200);
Check(Math.Abs(near.Slots[4].X - hover.Slots[4].X) < 1, "Continuous mouse response");

string folder = Path.Combine(Path.GetTempPath(), "DesktopDock-check-" + Guid.NewGuid().ToString("N"));
string file = Path.Combine(folder, "settings.json");
try
{
    Directory.CreateDirectory(folder);
    File.WriteAllText(file, "{invalid");
    Check(SettingsStore.Load(file).IconSize == 48, "Corrupt settings recover to defaults");
    var settings = new DockSettings { IconSize = 1000, Magnification = -1, Items = [new("Example", "example.exe"), new("Duplicate", "EXAMPLE.exe")] };
    SettingsStore.Save(file, settings);
    var loaded = SettingsStore.Load(file);
    Check(loaded.IconSize == 80 && loaded.Magnification == 1, "Untrusted settings bounded");
    Check(loaded.Items.Count == 1, "Duplicate targets removed");
    Check(!File.Exists(file + ".tmp"), "Atomic save completed");
}
finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
var sixty = new SpringMotion();
var oneTwenty = new SpringMotion();
for (int i = 0; i < 60; i++) sixty.Step(100, 1d / 60, 36);
for (int i = 0; i < 120; i++) oneTwenty.Step(100, 1d / 120, 36);
Check(Math.Abs(sixty.Value - oneTwenty.Value) < .001, "Frame-rate independent spring");
var monotonic = new SpringMotion();
double previous = 0;
for (int i = 0; i < 100; i++)
{
    monotonic.Step(1, 1d / 120, 36);
    Check(monotonic.Value >= previous && monotonic.Value <= 1, "No hover overshoot");
    previous = monotonic.Value;
}
Check(!monotonic.Step(1, 1d / 60, 36), "Animation reaches rest and stops");
var dockArea = new WindowBounds(600, 950, 1320, 1030);
Check(WindowAvoidance.Overlaps(new(0, 0, 1920, 1080), dockArea), "Full-screen window overlaps Dock");
Check(WindowAvoidance.Overlaps(new(700, 800, 1000, 1000), dockArea), "Ordinary window partially covering Dock hides it");
Check(!WindowAvoidance.Overlaps(new(0, 0, 1920, 950), dockArea), "Touching top edge does not count as overlap");
Check(!WindowAvoidance.Overlaps(new(1920, 0, 3840, 1080), dockArea), "Another monitor does not cover Dock");
var visibility = new DockVisibility();
Check(!visibility.ShouldShow(true, false, 0), "Covered Dock is hidden");
Check(!visibility.ShouldShow(true, false, 1, atEdge: true), "Brief bottom edge touch does not reveal");
Check(visibility.ShouldShow(true, false, 1.2, atEdge: true), "Bottom edge dwell reveals covered Dock");
Check(visibility.ShouldShow(true, false, 8, atEdge: true), "Staying on the bottom edge keeps Dock available");
Check(visibility.ShouldShow(true, false, 8.5, overDock: true), "Moving onto icons preserves the reveal");
Check(visibility.ShouldShow(true, false, 9.2), "Leaving has a grace interval");
Check(!visibility.ShouldShow(true, false, 9.3), "Leaving for 750 ms hides covered Dock");
Check(!visibility.ShouldShow(false, false, 11), "Uncovered reveal waits for stability");
Check(visibility.ShouldShow(false, false, 11.3), "Stable uncovered Dock reappears");
Check(!visibility.ShouldShow(false, true, 12), "Task switching suppresses reveal");
Check(!visibility.ShouldShow(false, false, 12.1), "No immediate flash after task switch");
Check(visibility.ShouldShow(false, false, 12.4), "Reveal resumes only after stable transition");
Check(!visibility.ShouldShow(true, false, 12.5), "Covering again hides immediately");
visibility.Summon(13);
Check(visibility.ShouldShow(false, false, 13), "Tray can still show Dock");
Check(visibility.ShouldShow(false, false, 14), "No blink when temporary reveal ends after coverage clears");
Check(visibility.ShouldShow(true, false, 15, interacting: true), "Menu or drag keeps covered Dock visible");
Check(visibility.ShouldShow(true, false, 25, interacting: true), "Long interaction never times out");
Check(!visibility.ShouldShow(true, true, 26, atEdge: true, interacting: true), "Task switch suppresses even active interaction");
Check(!visibility.ShouldShow(true, false, 26.01, atEdge: true), "Task switch resets edge dwell");
Check(!visibility.ShouldShow(false, false, 27, autoHide: true), "Auto-hide stays hidden without edge input");
Check(AppClick.Decide(false, false, false) == AppClickAction.Launch, "Unopened application launches");
Check(AppClick.Decide(false, false, false, allowLaunch: false) == AppClickAction.Ignore, "Single click never launches an unopened app");
Check(AppClick.Decide(true, true, false) == AppClickAction.Minimize, "Active app minimizes on repeat click");
Check(AppClick.Decide(true, false, true) == AppClickAction.Restore, "Minimized app restores");
Check(AppClick.Decide(true, false, false) == AppClickAction.Activate, "Background app activates");
Check(AppClick.Decide(true, true, false, false) == AppClickAction.Activate, "Open menu does not minimize");
Check(AppClick.Decide(true, true, false, allowLaunch: false) == AppClickAction.Minimize, "Running apps minimize on a single click");
Check(AppClick.Decide(true, false, true, allowLaunch: false) == AppClickAction.Restore, "Running apps restore on a single click");
Check(AppClick.Decide(true, false, false, allowLaunch: false) == AppClickAction.Activate, "Running apps activate on a single click");
var clicks = new DockClickSequence();
Check(!clicks.Complete("app", 0, 10, 10, .5, 4, 4), "First click does not authorize a launch");
Check(clicks.Complete("app", .2, 11, 12, .5, 4, 4), "Same icon within system timing and distance double-clicks");
Check(!clicks.Complete("app", .3, 10, 10, .5, 4, 4), "Third click starts a new pair");
Check(!clicks.Complete("app", 1, 10, 10, .5, 4, 4), "Expired click does not complete a double-click");
Check(!clicks.Complete("other", 1.1, 10, 10, .5, 4, 4), "Different icons never form a double-click");
Check(!clicks.Complete("other", 1.2, 13, 10, .5, 4, 4), "Clicks outside the system distance start a new pair");
clicks.Reset();
Check(!clicks.Complete("other", 1.3, 13, 10, .5, 4, 4), "Drag or cancellation clears the earlier click");
Check(!clicks.Complete("other", 1.2, 13, 10, .5, 4, 4), "Clock reversal cannot authorize a launch");
clicks.Reset();
Check(!clicks.Complete("app", 3, 10, 10, .5, 4, 4) && clicks.Complete("app", 3.5, 12, 12, .5, 4, 4), "System double-click boundary is inclusive");
var taskbar = new TaskbarVisibility();
Check(!LaunchArguments.RequiresWindowBinding("chrome", ""), "Ordinary browser launch can reuse existing windows");
foreach (string value in new[] { "desktop", "start-menu", "taskbar", "9", "\"desktop\"" })
    Check(!LaunchArguments.RequiresWindowBinding("chrome", "--source-shortcut-location=" + value), "Shortcut launch metrics do not select a separate browser window");
Check(!LaunchArguments.RequiresWindowBinding("CHROME", " --source-shortcut-location=desktop \t--source-shortcut-location=taskbar "), "Metadata-only arguments can be repeated and padded");
Check(!LaunchArguments.RequiresWindowBinding("msedge", "--source-shortcut-location=desktop"), "Chromium browser launch metadata is non-specific");
foreach (string arguments in new[] { "--source-shortcut-location=desktop https://example.invalid/", "--profile-directory=\"Example Profile\" --source-shortcut-location=desktop",
    "--app-id=example", "--app=https://example.invalid/", "--incognito", "--new-window", "--user-data-dir=example", "example.html",
    "--source-shortcut-location=", "--source-shortcut-location=\"desktop", "--source-shortcut-location=desktop --unknown" })
    Check(LaunchArguments.RequiresWindowBinding("chrome", arguments), "Document, profile, app and unknown flags keep their original target semantics");
Check(LaunchArguments.RequiresWindowBinding("example", "--source-shortcut-location=desktop"), "Browser-only metadata never changes another application's arguments");
Check(LaunchArguments.RequiresWindowBinding(null, "--source-shortcut-location=desktop"), "Unresolved launch targets stay conservative");
var taskbarMonitor = new WindowBounds(100, 200, 2020, 1280);
var taskbarFrame = new WindowBounds(100, 200, 2020, 272);
Check(TaskbarVisibility.AtTopEdge(taskbarMonitor, 100, 200), "Topmost physical pixel can reveal the taskbar");
Check(TaskbarVisibility.AtTopEdge(taskbarMonitor, 2019, 201), "Second edge pixel belongs to the monitor");
Check(!TaskbarVisibility.AtTopEdge(taskbarMonitor, 100, 202), "Clicking below the two-pixel top edge does not count as edge dwell");
Check(!TaskbarVisibility.AtTopEdge(taskbarMonitor, 99, 200) && !TaskbarVisibility.AtTopEdge(taskbarMonitor, 2020, 200), "Other monitors do not trigger the primary edge");
Check(!TaskbarVisibility.AtTopEdge(taskbarMonitor, 100, 199), "Area above the monitor is not the edge");
foreach (int y in new[] { 200, 201, 202, 236, 271, 272 })
    Check(!TaskbarVisibility.OverVisibleBar(taskbarFrame, 0, 500, y), "A hidden taskbar has no hover or click retention area");
Check(TaskbarVisibility.OverVisibleBar(taskbarFrame, .25, 500, 217), "Visible part of the sliding taskbar retains interaction");
Check(!TaskbarVisibility.OverVisibleBar(taskbarFrame, .25, 500, 218), "Area below the sliding picture cannot summon it");
Check(TaskbarVisibility.OverVisibleBar(taskbarFrame, .5, 500, 235) && !TaskbarVisibility.OverVisibleBar(taskbarFrame, .5, 500, 236), "Partial-height boundary matches the slide geometry");
Check(TaskbarVisibility.OverVisibleBar(taskbarFrame, 1, 500, 271) && !TaskbarVisibility.OverVisibleBar(taskbarFrame, 1, 500, 272), "Fully shown taskbar uses its physical rectangle");
Check(!TaskbarVisibility.OverVisibleBar(taskbarFrame, 1, 99, 210) && !TaskbarVisibility.OverVisibleBar(taskbarFrame, 1, 2020, 210), "Side boundaries do not retain the bar");
Check(!TaskbarVisibility.OverVisibleBar(taskbarFrame, double.NaN, 500, 210), "Invalid animation progress never creates an input region");
Check(!taskbar.Update(0, false, false, false, false), "Taskbar hides without an edge input surface");
Check(!taskbar.Update(1, true, false, false, false), "Accidental edge touch does not reveal immediately");
Check(taskbar.Update(1.2, true, false, false, false), "Sustained top-edge dwell reveals");
Check(taskbar.Update(2, false, true, false, false), "Hovering taskbar preserves access");
Check(taskbar.Update(2.7, false, false, false, false), "Leaving taskbar has a grace interval");
Check(!taskbar.Update(2.76, false, false, false, false), "Taskbar hides after leave delay");
Check(taskbar.Update(3, false, false, true, false), "Start or tray interaction reveals taskbar");
Check(taskbar.Update(30, false, false, true, false), "Open shell UI keeps taskbar available");
Check(!taskbar.Update(31, false, false, false, true), "Task switching suppresses taskbar recall");
Check(!taskbar.Update(31.1, false, false, false, false), "Taskbar does not flash after task switching");
taskbar.Reveal(32);
Check(taskbar.Update(34.99, false, false, false, false), "Manual taskbar recall lasts three seconds");
Check(!taskbar.Update(35, false, false, false, false), "Manual recall expires");
Check(!new DockSettings().TaskbarEnhancement && new DockSettings().TaskbarGlass, "Taskbar enhancement is opt-in");
var oldSettings = System.Text.Json.JsonSerializer.Deserialize<DockSettings>("{\"IconSize\":48}")!;
Check(!oldSettings.TaskbarEnhancement, "Existing configuration never silently takes control of native taskbar");
foreach (int count in new[] { 1, 8, 20, 40 })
foreach (int from in Enumerable.Range(0, count))
foreach (int to in Enumerable.Range(0, count))
{
    var order = DockReorder.Order(count + 1, from, to, count);
    Check(order.Length == count + 1 && order.Distinct().Count() == count + 1, "Reordering preserves every item");
    Check(order[^1] == count && order[to] == from, "Reorder destination and fixed settings slot");
    Check(order.Where(x => x != from).SequenceEqual(Enumerable.Range(0, count + 1).Where(x => x != from)), "Other entries keep their relative order");
}
Check(DockReorder.Order(5, 4, 0, 4).SequenceEqual(Enumerable.Range(0, 5)), "Settings cannot be reordered");
Check(!DockReorder.Moved(4, 4) && DockReorder.Moved(5, 4), "A small movement remains a click");
var dropGeometry = DockLayout.Calculate(9, 48, 1.65, null, 1920);
Check(DockReorder.Insertion(dropGeometry, 8, 0) == 0 && DockReorder.Insertion(dropGeometry, 8, 1920) == 8, "Drop slots stay before settings");
Check(DockReorder.Target(dropGeometry, 8, 0, 1920) == 7 && DockReorder.Target(dropGeometry, 8, 7, 0) == 0, "Live reorder follows stable icon centers");
Check(!DockReorder.RemoveOnRelease(dropGeometry, 1920, 250, 960, dropGeometry.BodyTop(250) - 40), "A nearby outside release does not remove");
Check(DockReorder.RemoveOnRelease(dropGeometry, 1920, 250, 960, dropGeometry.BodyTop(250) - 90), "A far upward release removes an entry");
Check(DockReorder.RemoveOnRelease(dropGeometry, 1920, 250, dropGeometry.BodyLeft(1920) - 90, dropGeometry.Baseline(250)), "A far sideways release removes an entry");
var transition = new TimedTransition();
transition.Begin(1, 0, .22);
double halfway = transition.Sample(.1);
Check(halfway > 0 && halfway < 1, "Transition has intermediate visual frames");
transition.Begin(0, .1, .18);
Check(Math.Abs(transition.Value - halfway) < .00001, "Reversing keeps the current position");
Check(transition.Sample(.5) == 0 && !transition.Active, "Reversed transition settles and stops");
transition.Begin(1, 1, .22); transition.Sample(1.1); transition.Begin(1, 1.1, .22);
Check(transition.Sample(1.23) == 1 && !transition.Active, "Repeated targets do not restart an animation");
var shellDrag = new ShellDragIntent();
Check(!shellDrag.Update(true, true, true, false, false, 10, 10), "Explorer mouse capture on press is not a file drag");
Check(!shellDrag.Update(true, true, true, false, false, 11, 11), "Small Explorer click jitter cannot summon Dock");
shellDrag.Update(false, false, false, false, false, 11, 11);
Check(!shellDrag.Update(true, true, false, false, false, 10, 10), "Desktop press alone is not a drag");
Check(shellDrag.Update(true, true, false, false, false, 20, 20), "Desktop movement detects drag without WinEvent");
Check(shellDrag.Update(true, false, false, false, false, 900, 900), "Drag stays active after leaving its source");
Check(!shellDrag.Update(false, false, false, false, false, 900, 900), "Release clears the drag hold");
Check(!shellDrag.Update(true, true, true, false, false, 500, 500), "Late Shell capture waits for actual pointer movement");
Check(shellDrag.Update(true, false, true, false, false, 520, 520), "Movement after late capture recognizes the drag");
Check(!shellDrag.Update(true, true, true, false, true, 500, 500), "Task switch or Escape clears drag hold");
Check(!shellDrag.Update(true, false, false, false, false, 1, 1), "Other app press is not a Shell drag");
Check(!shellDrag.Update(true, false, false, false, false, 100, 100), "Moving an app window does not summon Dock");
shellDrag.Update(false, false, false, false, false, 0, 0);
Check(shellDrag.Update(true, false, false, true, false, 0, 0), "Explicit drag event is still supported");
Check(!shellDrag.Update(false, false, false, true, false, 0, 0), "Stale event cannot keep Dock shown after release");
Check(!shellDrag.Update(true, true, true, false, false, 100, 100, nearDock: false)
    && !shellDrag.Update(true, true, true, false, false, 140, 140, nearDock: false), "Moving or selecting inside Explorer away from Dock does not reveal");
Check(shellDrag.Update(true, false, true, false, false, 200, 800, nearDock: true), "Shell drag reveals after approaching Dock");
Check(shellDrag.Update(true, false, false, false, false, 200, 500, nearDock: false), "Revealed Dock stays available until drag ends");
Check(!shellDrag.Update(false, false, false, false, false, 200, 500), "Release clears near-Dock drag recall");
byte[] reflectedSource = new byte[16 * 16 * 4];
for (int i = 0; i < reflectedSource.Length; i += 4)
{
    reflectedSource[i] = (byte)(i / 64 + 1); reflectedSource[i + 1] = 40;
    reflectedSource[i + 2] = 80; reflectedSource[i + 3] = 160;
}
byte[] reflection = IconReflection.Create(reflectedSource, 16);
Check(reflection[0] == Math.Round(reflectedSource[15 * 64] * IconReflection.Opacity), "Reflection mirrors the bottom source row");
Check(reflection[3] > reflection[64 + 3] && reflection[64 + 3] > reflection[128 + 3], "Reflection fades away from the baseline");
Check(reflection[10 * 64 + 3] > 0, "The full mirrored image is retained before vertical compression");
Check(reflection.Skip(15 * 64).All(x => x == 0), "Reflection ends in full transparency");
for (int i = 0; i < reflection.Length; i += 4)
    Check(reflection[i] <= reflection[i + 3] && reflection[i + 1] <= reflection[i + 3] && reflection[i + 2] <= reflection[i + 3], "Reflection keeps premultiplied alpha");
foreach (double iconSize in new[] { 32d, 48, 80 })
{
    var reflectedGeometry = DockLayout.Calculate(8, iconSize, 1.8, 960, 1920);
    Check(reflectedGeometry.Slots.All(x => reflectedGeometry.Baseline(250) - x.Size >= 0), "Magnified icons fit the host above the reflection plane");
    Check(reflectedGeometry.Baseline(250) + 2 + reflectedGeometry.Slots.Max(x => x.Size) * IconReflection.Depth <= 250 - DockGeometry.Foot + .01, "Reflections fit below the shared baseline");
}
Check(System.Text.Json.JsonSerializer.Deserialize<DockSettings>("{}")!.Reflections, "Existing settings default to reflections");
var featureSettings = System.Text.Json.JsonSerializer.Deserialize<DockSettings>("{}")!;
Check(featureSettings.Widgets.Clock && featureSettings.Widgets.Weather && featureSettings.Widgets.Light,
    "Existing settings gain transparent desktop widgets");
var firstGroup = new DesktopGroup { Name = "Work", Targets = ["Example.lnk", "example.lnk"] };
var secondGroup = new DesktopGroup { Name = "Play" };
var groups = DesktopLibrary.NormalizeGroups([firstGroup, secondGroup, new() { Id = "invalid" }]);
Check(groups.Count == 2 && groups[0].Targets.Count == 1, "Groups validate identifiers and deduplicate targets");
DockItem[] library = [new("Example", "Example.lnk"), new("Other", "Other.lnk"), new("Notes", "Notes.txt")];
Check(DesktopLibrary.Filter(library, groups, firstGroup.Id, "EXAM").Length == 1, "Group search is case independent");
Check(DesktopLibrary.Filter(library, groups, "ungrouped", "").Length == 2, "Ungrouped excludes grouped entries");
DesktopLibrary.Assign(groups, "Example.lnk", secondGroup.Id);
Check(firstGroup.Targets.Count == 0 && secondGroup.Targets.SequenceEqual(["Example.lnk"]), "Assign moves virtual membership without duplicate groups");
DesktopLibrary.Assign(groups, "Example.lnk", null);
Check(DesktopLibrary.Filter(library, groups, "ungrouped", "").Length == 3, "Ungroup preserves the desktop entry");
var positions = new WidgetOptions { City = "   ", Positions = new() { ["clock"] = new(-5, 90000), ["calendar"] = new(double.NaN, 2), ["unknown"] = new(1, 2) } };
positions.Normalize();
Check(positions.City.Length > 0 && positions.Positions.Count == 1 && positions.Positions["clock"] == new WidgetPosition(0, 20000), "Widget positions and city normalize safely");
var migratedWidgets = System.Text.Json.JsonSerializer.Deserialize<WidgetOptions>("""{"Calendar":true,"Positions":{"calendar":{"X":20,"Y":30},"clock":{"X":10,"Y":20}}}""")!;
migratedWidgets.Normalize();
Check(migratedWidgets.Positions.Count == 1 && !System.Text.Json.JsonSerializer.Serialize(migratedWidgets).Contains("Calendar"), "Removed calendar settings migrate without reappearing");
foreach (var (target, directory, category) in new[] { ("Example.LNK", false, "apps"), ("shell:MyComputerFolder", false, "apps"), ("ms-settings:", false, "apps"), ("Example.pdf", false, "documents"), ("Example.JPG", false, "media"), ("Example.mp3", false, "media"), ("Archive.zip", false, "other"), ("Folder.jpg", true, "folders") })
    Check(DesktopLibrary.Category(new("Example", target), directory) == category, "Automatic category handles extensions, system targets and folders");
var pageWheel = new PageWheel();
Check(pageWheel.Update(-40, 0) == 0 && pageWheel.Update(-40, .04) == 0 && pageWheel.Update(-40, .08) == 1, "Fine wheel deltas accumulate into one page");
Check(pageWheel.Update(-120, .1) == 0 && pageWheel.Update(-240, .12) == 0, "Trackpad burst does not skip multiple pages");
Check(pageWheel.Update(120, .3) == -1, "Reverse wheel navigates back");
Check(pageWheel.Update(-60, 1) == 0 && pageWheel.Update(-60, 2) == 0, "Separated partial wheel gestures do not accumulate");
const string weatherFixture = """
{"current":{"temperature_2m":18.4,"apparent_temperature":17.1,"relative_humidity_2m":54,"wind_speed_10m":8.2,"weather_code":2,"is_day":1},"daily":{"temperature_2m_max":[23.1],"temperature_2m_min":[12.5]}}
""";
var weather = WeatherSnapshot.Parse("Example City", weatherFixture, DateTimeOffset.UnixEpoch);
Check(weather.Temperature == 18.4 && weather.Humidity == 54 && weather.Low == 12.5 && weather.Description == "晴间多云", "Weather parses real API field names and units");
Check((weather with { Code = 0, IsDay = false }).Description == "晴朗夜空", "Weather distinguishes night from day");
Check((weather with { Code = 95 }).Description == "雷雨" && (weather with { Code = 73 }).Description == "雪", "Weather codes map to conditions");
try { WeatherSnapshot.Parse("Example City", weatherFixture.Replace(":54", ":154"), DateTimeOffset.UnixEpoch); Check(false, "Invalid weather must be rejected"); }
catch (System.Text.Json.JsonException) { Check(true, "Invalid weather is rejected without a fake value"); }
string featureData = Path.Combine(Path.GetTempPath(), "DesktopDock-features-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(featureData); string desktopData = Path.Combine(featureData, "desktop"); Directory.CreateDirectory(desktopData);
    File.WriteAllText(Path.Combine(desktopData, "Example.lnk"), "fixture"); File.WriteAllText(Path.Combine(desktopData, "Notes.txt"), "fixture");
    File.WriteAllText(Path.Combine(desktopData, "desktop.ini"), "fixture"); File.SetAttributes(Path.Combine(desktopData, "desktop.ini"), FileAttributes.Hidden);
    Directory.CreateDirectory(Path.Combine(desktopData, "Folder"));
    var entries = DesktopLibrary.Read([desktopData, desktopData, Path.Combine(featureData, "missing")]);
    Check(entries.Length == 3 && entries.Any(x => x.Name == "Example") && entries.Any(x => x.Name == "Notes.txt"), "Desktop catalog preserves files, folders and shortcut labels");
    Check(!entries.Any(x => x.Name == "desktop.ini"), "Desktop catalog excludes hidden system metadata");
    DesktopLibrary.Assign(groups, entries[0].Target, firstGroup.Id);
    string persisted = Path.Combine(featureData, "settings.json");
    SettingsStore.Save(persisted, new DockSettings { DesktopGroups = groups, Widgets = positions });
    var restoredFeatures = SettingsStore.Load(persisted);
    Check(restoredFeatures.DesktopGroups[0].Targets.Contains(entries[0].Target) && restoredFeatures.Widgets.Positions["clock"].X == 0,
        "Virtual groups and widget positions persist across restarts");
    Check(File.Exists(Path.Combine(desktopData, "Example.lnk")) && Directory.Exists(Path.Combine(desktopData, "Folder")), "Grouping leaves original desktop files untouched");
}
finally { if (Directory.Exists(featureData)) Directory.Delete(featureData, true); }
var prefixed = DockLayout.Calculate(7, 48, 1.5, null, 1200);
Check(DockReorder.Order(7, 1, 4, 5, start: 1).SequenceEqual([0, 2, 3, 4, 1, 5, 6]), "Reorder keeps launcher and settings fixed at either end");
Check(DockReorder.Order(7, 0, 4, 5, start: 1).SequenceEqual(Enumerable.Range(0, 7)), "Launcher is excluded from movable range");
Check(DockReorder.Target(prefixed, 5, 1, -100, start: 1) == 1 && DockReorder.Target(prefixed, 5, 1, 5000, start: 1) == 5, "Drag targets clamp to pinned entries");
Check(DockReorder.Insertion(prefixed, 5, -100, start: 1) == 0 && DockReorder.Insertion(prefixed, 5, 5000, start: 1) == 5, "Drops return pinned indexes after the launcher");
var previewHover = new PreviewHover();
Check(previewHover.Update(2, false, true, 0) == -1 && previewHover.Update(2, false, true, .39) == -1, "Preview requires an icon dwell");
Check(previewHover.Update(2, false, true, .41) == 2, "Stable hover opens the correct preview");
Check(previewHover.Update(-1, false, true, .5) == 2 && previewHover.Update(-1, true, true, .7) == 2, "Preview survives the gap and pointer entry");
Check(previewHover.Update(3, false, true, 1) == 2 && previewHover.Update(3, false, true, 1.41) == 3, "Crossing icons waits before changing preview");
Check(previewHover.Update(-1, false, true, 2) == 3 && previewHover.Update(-1, false, true, 2.36) == -1, "Leaving preview closes after grace");
previewHover.Update(1, false, true, 3); previewHover.Update(1, false, true, 3.5);
Check(previewHover.Update(1, true, false, 3.6) == -1, "Dragging, switching or disabled previews dismiss immediately");
Check(previewHover.Update(1, false, true, 3.7) == -1, "Interaction restarts dwell instead of reopening immediately");
foreach (double availableWidth in new[] { 320d, 600, 1280, 3840 })
foreach (double availableHeight in new[] { 160d, 700 })
{
    var previewLayout = WindowPreviewLayout.Calculate(availableWidth, availableHeight);
    Check(previewLayout.Capacity is >= 1 and <= 4 && previewLayout.Width(previewLayout.Capacity) <= availableWidth && previewLayout.Height <= availableHeight,
        "Preview pages fit available space at different DPI scales");
    foreach (var size in new[] { (1920d, 1080d), (700d, 1200d), (0d, 0d) })
    {
        var fit = WindowPreviewLayout.Fit(size.Item1, size.Item2, previewLayout.CardWidth, previewLayout.ImageHeight);
        Check(fit.X >= -.001 && fit.Y >= -.001 && fit.X + fit.Width <= previewLayout.CardWidth + .001 && fit.Y + fit.Height <= previewLayout.ImageHeight + .001,
            "Portrait and landscape thumbnail content stays inside its card");
        if (size.Item1 > 0) Check(Math.Abs(fit.Width / fit.Height - size.Item1 / size.Item2) < .001, "Thumbnails retain their aspect ratio");
    }
}
Check(System.Text.Json.JsonSerializer.Deserialize<DockSettings>("{}")!.WindowPreviews, "Old settings enable preview without migration");
Check(!System.Text.Json.JsonSerializer.Deserialize<DockSettings>("{\"WindowPreviews\":false}")!.WindowPreviews, "Disabled previews persist");
foreach (var invalid in new[] { "", "1.0", "01.0.0", "1.0.0-", "1.0.0-preview.01", "1.0.0-预览", "-1.0.0", "2147483648.0.0", "1.0.0+", "1.0.0-a..b" })
    Check(!ReleaseVersion.TryParse(invalid, out _), "Malformed update version is rejected");
var versionOrder = new[] { "0.1.0-alpha", "0.1.0-preview.2", "0.1.0-preview.9", "0.1.0-preview.10", "0.1.0-preview.23", "0.1.0", "0.1.1", "0.2.0", "1.0.0" };
for (int i = 0; i < versionOrder.Length; i++)
for (int j = 0; j < versionOrder.Length; j++)
{
    Check(ReleaseVersion.TryParse(versionOrder[i], out var first) && ReleaseVersion.TryParse("v" + versionOrder[j], out var second)
        && Math.Sign(first.CompareTo(second)) == Math.Sign(i.CompareTo(j)), "Stable and numeric prerelease versions sort correctly");
}
string UpdateFixture(string version, bool draft = false, bool pre = true, string host = "github.com", string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", object? bytes = null) =>
    System.Text.Json.JsonSerializer.Serialize(new[] { new { tag_name = "v" + version, draft, prerelease = pre, body = "Example notes", assets = new[] { new {
        name = $"DesktopDock-{version}-Setup-x64.exe", state = "uploaded", size = bytes ?? 123L, digest,
        browser_download_url = $"https://{host}/lpd2005/desktop-dock/releases/download/v{version}/DesktopDock-{version}-Setup-x64.exe" } } } });
ReleaseVersion.TryParse("0.1.0-preview.22", out var previewVersion);
ReleaseVersion.TryParse("0.1.0", out var stableVersion);
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.23"), previewVersion)?.Version.ToString() == "0.1.0-preview.23", "Preview channel receives next preview");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.1", pre: false), stableVersion)?.Version.ToString() == "0.1.1", "Stable channel receives stable updates");
Check(ReleaseFeed.Latest(UpdateFixture("0.2.0-preview.1"), stableVersion) is null, "Stable users do not receive previews");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.21"), previewVersion) is null, "Updater never downgrades");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.23", draft: true), previewVersion) is null, "Draft release is never installable");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.23", host: "example.invalid"), previewVersion) is null, "Installer must come from the fixed repository URL");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.23", digest: ""), previewVersion) is null, "Installer without SHA-256 is rejected");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.23", bytes: "123"), previewVersion) is null, "Wrong asset size type is safely ignored");
Check(ReleaseFeed.Latest(UpdateFixture("0.1.0-preview.23", bytes: ReleaseFeed.MaximumBytes + 1), previewVersion) is null, "Oversized installer is rejected");
Check(System.Text.Json.JsonSerializer.Deserialize<DockSettings>("{}")!.AutoCheckUpdates, "Existing settings enable low frequency update checks");
Console.WriteLine($"PASS: {checks} geometry, persistence and interaction checks.");
