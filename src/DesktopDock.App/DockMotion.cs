using System.Diagnostics;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class DockMotion : IDisposable
{
    private readonly nint input, presentation;
    private readonly TimedTransition transition = new();
    private readonly Stopwatch time = Stopwatch.StartNew();
    private readonly AnimationClock clock;
    private int left, top, lastY = int.MinValue;
    private float scale = 1;
    private int lastAlpha = -1;
    private bool disposed;
    public bool Target => transition.Target > .5;
    public bool Active => transition.Active;
    public double Progress => transition.Value;
    public int Frames { get; private set; }

    public DockMotion(nint input, nint presentation, Dispatcher dispatcher)
    {
        this.input = input; this.presentation = presentation;
        clock = new(dispatcher, Frame);
    }

    public void SetBounds(int left, int top, int width, int height, float scale)
    {
        this.left = left; this.top = top; this.scale = scale;
        lastY = int.MinValue;
        int y = top + (int)Math.Round((1 - Progress) * 28 * scale);
        Native.SetWindowPos(input, 0, left, y, width, height, 0x0014);
        Native.SetWindowPos(presentation, 0, left, y, width, height, 0x0014);
        lastY = y;
    }

    public void Request(bool show, bool immediate = false)
    {
        if (disposed) return;
        if (show == Target && (!immediate || !Active))
        {
            if (!Active && (show
                ? !Native.IsWindowVisible(input) || !Native.IsWindowVisible(presentation)
                : Native.IsWindowVisible(input) || Native.IsWindowVisible(presentation))) Render();
            return;
        }
        if (!show) Native.ShowWindow(input, 0);
        if (immediate)
        {
            clock.Stop(); transition.Reset(show ? 1 : 0); Render(); return;
        }
        transition.Begin(show ? 1 : 0, time.Elapsed.TotalSeconds, show ? .22 : .18);
        Render();
        if (transition.Active) clock.Start();
    }

    private void Frame()
    {
        transition.Sample(time.Elapsed.TotalSeconds); Frames++; Render();
        if (!transition.Active) clock.Stop();
    }

    private void Render()
    {
        int y = top + (int)Math.Round((1 - Progress) * 28 * scale);
        if (lastY != y)
        {
            // Only move the host during visibility animation. Layout changes
            // still affect the separate input region, never the presentation HWND.
            Native.SetWindowPos(input, 0, left, y, 0, 0, 0x0615);
            Native.SetWindowPos(presentation, 0, left, y, 0, 0, 0x0615);
            lastY = y;
        }
        int alpha = (int)Math.Round(255 * Progress);
        if (alpha != lastAlpha)
        {
            if (!Native.SetLayeredWindowAttributes(presentation, 0, (byte)alpha, 2))
                throw new System.ComponentModel.Win32Exception();
            lastAlpha = alpha;
        }
        if (Target || Active || Progress > 0)
        {
            if (!Native.IsWindowVisible(presentation))
            {
                Native.SetWindowPos(presentation, new(-1), 0, 0, 0, 0, 0x0013);
                Native.ShowWindow(presentation, 8);
            }
        }
        else Native.ShowWindow(presentation, 0);
        if (Target && !Active)
        {
            Native.SetWindowPos(input, new(-1), 0, 0, 0, 0, 0x0013);
            Native.ShowWindow(input, 8);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; clock.Dispose();
        Native.ShowWindow(input, 0); Native.ShowWindow(presentation, 0);
    }
}
