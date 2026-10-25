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
visibility.Summon(1);
Check(visibility.ShouldShow(true, false, 1), "Hotkey temporarily overrides coverage");
Check(visibility.ShouldShow(true, false, 3.99), "Recall remains available before timeout");
Check(!visibility.ShouldShow(true, false, 4), "Recall expires after exactly three idle seconds");
visibility.Summon(5); visibility.Interact(7);
Check(visibility.ShouldShow(true, false, 9.99), "Dock interaction resets recall timeout");
Check(!visibility.ShouldShow(true, false, 10), "Reset timeout also expires");
Check(!visibility.ShouldShow(false, false, 11), "Uncovered reveal waits for stability");
Check(visibility.ShouldShow(false, false, 11.3), "Stable uncovered Dock reappears");
Check(!visibility.ShouldShow(false, true, 12), "Task switching suppresses reveal");
Check(!visibility.ShouldShow(false, false, 12.1), "No immediate flash after task switch");
Check(visibility.ShouldShow(false, false, 12.4), "Reveal resumes only after stable transition");
Check(!visibility.ShouldShow(true, false, 12.5), "Covering again hides immediately");
visibility.Summon(13);
Check(visibility.ShouldShow(false, false, 14), "Clear state stabilizes while recalled");
Check(visibility.ShouldShow(false, false, 16.1), "No hide-reveal blink when recall expires after coverage clears");
Check(AppClick.Decide(false, false, false) == AppClickAction.Launch, "Unopened application launches");
Check(AppClick.Decide(true, true, false) == AppClickAction.Minimize, "Active app minimizes on repeat click");
Check(AppClick.Decide(true, false, true) == AppClickAction.Restore, "Minimized app restores");
Check(AppClick.Decide(true, false, false) == AppClickAction.Activate, "Background app activates");
Check(AppClick.Decide(true, true, false, false) == AppClickAction.Activate, "Open menu does not minimize");
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
Console.WriteLine($"PASS: {checks} geometry, persistence and interaction checks.");
