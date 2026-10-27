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
Check(!shellDrag.Update(true, true, false, false, false, 10, 10), "Desktop press alone is not a drag");
Check(shellDrag.Update(true, true, false, false, false, 20, 20), "Desktop movement detects drag without WinEvent");
Check(shellDrag.Update(true, false, false, false, false, 900, 900), "Drag stays active after leaving its source");
Check(!shellDrag.Update(false, false, false, false, false, 900, 900), "Release clears the drag hold");
Check(shellDrag.Update(true, true, true, false, false, 500, 500), "Capture recognizes a drag missed at its start");
Check(!shellDrag.Update(true, true, true, false, true, 500, 500), "Task switch or Escape clears drag hold");
Check(!shellDrag.Update(true, false, false, false, false, 1, 1), "Other app press is not a Shell drag");
Check(!shellDrag.Update(true, false, false, false, false, 100, 100), "Moving an app window does not summon Dock");
shellDrag.Update(false, false, false, false, false, 0, 0);
Check(shellDrag.Update(true, false, false, true, false, 0, 0), "Explicit drag event is still supported");
Check(!shellDrag.Update(false, false, false, true, false, 0, 0), "Stale event cannot keep Dock shown after release");
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
Console.WriteLine($"PASS: {checks} geometry, persistence and interaction checks.");
