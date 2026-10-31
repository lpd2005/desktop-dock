namespace DesktopDock.Core;

public sealed record DockItem(string Name, string Target, string Arguments = "");

public sealed class DockSettings
{
    public double IconSize { get; set; } = 48;
    public double Magnification { get; set; } = 1.65;
    public double BottomGap { get; set; } = 12;
    public bool Blur { get; set; } = true;
    public bool Reflections { get; set; } = true;
    public bool LightTheme { get; set; } = true;
    public bool HideDesktopIcons { get; set; }
    public bool AutoHide { get; set; }
    public bool ReducedMotion { get; set; }
    public bool WindowPreviews { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool TaskbarEnhancement { get; set; }
    public bool TaskbarAutoHide { get; set; } = true;
    public bool TaskbarGlass { get; set; } = true;
    public List<DockItem> Items { get; set; } = [];
    public List<DesktopGroup> DesktopGroups { get; set; } = [];
    public WidgetOptions Widgets { get; set; } = new();

    public void Normalize()
    {
        IconSize = double.IsFinite(IconSize) ? Math.Clamp(IconSize, 32, 80) : 52;
        Magnification = double.IsFinite(Magnification) ? Math.Clamp(Magnification, 1, 1.8) : 1.5;
        BottomGap = double.IsFinite(BottomGap) ? Math.Clamp(BottomGap, 0, 100) : 12;
        Items = (Items ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Target))
            .DistinctBy(x => (x.Target.ToUpperInvariant(), x.Arguments)).Take(40).ToList();
        DesktopGroups = DesktopLibrary.NormalizeGroups(DesktopGroups);
        Widgets ??= new(); Widgets.Normalize();
    }
}
