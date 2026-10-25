using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DesktopDock.Core;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDock");
        int dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < args.Length) data = Path.GetFullPath(args[dataIndex + 1]);
        bool preview = args.Contains("--preview") || args.Contains("--smoke-test");
        Directory.CreateDirectory(data);
        int probeIndex = Array.IndexOf(args, "--probe-window");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length) return RegressionProbe.Run(args[probeIndex + 1], data);
        using var desktop = new DesktopIcons(data);
        if (args.Contains("--quit"))
        {
            nint live = Native.FindWindow(null, "Desktop Dock");
            if (live != 0) Native.PostMessage(live, 0x0010, 0, 0);
            return 0;
        }
        if (args.Contains("--verify-recovery"))
        {
            desktop.Recover();
            var result = desktop.VerifyRoundTrip();
            File.WriteAllText(Path.Combine(data, "recovery-report.json"), JsonSerializer.Serialize(result));
            return 0;
        }
        if (args.Contains("--recover"))
        {
            nint live = Native.FindWindow(null, "Desktop Dock");
            if (live != 0) Native.PostMessage(live, Native.RecoverMessage, 0, 0);
            else desktop.Recover();
            return 0;
        }
        using var mutex = new Mutex(true, "Local\\DesktopDock.SingleInstance", out bool created);
        if (!created) return 0;
        nint queue = 0;
        Controller? controller = null;
        try
        {
            var options = new Native.QueueOptions { Size = Marshal.SizeOf<Native.QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(Native.CreateDispatcherQueueController(options, out queue));
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.DispatcherUnhandledException += (_, e) =>
            {
                desktop.Restore();
                File.WriteAllText(Path.Combine(data, "last-error.txt"), e.Exception.ToString());
                application.Shutdown(1);
                e.Handled = true;
            };
            desktop.Recover();
            controller = new(application, data, desktop, preview, args.Contains("--stress"));
            application.Startup += async (_, _) =>
            {
                await controller.Start();
                if (args.Contains("--settings")) controller.OpenSettings();
                if (args.Contains("--smoke-test")) await controller.RunSmokeTest();
            };
            return application.Run();
        }
        catch (Exception e)
        {
            desktop.Restore();
            File.WriteAllText(Path.Combine(data, "last-error.txt"), e.ToString());
            if (!args.Contains("--smoke-test")) System.Windows.MessageBox.Show("Dock 无法启动。桌面已尝试恢复。诊断信息保存在本机应用数据目录。", "Desktop Dock");
            return 1;
        }
        finally
        {
            controller?.Dispose();
            desktop.Restore();
            if (queue != 0) Marshal.Release(queue);
        }
    }
}

internal sealed class Controller : IDisposable
{
    private readonly System.Windows.Application application;
    private readonly string data;
    private readonly DesktopIcons desktop;
    private readonly DockSettings settings;
    private readonly bool preview;
    private readonly Forms.NotifyIcon tray;
    private readonly DockWindow dock;
    private SettingsWindow? settingsWindow;
    private bool disposed;

