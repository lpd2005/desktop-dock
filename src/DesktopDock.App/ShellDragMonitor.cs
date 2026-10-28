using System.Runtime.InteropServices;
using System.Text;
using DesktopDock.Core;

namespace DesktopDock.App;

internal readonly record struct ShellDragInput(bool Down, bool ShellSource, bool Captured, Native.Point Point, bool Cancelled = false);

internal sealed class ShellDragMonitor
{
    private readonly ShellDragIntent intent = new();
    private bool shellCaptureStarted;
    internal Func<ShellDragInput>? InputForChecks { get; set; }
    public bool Active => intent.Active;
    public void CaptureStarted(nint window) { if (IsShellSource(window)) shellCaptureStarted = true; }

    public bool Update(bool notified, bool switching, float scale, WindowBounds dockArea)
    {
        var input = InputForChecks?.Invoke() ?? Read();
        // Selection, scrolling and navigation in an Explorer window stay local
        // to that window. Reveal only when a real gesture approaches the Dock.
        bool nearDock = input.Point.X >= dockArea.Left - 32 * scale && input.Point.X <= dockArea.Right + 32 * scale
            && input.Point.Y >= dockArea.Top - 40 * scale && input.Point.Y <= dockArea.Bottom + 24 * scale;
        return intent.Update(input.Down, input.ShellSource, input.Captured, notified,
            switching || input.Cancelled, input.Point.X / scale, input.Point.Y / scale, nearDock);
    }

    private ShellDragInput Read()
    {
        Native.GetCursorPos(out var point);
        int button = Native.GetSystemMetrics(23) != 0 ? 0x02 : 0x01;
        if (Native.GetAsyncKeyState(button) >= 0)
        {
            shellCaptureStarted = false;
            return new(false, false, false, point);
        }
        bool cancelled = Native.GetAsyncKeyState(0x1B) < 0;
        var info = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        bool haveInfo = Native.GetGUIThreadInfo(0, ref info);
        // Resizing/moving an Explorer window is not a file drag.
        if (haveInfo && (info.Flags & 2) != 0) return new(true, false, false, point, true);
        bool captured = shellCaptureStarted || haveInfo && info.Capture != 0 && IsShellSource(info.Capture);
        bool source = captured || IsShellSource(Native.WindowFromPoint(point));
        return new(true, source, captured, point, cancelled);
    }

    private static bool IsShellSource(nint window)
    {
        if (window == 0) return false;
        nint root = Native.GetAncestor(window, 2);
        var name = new StringBuilder(128);
        Native.GetClassName(root, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "CabinetWClass" or "ExploreWClass";
    }
}
