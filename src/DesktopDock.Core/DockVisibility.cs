namespace DesktopDock.Core;

public sealed class DockVisibility
{
    public const double SummonSeconds = 3;
    public const double StableRevealSeconds = .25;
    private double lastInteraction = double.NegativeInfinity;
    private double? clearSince;
    private bool summoned;
    public bool IsTemporarilyShown { get; private set; }

    public void Summon(double now) { summoned = true; lastInteraction = now; }
    public void Interact(double now) { if (summoned) lastInteraction = now; }
    public bool ShouldShow(bool covered, bool switching, double now)
    {
        if (summoned && now - lastInteraction >= SummonSeconds) summoned = false;
        IsTemporarilyShown = summoned;
        if (switching) { clearSince = null; return false; }
        if (covered) { clearSince = null; return summoned; }
        clearSince ??= now;
        return summoned || now - clearSince >= StableRevealSeconds;
    }
}

public enum AppClickAction { Launch, Minimize, Restore, Activate }
public static class AppClick
{
    public static AppClickAction Decide(bool exists, bool active, bool minimized, bool toggle = true)
        => !exists ? AppClickAction.Launch : minimized ? AppClickAction.Restore
        : active && toggle ? AppClickAction.Minimize : AppClickAction.Activate;
}
