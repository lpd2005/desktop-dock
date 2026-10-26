using System.Diagnostics;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class TaskbarMotion : IDisposable
{
    private readonly TaskbarSnapshot snapshot;
    private readonly TaskbarTransparency original;
    private readonly Action failed;
    private readonly TimedTransition transition = new(1);
    private readonly Stopwatch time = Stopwatch.StartNew();
    private readonly AnimationClock clock;
    private BitmapOverlay? overlay;
    private byte[]? cached;
    private double capturedAt = double.NegativeInfinity;
    public bool Active => transition.Active;
    public bool Target => transition.Target > .5;
    public double Progress => transition.Value;
    public int Frames { get; private set; }
    public bool SlideUsed { get; private set; }
    public bool Enabled { get; set; } = true;
    public TaskbarMotion(TaskbarSnapshot snapshot, TaskbarTransparency original, Action failed)
    {
        this.snapshot = snapshot; this.original = original; this.failed = failed;
        clock = new(Dispatcher.CurrentDispatcher, Frame);
    }
    public bool Request(bool show, bool immediate = false)
    {
        if (show == Target && !immediate) return true;
        if (immediate || !Enabled)
        {
            clock.Stop(); overlay?.Dispose(); overlay = null;
            transition.Reset(show ? 1 : 0);
            return TaskbarNative.Present((nint)snapshot.Handle, show, original);
        }
        double now = time.Elapsed.TotalSeconds;
        transition.Sample(now);
        if (!show && transition.Value > .999 && TaskbarNative.IsPresented((nint)snapshot.Handle))
        {
            byte[]? pixels = BitmapOverlay.CaptureTaskbar(snapshot);
            if (pixels is not null) { cached = pixels; capturedAt = now; }
        }
        if (overlay is null && cached is not null && now - capturedAt < 30)
        {
            try
            {
                overlay = new(cached, snapshot.Frame.Right - snapshot.Frame.Left, snapshot.Frame.Bottom - snapshot.Frame.Top);
                SlideUsed = true;
            }
            catch (System.ComponentModel.Win32Exception) { overlay?.Dispose(); overlay = null; }
        }
        if (show && !TaskbarNative.Raise((nint)snapshot.Handle)) return false;
        transition.Begin(show ? 1 : 0, now, show ? .22 : .18);
        if (!Render()) return false;
        clock.Start(); return true;
    }
    private void Frame()
    {
        transition.Sample(time.Elapsed.TotalSeconds);
        Frames++;
        if (!Render()) { clock.Stop(); failed(); return; }
        if (!transition.Active)
        {
            clock.Stop(); overlay?.Dispose(); overlay = null;
        }
    }
    private bool Render()
    {
        double progress = transition.Value;
        double alpha = progress;
        if (overlay is not null)
        {
            // Crossfade the last part into the current native content, so a cached
            // clock or running-app marker never snaps abruptly at the handoff.
            double handoff = Target ? Math.Clamp((progress - .65) / .35, 0, 1) : 0;
            alpha = handoff;
            int height = snapshot.Frame.Bottom - snapshot.Frame.Top;
            try { overlay.Place(snapshot.Frame.Left, snapshot.Frame.Top - (int)Math.Round(height * (1 - progress)), (byte)Math.Round(255 * (1 - handoff))); }
            catch (System.ComponentModel.Win32Exception) { overlay.Dispose(); overlay = null; alpha = progress; }
        }
        if (!transition.Active) alpha = Target ? 1 : 0;
        return TaskbarNative.SetAlpha((nint)snapshot.Handle, (byte)Math.Round(original.Alpha * alpha), original);
    }
    public void Invalidate() { cached = null; capturedAt = double.NegativeInfinity; }
    public void Dispose() { clock.Dispose(); overlay?.Dispose(); overlay = null; cached = null; }
}
