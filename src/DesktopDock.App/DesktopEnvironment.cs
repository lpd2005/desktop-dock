using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopDock.App;

[Flags]
internal enum DesktopChange { None = 0, Display = 1, WorkArea = 2, Dpi = 4, Shell = 8, Resume = 16, Session = 32 }

// Uses the existing Dock HWND. The timer runs only while a change is pending.
internal sealed class DesktopEnvironment : IDisposable
{
    private readonly HwndSource source;
    private readonly DispatcherTimer timer;
    private readonly Action<bool> suspend;
    private readonly Action<DesktopChange> refresh;
    private readonly int session;
    private DesktopChange pending;
    private int registrationAttempts = 1;
    private bool locked, sleeping, disconnected, changingDisplay, reportedSuspended, disposed;
    internal int ShellMessage { get; } = RegisterWindowMessage("TaskbarCreated");
    internal bool SessionRegistered { get; private set; }
    internal bool Pending => timer.IsEnabled;

    internal DesktopEnvironment(HwndSource source, Action<bool> suspend, Action<DesktopChange> refresh)
    {
        this.source = source; this.suspend = suspend; this.refresh = refresh;
        using var process = Process.GetCurrentProcess(); session = process.SessionId;
        SessionRegistered = WTSRegisterSessionNotification(source.Handle, 0);
        timer = new(DispatcherPriority.Background, source.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Flush();
        source.AddHook(Message);
        RetryRegistration();
    }

    private nint Message(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (disposed) return 0;
        if (ShellMessage != 0 && message == ShellMessage) Queue(DesktopChange.Shell);
        else switch (message)
        {
            case 0x007E: Queue(DesktopChange.Display); break; // WM_DISPLAYCHANGE
            case 0x02E0: Queue(DesktopChange.Dpi); break;
            case 0x001A when wparam == 0x002F: Queue(DesktopChange.WorkArea); break; // SPI_SETWORKAREA
            case 0x0218 when wparam == 4:
                sleeping = true; Pause(); handled = true; return 1;
            case 0x0218 when wparam == 7 || wparam == 18:
                sleeping = false; Queue(DesktopChange.Resume); handled = true; return 1;
            case 0x02B1 when lparam == session:
                switch ((int)wparam)
                {
                    case 7: locked = true; break;
                    case 8: locked = false; break;
                    case 2: case 4: case 6: disconnected = true; break;
                    case 1: case 3: case 5: disconnected = false; break;
                    default: return 0;
                }
                Queue(DesktopChange.Session); break;
        }
        return 0;
    }

    private bool Unavailable => locked || sleeping || disconnected || changingDisplay;
    private void Pause()
    {
        if (!reportedSuspended && Unavailable) { reportedSuspended = true; suspend(true); }
    }
    private void Queue(DesktopChange change)
    {
        pending |= change;
        if ((change & (DesktopChange.Display | DesktopChange.Dpi | DesktopChange.Shell)) != 0) changingDisplay = true;
        Pause(); timer.Stop(); timer.Interval = TimeSpan.FromMilliseconds(250);
        if (!locked && !sleeping && !disconnected) timer.Start();
    }
    private void Flush()
    {
        timer.Stop();
        if (disposed || locked || sleeping || disconnected) return;
        if (!SessionRegistered && registrationAttempts < 5)
        { registrationAttempts++; SessionRegistered = WTSRegisterSessionNotification(source.Handle, 0); }
        var changes = pending; pending = DesktopChange.None; changingDisplay = false;
        if (changes != DesktopChange.None) refresh(changes);
        // Reposition while the input window is still hidden, then resume interaction.
        if (reportedSuspended && !Unavailable) { reportedSuspended = false; suspend(false); }
        RetryRegistration();
    }
    private void RetryRegistration()
    {
        // Remote Desktop Services may not be ready during login. Retry briefly, never poll forever.
        if (!SessionRegistered && registrationAttempts < 5 && !timer.IsEnabled)
        { timer.Interval = TimeSpan.FromSeconds(2); timer.Start(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); source.RemoveHook(Message);
        if (SessionRegistered) WTSUnRegisterSessionNotification(source.Handle);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
}
