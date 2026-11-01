using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopDock.App;

// Shell requests only: never reads notification or conversation contents.
internal sealed class ShellAttention : IDisposable
{
    private readonly HwndSource source;
    private readonly Action<int, nint> changed;
    private bool disposed;
    internal int MessageId { get; } = RegisterWindowMessage("SHELLHOOK");
    internal bool Registered { get; }
    internal ShellAttention(HwndSource source, Action<int, nint> changed)
    {
        this.source = source; this.changed = changed;
        source.AddHook(Message); Registered = MessageId != 0 && RegisterShellHookWindow(source.Handle);
    }
    private nint Message(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (!disposed && message == MessageId && (int)wparam is 0x8006 or 4 or 0x8004 or 2)
        {
            int kind = (int)wparam;
            source.Dispatcher.BeginInvoke(() => { if (!disposed) changed(kind, lparam); }, DispatcherPriority.Background);
        }
        return 0;
    }
    public void Dispose() { if (disposed) return; disposed = true; source.RemoveHook(Message); if (Registered) DeregisterShellHookWindow(source.Handle); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(nint window);
}