    public Controller(System.Windows.Application application, string data, DesktopIcons desktop, bool preview, bool stress)
    {
        this.application = application; this.data = data; this.desktop = desktop; this.preview = preview;
        string file = Path.Combine(data, "settings.json");
        bool firstRun = !File.Exists(file);
        settings = SettingsStore.Load(file);
        if (firstRun)
        {
            settings.HideDesktopIcons = !preview;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
            settings.Items = [new("文件资源管理器", Path.Combine(windows, "explorer.exe")),
                new("浏览器", File.Exists(edge) ? edge : "microsoft-edge:"), new("记事本", Path.Combine(windows, "System32", "notepad.exe")),
                new("Windows 设置", "ms-settings:"),
                new("下载", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
                new("回收站", "shell:RecycleBinFolder")];
        }
        if (preview) settings.HideDesktopIcons = false;
        if (stress && preview)
        {
            var defaults = settings.Items.ToArray();
            settings.Items = Enumerable.Range(0, 19).Select(i => defaults[i % defaults.Length] with { Name = "Sample " + i }).ToList();
        }
        dock = new(settings, OpenSettings, Save, Recover, Quit, inspectable: preview, toggleSettings: ToggleSettings);
        tray = new Forms.NotifyIcon { Text = "Desktop Dock", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        tray.DoubleClick += (_, _) => application.Dispatcher.Invoke(OpenSettings);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Dock 设置…", null, (_, _) => application.Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("恢复桌面图标", null, (_, _) => application.Dispatcher.Invoke(Recover));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出并恢复桌面", null, (_, _) => application.Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu;
    }

    public async Task Start()
    {
        await dock.LoadIcons();
        desktop.Apply(settings.HideDesktopIcons);
    }
    private void Save()
    {
        if (!preview) SettingsStore.Save(Path.Combine(data, "settings.json"), settings);
    }
    private void Apply() { Save(); desktop.Apply(settings.HideDesktopIcons); dock.ApplySettings(); }
    public void OpenSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new(settings, Apply, () => { Save(); _ = dock.LoadIcons(); }, Quit);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.ShowAboveDock(dock.AvailableWindowArea);
        }
        else
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }
    }
    private void ToggleSettings()
    {
        if (settingsWindow is { IsVisible: true } && settingsWindow.WindowState != WindowState.Minimized) settingsWindow.Close();
        else OpenSettings();
    }
    private void Recover()
    {
        settings.HideDesktopIcons = false;
        desktop.Restore();
        Save();
        settingsWindow?.Close();
    }
    private void Quit() { desktop.Restore(); application.Shutdown(); }

    public async Task RunSmokeTest()
    {
        using var process = Process.GetCurrentProcess();
        string probeData = Path.Combine(data, "probe-" + Guid.NewGuid().ToString("N"));
        using var anchor = RegressionProbe.Start("normal", probeData);
        try
        {
        await RegressionProbe.WaitReady(anchor, probeData);
        dock.CheckVisibility();
        dock.InvokeSettingsEntry();
        await Task.Delay(230);
        bool settingsOpened = settingsWindow is { IsVisible: true };
        bool settingsAboveDock = settingsWindow is not null && AppWindowPlacement.Frame(new System.Windows.Interop.WindowInteropHelper(settingsWindow).Handle).Bottom <= dock.AvailableWindowArea.Bottom + 2;
        dock.InvokeSettingsEntry();
        bool settingsClosedOnSecondClick = settingsWindow is null;
        dock.InvokeSettingsEntry();
        await Task.Delay(220);
        bool settingsReopened = settingsWindow is { IsVisible: true };
        dock.InvokeSettingsEntry();
        if (!settingsOpened || !settingsClosedOnSecondClick || !settingsReopened || !settingsAboveDock)
            throw new InvalidOperationException("Settings toggle or placement regression failed.");
        string launchData = Path.Combine(probeData, "launched");
        Directory.CreateDirectory(launchData);
        string host = Environment.ProcessPath!;
        string hostArguments = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + typeof(Program).Assembly.Location + "\" " : "";
        hostArguments += "--probe-window normal --data-dir \"" + launchData + "\"";
        nint launchedWindow = 0;
        bool launchedWindowAboveDock;
        try
        {
            dock.LaunchForCheck(new("Regression launch", host, hostArguments));
            string ready = Path.Combine(launchData, "ready.json");
            for (int i = 0; i < 100 && !File.Exists(ready); i++) await Task.Delay(50);
            using var state = JsonDocument.Parse(File.ReadAllText(ready));
            launchedWindow = new nint(state.RootElement.GetProperty("WindowHandle").GetInt64());
            for (int i = 0; i < 50 && dock.LaunchWatcherActive; i++) await Task.Delay(50);
            await Task.Delay(100);
            launchedWindowAboveDock = AppWindowPlacement.Frame(launchedWindow).Bottom <= dock.AvailableWindowArea.Bottom + 2;
            if (!launchedWindowAboveDock || dock.LaunchWatcherActive) throw new InvalidOperationException("Launched window regression: " + JsonSerializer.Serialize(new { Frame = AppWindowPlacement.Frame(launchedWindow), Available = dock.AvailableWindowArea, Watching = dock.LaunchWatcherActive, CandidateIsTarget = dock.LastLaunchCandidate == launchedWindow, FitAccepted = dock.LastLaunchFitAccepted }));
        }
        finally { if (launchedWindow != 0) Native.PostMessage(launchedWindow, 0x0010, 0, 0); }
        await Task.Delay(300);
        dock.ClearFrameSamples();
        for (int i = 0; i < 180; i++)
        {
            dock.PreviewWave(.5 + Math.Sin(i * .045) * .42);
            await Task.Delay(8);
        }
        var hoveredInputReport = dock.VerifyInputRegion();
        dock.ResetWave(); await Task.Delay(900);
        var inputReport = dock.VerifyInputRegion();
        if (!inputReport.Passed || !hoveredInputReport.Passed) throw new InvalidOperationException("Dock blocks input outside its visible footprint.");
        var intervals = dock.FrameIntervals.Skip(2).OrderBy(x => x).ToArray();
        var avoidance = new Dictionary<string, bool>();
        foreach (string mode in new[] { "maximized", "fullscreen" })
        {
            await RegressionProbe.SetMode(anchor, probeData, mode);
            dock.CheckVisibility();
            avoidance[mode] = dock.AvoidanceHidden;
            await RegressionProbe.SetMode(anchor, probeData, "normal");
            dock.CheckVisibility();
        }
        bool restoredAfterAvoidance = !dock.AvoidanceHidden;
        if (!restoredAfterAvoidance || avoidance.Values.Any(x => !x)) throw new InvalidOperationException("Foreground avoidance regression failed: " + JsonSerializer.Serialize(avoidance) + "; restored=" + restoredAfterAvoidance);
        if (intervals.Length == 0 || intervals[intervals.Length / 2] > 25) throw new InvalidOperationException("Active animation updates are unexpectedly slow.");
        await Task.Delay(1000);
        process.Refresh();
        var initialCpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        await Task.Delay(5000);
        process.Refresh();
        var report = new
        {
            Version = "0.1.0-preview.2", dock.HardwareRendering, dock.HostBackdrop, dock.IconCount,
            IdleCpuPercent = (process.TotalProcessorTime - initialCpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
            WorkingSetMiB = process.WorkingSet64 / 1048576d,
            SampleSeconds = watch.Elapsed.TotalSeconds,
            InputRegion = inputReport, HoveredInputRegion = hoveredInputReport, Avoidance = avoidance, RestoredAfterAvoidance = restoredAfterAvoidance,
            SettingsOpened = settingsOpened, SettingsClosedOnSecondClick = settingsClosedOnSecondClick, SettingsReopened = settingsReopened,
            SettingsAboveDock = settingsAboveDock, LaunchedWindowAboveDock = launchedWindowAboveDock, LaunchWatcherStopped = !dock.LaunchWatcherActive,
            MotionFrames = intervals.Length, dock.HighResolutionClock,
            MotionMedianMs = intervals.Length > 0 ? intervals[intervals.Length / 2] : 0,
            MotionP95Ms = intervals.Length > 0 ? intervals[(int)((intervals.Length - 1) * .95)] : 0,
            Notes = "Motion values measure UI update cadence, not GPU presentation. No desktop icon changes in smoke mode."
        };
        File.WriteAllText(Path.Combine(data, "smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { RegressionProbe.Close(anchor); }
        Quit();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose();
        dock.Dispose(); desktop.Restore();
    }
}
