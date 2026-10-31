using System.Diagnostics;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class TaskbarEnhancement : IDisposable
{
    private readonly string data;
    private readonly GlassCompanion glass;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TaskbarVisibility visibility = new();
    private TaskbarRecoveryState? state;
    private EventWaitHandle? guardStop;
    private Process? guard;
    private TaskbarMotion? motion;
    private bool autoHide, disposed, suspended;
    private double lastValidation;
    public string Status { get; private set; } = "任务栏增强未启用";
    public string GlassStatus => glass.Status;
    public event Action? StatusChanged;
    public bool Active => state is not null;
    internal bool GlassRunning => glass.IsRunning;
    internal bool GuardRunning => guard is { HasExited: false };
    internal TaskbarSnapshot? Snapshot => state?.Snapshot;
    internal int GuardProcessId => guard is { HasExited: false } ? guard.Id : 0;
    internal bool MotionActive => motion?.Active == true;
    internal int MotionFrames => motion?.Frames ?? 0;
    internal bool SlideUsed => motion?.SlideUsed == true;
    internal Func<(bool Edge, bool Over, bool Shell, bool Switching)>? InputForChecks { get; set; }
    internal Func<(Native.Point Pointer, bool Down, bool Shell, bool Switching)>? PointerForChecks { get; set; }

    public TaskbarEnhancement(string data)
    {
        this.data = data; glass = new(data);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => Tick();
    }
    public void Apply(DockSettings settings)
    {
        if (disposed) return;
        if (!settings.TaskbarEnhancement) { Stop(); return; }
        if (state is null)
        {
            if (!TaskbarRecovery.Restore(data)) { Status = "上次任务栏状态尚未恢复，请先恢复任务栏"; Changed(); return; }
            var snapshot = TaskbarNative.Capture();
            if (snapshot is null || !snapshot.IsTop)
            { Status = "请先在 Windows 中将主屏原生任务栏设为顶部，再启用增强"; Changed(); return; }
            if (!snapshot.WasVisible || snapshot.ProcessStart == 0)
            { Status = "请先唤出原生任务栏，再启用增强"; Changed(); return; }
            try
            {
                state = new(snapshot, false);
                TaskbarRecovery.Save(data, state); // Before any system change.
                string name = "Local\\DesktopDock.TaskbarGuard." + Guid.NewGuid().ToString("N");
                guardStop = new(false, EventResetMode.ManualReset, name);
                guard = TaskbarRecovery.StartGuard(data, guardStop, name);
                visibility = new();
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { Stop(); Status = "恢复保护无法启动，任务栏保持原状"; Changed(); return; }
        }
        autoHide = settings.TaskbarAutoHide && !state.Snapshot.NativeAutoHide;
        if (autoHide && state.Transparency is null)
        {
            var transparency = TaskbarNative.CaptureTransparency((nint)state.Snapshot.Handle);
            if (transparency is null || transparency.Alpha == 0)
            { Stop(); Status = "任务栏外观由其他程序管理，无法启用隐藏"; Changed(); return; }
            state = state with { Transparency = transparency };
            TaskbarRecovery.Save(data, state);
        }
        if (!autoHide && state.Transparency is not null)
        {
            motion?.Dispose(); motion = null;
            if (!TaskbarNative.RestoreTransparency((nint)state.Snapshot.Handle, state.Transparency))
            { Stop(); Status = "任务栏外观无法恢复，已停止增强"; Changed(); return; }
            state = state with { Transparency = null };
            TaskbarRecovery.Save(data, state);
        }
        if (autoHide && motion is null)
            motion = new(state.Snapshot, state.Transparency!, () => { Stop(); Status = "任务栏过渡不可用，已恢复"; Changed(); });
        if (motion is not null)
        {
            motion.Enabled = !settings.ReducedMotion;
            motion.Invalidate();
            if (settings.ReducedMotion && motion.Active) motion.Request(motion.Target, true);
        }
        bool release = autoHide && state.Snapshot.CanReleaseTop;
        if (release != state.WorkChanged)
        {
            var current = TaskbarNative.Capture();
            var expected = state.WorkChanged ? state.Snapshot.ExpandedWork : state.Snapshot.Work;
            if (current?.Work == expected)
            {
                // Persist the intent first, so a crash between the API and the next statement is recoverable.
                if (release) { state = state with { WorkChanged = true }; TaskbarRecovery.Save(data, state); }
                if (!TaskbarNative.SetWork(release ? state.Snapshot.ExpandedWork : state.Snapshot.Work))
                { Stop(); Status = "无法调整工作区，任务栏已恢复"; Changed(); return; }
                if (!release) { state = state with { WorkChanged = false }; TaskbarRecovery.Save(data, state); }
            }
            else { Stop(); Status = "工作区已被其他程序更改，已停止任务栏增强"; Changed(); return; }
        }
        glass.Apply(settings.TaskbarGlass, settings.LightTheme);
        state = state with { GlassProcess = glass.ProcessId, GlassStart = glass.ProcessStart };
        TaskbarRecovery.Save(data, state);
        Status = state.Snapshot.NativeAutoHide ? "顶部任务栏 · 使用系统已有的自动隐藏"
            : autoHide ? "顶部任务栏 · 顶边停留唤出，移开后隐藏" : "顶部任务栏 · 保持显示";
        if (autoHide && !state.Snapshot.CanReleaseTop) Status += "（保留其他程序预留的工作区）";
        if (!suspended) { timer.Start(); Tick(); } else motion?.Request(true, true);
        Changed();
    }
    private void Tick()
    {
        if (state is null || suspended) return;
        double now = clock.Elapsed.TotalSeconds;
        if (now - lastValidation >= 1)
        {
            lastValidation = now;
            var current = TaskbarNative.Capture();
            if (!TaskbarNative.Matches(state.Snapshot) || current is null || !current.IsTop || current.Frame != state.Snapshot.Frame || current.Monitor != state.Snapshot.Monitor
                || current.NativeAutoHide != state.Snapshot.NativeAutoHide)
            { Stop(); Status = "任务栏位置、显示器或资源管理器已变化；已恢复，请重新启用"; Changed(); return; }
            if (state.WorkChanged && current.Work != state.Snapshot.ExpandedWork)
            {
                if (current.Work != state.Snapshot.Work || !TaskbarNative.SetWork(state.Snapshot.ExpandedWork))
                { Stop(); Status = "工作区已变化，已恢复任务栏并停止增强"; Changed(); return; }
                // Explorer may re-reserve the original strip while the glass component initializes.
                // Reconcile only our exact original reservation; never replace a different work area.
            }
            glass.RefreshStatus(); Changed();
        }
        if (!autoHide) return;
        if (!Native.GetCursorPos(out var point)) return;
        var snapshot = state.Snapshot;
        bool down = TaskbarNative.Down(1), shellInteraction = TaskbarNative.ShellInteraction(snapshot);
        bool switching = TaskbarNative.Down(0x12) && (TaskbarNative.Down(9) || !TaskbarNative.Down(0x11));
        if (PointerForChecks is not null)
            (point, down, shellInteraction, switching) = PointerForChecks();
        bool edge = TaskbarVisibility.AtTopEdge(snapshot.Monitor, point.X, point.Y);
        bool over = TaskbarVisibility.OverVisibleBar(snapshot.Frame, motion?.Progress ?? 0, point.X, point.Y);
        bool shell = shellInteraction || over && (down || motion?.Active == true);
        if (InputForChecks is not null) (edge, over, shell, switching) = InputForChecks();
        bool show = visibility.Update(now, edge, over, shell, switching);
        bool externallyChanged = motion is not null && show == motion.Target && !motion.Active && show != TaskbarNative.IsPresented((nint)snapshot.Handle);
        if (motion is not null && (show != motion.Target || switching && motion.Active || externallyChanged))
        {
            if (!motion.Request(show, switching || externallyChanged))
            { Stop(); Status = "任务栏呈现状态无法改变，已恢复"; Changed(); }
        }
    }
    public void Reveal()
    {
        if (suspended) return;
        visibility.Reveal(clock.Elapsed.TotalSeconds);
        if (motion is not null) motion.Request(true);
    }
    internal void SetSuspended(bool value)
    {
        if (disposed || suspended == value) return;
        suspended = value; visibility = new();
        if (value) { timer.Stop(); motion?.Request(true, true); }
        else if (state is not null) { lastValidation = double.NegativeInfinity; timer.Start(); Tick(); }
    }
    internal void EnvironmentChanged()
    {
        if (!Active) return;
        bool restored = Stop();
        Status = restored ? "显示环境或资源管理器已变化；已恢复任务栏，请重新启用增强"
            : "显示环境或资源管理器已变化；恢复记录已保留，请使用“恢复任务栏”重试";
        Changed();
    }
    public bool Stop()
    {
        timer.Stop();
        motion?.Dispose(); motion = null;
        glass.Dispose();
        bool restored = TaskbarRecovery.Restore(data);
        guardStop?.Set(); guardStop?.Dispose(); guardStop = null;
        guard?.Dispose(); guard = null; state = null;
        Status = restored ? "任务栏增强未启用，已恢复原状" : "恢复记录已保留，请使用“恢复任务栏”重试";
        Changed();
        return restored;
    }
    private void Changed() => StatusChanged?.Invoke();
    internal void TickForChecks() => Tick();
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}
