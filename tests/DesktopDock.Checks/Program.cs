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
var monitor = new WindowBounds(0, 0, 1920, 1080);
Check(WindowAvoidance.ShouldHide(monitor, monitor, false, false), "Borderless full screen always hides Dock");
Check(WindowAvoidance.ShouldHide(new(0, 0, 1920, 1032), monitor, true, true), "Maximized app hides Dock");
Check(!WindowAvoidance.ShouldHide(new(0, 0, 1920, 1032), monitor, true, false), "Maximized avoidance can be disabled");
Check(!WindowAvoidance.ShouldHide(new(1920, 0, 3840, 1080), monitor, true, true), "Other monitor does not hide Dock");
Check(!WindowAvoidance.ShouldHide(new(100, 100, 1200, 900), monitor, false, true), "Normal window keeps Dock visible");
var safe = new WindowBounds(0, 0, 1920, 920);
Check(WindowFitting.Fit(new(100, 400, 1100, 1080), safe) == new WindowBounds(100, 240, 1100, 920), "Move overlapping window above Dock without resizing");
Check(WindowFitting.Fit(new(0, 0, 2000, 1200), safe) == safe, "Shrink oversize startup window to available area");
Check(WindowFitting.Fit(new(200, 100, 800, 700), safe) == new WindowBounds(200, 100, 800, 700), "Leave already fitting window alone");
Check(WindowFitting.Fit(new(-200, -100, 800, 700), safe) == new WindowBounds(0, 0, 1000, 800), "Keep fitted window within monitor");
Check(LaunchFeedback.Lift(0) == 0 && LaunchFeedback.Lift(LaunchFeedback.Duration) == 0, "Launch feedback starts and ends at rest");
for (int i = 0; i < 100; i++) Check(LaunchFeedback.Lift(i * LaunchFeedback.Duration / 100) is >= 0 and < 12, "Bounded launch feedback");
var lifted = DockLayout.Calculate(5, 48, 1.65, null, 1000);
lifted.Slots[2] = lifted.Slots[2] with { Lift = 8 };
Check(lifted.HitIcon(lifted.Slots[2].X + 24, lifted.Baseline(190) - 8 - 24, 190) == 2, "Bouncing visual and hit target share geometry");
Console.WriteLine($"PASS: {checks} geometry and persistence checks.");
