namespace DesktopDock.Core;

/// <summary>Timing only; never owns an input surface along the screen edge.</summary>
public sealed class TaskbarVisibility
{
    public const double EdgeDwellSeconds = .18;
    public const double LeaveDelaySeconds = .75;
    private double? edgeSince;
    private double keepUntil;
    public bool Visible { get; private set; } = true;

    public bool Update(double now, bool atEdge, bool overBar, bool shellInteraction, bool switching)
    {
        if (switching && !shellInteraction)
        {
            edgeSince = null;
            keepUntil = now;
            return Visible = false;
        }
        if (atEdge) edgeSince ??= now;
        else edgeSince = null;
        bool summon = shellInteraction || (edgeSince.HasValue && now - edgeSince.Value >= EdgeDwellSeconds);
        if (summon || (Visible && overBar)) keepUntil = now + LeaveDelaySeconds;
        return Visible = summon || now < keepUntil;
    }

    public void Reveal(double now) { Visible = true; keepUntil = now + 3; }
}
