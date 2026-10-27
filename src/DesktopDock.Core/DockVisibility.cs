namespace DesktopDock.Core;

public sealed class DockVisibility
{
    public const double EdgeDwellSeconds = .18;
    public const double LeaveSeconds = .75;
    public const double StableRevealSeconds = .25;
    private double lastInteraction = double.NegativeInfinity;
    private double? edgeSince;
    private double? clearSince;
    private bool summoned;
    public bool IsTemporarilyShown { get; private set; }

    public void Summon(double now) { summoned = true; lastInteraction = now; }
    public void Interact(double now) { if (summoned) lastInteraction = now; }
    public bool ShouldShow(bool covered, bool switching, double now, bool autoHide = false,
        bool atEdge = false, bool overDock = false, bool interacting = false)
    {
        if (switching)
        { clearSince = edgeSince = null; summoned = IsTemporarilyShown = false; return false; }
        if (atEdge)
        {
            edgeSince ??= now;
            if (now - edgeSince >= EdgeDwellSeconds) { summoned = true; lastInteraction = now; }
        }
        else edgeSince = null;
        if (interacting) { summoned = true; lastInteraction = now; }
        if (summoned && overDock) lastInteraction = now;
        if (summoned && now - lastInteraction >= LeaveSeconds) summoned = false;
        IsTemporarilyShown = summoned;
        if (covered) clearSince = null;
        else clearSince ??= now;
        return summoned || !autoHide && !covered && now - clearSince >= StableRevealSeconds;
    }
}

public enum AppClickAction { Ignore, Launch, Minimize, Restore, Activate }
public static class AppClick
{
    public static AppClickAction Decide(bool exists, bool active, bool minimized, bool toggle = true, bool allowLaunch = true)
        => !exists ? allowLaunch ? AppClickAction.Launch : AppClickAction.Ignore : minimized ? AppClickAction.Restore
        : active && toggle ? AppClickAction.Minimize : AppClickAction.Activate;
}
