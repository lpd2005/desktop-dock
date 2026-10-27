namespace DesktopDock.Core;

/// <summary>Timing and pointer bounds; never owns an input surface along the screen edge.</summary>
public sealed class TaskbarVisibility
{
    public static bool AtTopEdge(WindowBounds monitor, int x, int y)
        => x >= monitor.Left && x < monitor.Right && y >= monitor.Top && y < monitor.Top + 2;

    public static bool OverVisibleBar(WindowBounds bar, double progress, int x, int y)
    {
        if (!double.IsFinite(progress) || x < bar.Left || x >= bar.Right || y < bar.Top) return false;
        int height = bar.Bottom - bar.Top;
        if (height <= 0) return false;
        int visibleHeight = height - (int)Math.Round(height * (1 - Math.Clamp(progress, 0, 1)));
        return y < bar.Top + visibleHeight;
    }

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
