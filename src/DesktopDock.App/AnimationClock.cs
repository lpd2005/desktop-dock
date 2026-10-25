using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopDock.App;

// Native Composition content has no WPF visual tree; WPF's Rendering event can throttle to ~30 Hz.
// A high-resolution, cancellable waitable timer drives only active animation. No global timer-period changes.
internal sealed class AnimationClock : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Action tick;
    private readonly AutoResetEvent shutdown = new(false);
    private readonly nint timer;
    private readonly Thread worker;
    private int active, pending;
    private bool disposed;
    public bool HighResolution { get; }

    public AnimationClock(Dispatcher dispatcher, Action tick)
    {
        this.dispatcher = dispatcher; this.tick = tick;
        timer = CreateWaitableTimerEx(0, null, 2, 0x1F0003);
        HighResolution = timer != 0;
        if (timer == 0) timer = CreateWaitableTimerEx(0, null, 0, 0x1F0003);
        if (timer == 0) throw new System.ComponentModel.Win32Exception();
        worker = new Thread(Pump) { IsBackground = true, Name = "Dock active animation clock" };
        worker.Start();
    }

    public void Start()
    {
        if (disposed || Interlocked.Exchange(ref active, 1) != 0) return;
        long due = -10000;
        if (!SetWaitableTimer(timer, ref due, 8, 0, 0, false))
        { Interlocked.Exchange(ref active, 0); throw new System.ComponentModel.Win32Exception(); }
    }

    public void Stop()
    {
        if (disposed || Interlocked.Exchange(ref active, 0) == 0) return;
        CancelWaitableTimer(timer);
    }

    private void Pump()
    {
        nint[] handles = [shutdown.SafeWaitHandle.DangerousGetHandle(), timer];
        while (WaitForMultipleObjects(2, handles, false, uint.MaxValue) == 1)
        {
            if (Volatile.Read(ref active) == 0 || Interlocked.Exchange(ref pending, 1) != 0) continue;
            if (dispatcher.HasShutdownStarted) break;
            try { dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref pending, 0);
                if (Volatile.Read(ref active) != 0) tick();
            }, DispatcherPriority.Input); }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { break; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop(); disposed = true;
        shutdown.Set(); worker.Join(500);
        CloseHandle(timer); shutdown.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(nint handle, ref long dueTime, int period, nint callback, nint arg, bool resume);
    [DllImport("kernel32.dll")] private static extern bool CancelWaitableTimer(nint handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForMultipleObjects(uint count, nint[] handles, bool all, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
