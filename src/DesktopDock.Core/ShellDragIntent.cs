namespace DesktopDock.Core;

// A read-only fallback for Shell sources that do not emit WinEvents. No mouse
// capture or input surface is needed before OLE calls our actual drop target.
public sealed class ShellDragIntent
{
    private bool held, candidate, confirmed;
    private double originX, originY;
    public bool Active { get; private set; }

    public bool Update(bool down, bool shellSource, bool captured, bool notified,
        bool cancelled, double x, double y, bool nearDock = true)
    {
        if (!down || cancelled)
        {
            held = candidate = confirmed = Active = false;
            return false;
        }
        if (!held)
        {
            held = true; candidate = shellSource; originX = x; originY = y;
        }
        // Capture can identify a Shell source even if the first poll occurred
        // after the pointer had already left the desktop or Explorer window.
        candidate |= shellSource && captured;
        // Explorer captures the pointer during ordinary clicks too. Capture
        // identifies a possible source, never sufficient evidence of a drag.
        confirmed |= notified || candidate && DockReorder.Moved(x - originX, y - originY);
        Active |= confirmed && nearDock;
        return Active;
    }
}
