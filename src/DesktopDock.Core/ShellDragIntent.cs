namespace DesktopDock.Core;

// A read-only fallback for Shell sources that do not emit WinEvents. No mouse
// capture or input surface is needed before OLE calls our actual drop target.
public sealed class ShellDragIntent
{
    private bool held, candidate;
    private double originX, originY;
    public bool Active { get; private set; }

    public bool Update(bool down, bool shellSource, bool captured, bool notified,
        bool cancelled, double x, double y)
    {
        if (!down || cancelled)
        {
            held = candidate = Active = false;
            return false;
        }
        if (!held)
        {
            held = true; candidate = shellSource; originX = x; originY = y;
        }
        // Capture can identify a Shell source even if the first poll occurred
        // after the pointer had already left the desktop or Explorer window.
        candidate |= shellSource && captured;
        Active |= notified || candidate && (captured || DockReorder.Moved(x - originX, y - originY));
        return Active;
    }
}
