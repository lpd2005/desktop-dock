using System.Diagnostics;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class DockSpectrum : IDisposable
{
    private readonly DockSettings settings;
    private readonly SpectrumVisual visual;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly LoopbackSpectrum capture = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly float[] levels = new float[SpectrumAnalyzer.Bands];
    private readonly SpectrumEnvelope envelope = new();
    private readonly bool audioAllowed;
    private bool allowed;
    private volatile bool disposed;
    private volatile bool ticking;
    private int queued;
    private double testUntil, lastTick;
    private string status = "音乐频谱已关闭";
    public event Action? StatusChanged;
    public string Status => status;
    internal bool Testing => clock.Elapsed.TotalSeconds < testUntil;
    internal bool Sampling => capture.Running;
    internal bool Ticking => ticking;
    internal int AudioFrames => capture.Frames;
    internal int CaptureStarts => capture.Starts;
    internal int PresentedFrames { get; private set; }
    internal float Peak { get { float peak = 0; foreach (float value in envelope.Levels) peak = Math.Max(peak, value); return peak; } }

    public DockSpectrum(DockSettings settings, SpectrumVisual visual, Dispatcher dispatcher, bool audioAllowed)
    {
        this.settings = settings; this.visual = visual; this.dispatcher = dispatcher; this.audioAllowed = audioAllowed;
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        timer.Tick += (_, _) => Tick(); capture.Changed += Wake;
    }

    public void SetPresented(bool value)
    {
        if (disposed) return;
        allowed = value && !settings.ReducedMotion;
        capture.SetActive(allowed && settings.Spectrum.Enabled && !Testing && audioAllowed, settings.Spectrum.EnergySaver);
        var interval = TimeSpan.FromMilliseconds(settings.Spectrum.EnergySaver ? 1000d / 15 : 1000d / 30);
        if (timer.Interval != interval) timer.Interval = interval;
        if (!allowed || !Testing && !settings.Spectrum.Enabled) Stop();
        else if (Testing) Start();
        UpdateStatus();
    }

    public void Test()
    {
        if (disposed) return;
        testUntil = settings.ReducedMotion || Testing ? 0 : clock.Elapsed.TotalSeconds + 10;
        SetPresented(allowed); if (Testing && allowed) Tick();
    }
    public void EndTest() { testUntil = 0; SetPresented(allowed); }
    private void Wake()
    {
        if (disposed || ticking && capture.Running || Interlocked.Exchange(ref queued, 1) != 0) return;
        if (dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed) return;
            UpdateStatus();
            if (allowed && settings.Spectrum.Enabled && capture.Running)
            {
                double age = capture.CopyTo(levels, settings.Spectrum.Sensitivity);
                if (age < .3) Start();
            }
        }, DispatcherPriority.Background);
    }

    private void Start()
    {
        if (ticking) return;
        ticking = true; lastTick = clock.Elapsed.TotalSeconds; timer.Start();
    }
    private void Stop()
    {
        ticking = false; timer.Stop(); visual.Hide(); envelope.Reset();
    }
    private void Tick()
    {
        if (!allowed || disposed) { Stop(); return; }
        double now = clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - lastTick, .001, .2); lastTick = now;
        bool testing = Testing;
        if (testUntil > 0 && !testing) { testUntil = 0; SetPresented(allowed); }
        double age;
        if (testing)
        {
            age = 0;
            for (int i = 0; i < levels.Length; i++)
                levels[i] = (float)((.2 + .8 * Math.Pow(.5 + .5 * Math.Sin(i * .44 - now * 3.8), 2)) * (.55 + .45 * Math.Sin(now * 2.3 + i * .1)));
        }
        else age = capture.CopyTo(levels, settings.Spectrum.Sensitivity);
        envelope.Step(levels, age, dt);
        visual.Present(envelope.Levels, envelope.Opacity, settings.Spectrum.EnergySaver); PresentedFrames++;
        if (age > .65 && envelope.Settled) Stop();
        UpdateStatus();
    }
    private void UpdateStatus()
    {
        string next = settings.ReducedMotion ? "减少动画已开启，频谱暂停"
            : Testing ? allowed ? "正在展示模拟频谱（10 秒）" : "模拟测试已暂停（Dock 隐藏或正在交互）"
            : !settings.Spectrum.Enabled ? "音乐频谱已关闭"
            : !audioAllowed ? "安全预览仅支持模拟测试，不采集声音"
            : !allowed ? "Dock 隐藏或正在交互，频谱暂停"
            : capture.Running ? "跟随默认扬声器 / 耳机，等待或响应系统声音" : "正在连接音频设备；不可用时自动重试";
        if (status == next) return;
        status = next; StatusChanged?.Invoke();
    }
    internal void ReconnectForChecks() => capture.ReconnectForChecks();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); capture.Changed -= Wake; capture.Dispose();
    }
}
