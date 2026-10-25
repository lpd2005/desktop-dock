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
        if (args.Contains("--taskbar-guard")) return TaskbarRecovery.RunGuard(args, data);
        if (args.Contains("--taskbar-test")) return TaskbarChecks.Run(data);
        if (args.Contains("--taskbar-exit-probe")) return TaskbarChecks.RunExitProbe(data);
        if (args.Contains("--inspect-taskbar"))
        {
            File.WriteAllText(Path.Combine(data, "taskbar-inspection.json"), JsonSerializer.Serialize(TaskbarNative.Capture(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        int probeIndex = Array.IndexOf(args, "--probe-window");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            int grantIndex = Array.IndexOf(args, "--allow-foreground");
            uint parent = grantIndex >= 0 && grantIndex + 1 < args.Length && uint.TryParse(args[grantIndex + 1], out uint parsed) ? parsed : 0;
            return RegressionProbe.Run(args[probeIndex + 1], data, parent);
        }
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
            if (live != 0) { Native.PostMessage(live, Native.RecoverMessage, 0, 0); Native.PostMessage(live, Native.RestoreTaskbarMessage, 0, 0); }
            else { desktop.Recover(); TaskbarRecovery.Restore(data); }
            return 0;
        }
        if (args.Contains("--recover-taskbar"))
        {
            nint live = Native.FindWindow(null, "Desktop Dock");
            if (live != 0) Native.PostMessage(live, Native.RestoreTaskbarMessage, 0, 0);
            else TaskbarRecovery.Restore(data);
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
            TaskbarRecovery.Restore(data);
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
    private readonly TaskbarEnhancement taskbar;
    private (bool Enabled, bool AutoHide, bool Glass, bool Light)? taskbarConfiguration;
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
        if (preview) settings.TaskbarEnhancement = false;
        taskbar = new(data);
        dock = new(settings, OpenSettings, Save, Recover, Quit, toggleSettings: ToggleSettings);
        dock.RestoreTaskbarRequested = RestoreTaskbar;
        tray = new Forms.NotifyIcon { Text = "Desktop Dock", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        tray.DoubleClick += (_, _) => application.Dispatcher.Invoke(OpenSettings);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("召回 Dock（" + dock.RecallShortcut + "）", null, (_, _) => application.Dispatcher.Invoke(dock.Summon));
        menu.Items.Add("Dock 设置…", null, (_, _) => application.Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("恢复桌面图标", null, (_, _) => application.Dispatcher.Invoke(Recover));
        menu.Items.Add("唤出顶部任务栏", null, (_, _) => application.Dispatcher.Invoke(taskbar.Reveal));
        menu.Items.Add("恢复原生任务栏", null, (_, _) => application.Dispatcher.Invoke(RestoreTaskbar));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出并恢复桌面", null, (_, _) => application.Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu;
    }

    public async Task Start()
    {
        await dock.LoadIcons();
        desktop.Apply(settings.HideDesktopIcons);
        ApplyTaskbar();
    }
    private void Save()
    {
        if (!preview) SettingsStore.Save(Path.Combine(data, "settings.json"), settings);
    }
    private void Apply() { Save(); desktop.Apply(settings.HideDesktopIcons); dock.ApplySettings(); ApplyTaskbar(); }
    private void ApplyTaskbar()
    {
        var current = (settings.TaskbarEnhancement, settings.TaskbarAutoHide, settings.TaskbarGlass, settings.LightTheme);
        if (taskbarConfiguration == current) return;
        taskbarConfiguration = current;
        taskbar.Apply(settings);
    }
    private void RestoreTaskbar()
    {
        settings.TaskbarEnhancement = false; taskbarConfiguration = null;
        taskbar.Stop(); Save(); settingsWindow?.Close();
    }
    public void OpenSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new(settings, Apply, () => { Save(); _ = dock.LoadIcons(); }, Quit, dock.RecallShortcut, taskbar);
            settingsWindow.Closed += (_, _) => { settingsWindow = null; dock.RefreshOcclusion(); };
            settingsWindow.Show();
            dock.RefreshOcclusion();
        }
        else
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }
    }
    private void ToggleSettings()
    {
        if (settingsWindow is { IsVisible: true } && settingsWindow.WindowState != WindowState.Minimized
            && Native.GetAncestor(Native.GetForegroundWindow(), 3) == new System.Windows.Interop.WindowInteropHelper(settingsWindow).Handle)
            settingsWindow.WindowState = WindowState.Minimized;
        else OpenSettings();
    }
    private void Recover()
    {
        settings.HideDesktopIcons = false;
        desktop.Restore();
        Save();
        settingsWindow?.Close();
    }
    private void Quit() { taskbar.Stop(); desktop.Restore(); application.Shutdown(); }

    public async Task RunSmokeTest()
    {
        using var process = Process.GetCurrentProcess();
        string probeData = Path.Combine(data, "probe-" + Guid.NewGuid().ToString("N"));
        using var anchor = RegressionProbe.Start("normal", probeData);
        var testWindows = new List<nint>();
        var children = new List<nint>();
        void Require(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
        async Task WaitFor(Func<bool> condition, string description, int attempts = 60)
        {
            for (int i = 0; i < attempts; i++) { if (condition()) return; await Task.Delay(50); }
            throw new InvalidOperationException(description);
        }
        try
        {
            await RegressionProbe.WaitReady(anchor, probeData);
            nint anchorWindow = RegressionProbe.Handle(probeData);
            testWindows.Add(anchorWindow);
            dock.UseOcclusionWindowsForChecks(() => testWindows.ToArray());
            await Task.Delay(400);
            dock.CheckVisibility();
            Require(dock.IsDockVisible && dock.ExcludedFromAltTab && dock.HotkeyRegistered, "Initial visibility, Alt+Tab exclusion or hotkey registration failed.");

            await RegressionProbe.SetMode(anchor, probeData, "covered");
            dock.RefreshOcclusion();
            Require(!dock.IsDockVisible, "Ordinary overlapping window did not hide Dock.");
            dock.InvokeHotkeyForChecks();
            await WaitFor(() => dock.IsDockVisible, "Hotkey did not recall covered Dock.");
            await Task.Delay(3300);
            dock.CheckVisibility();
            bool recallTimedOut = !dock.IsDockVisible;
            Require(recallTimedOut, "Recall did not hide after three idle seconds.");
            dock.InvokeHotkeyForChecks();
            await WaitFor(() => dock.IsDockVisible, "Second recall failed.");
            await Task.Delay(1800);
            dock.InteractForChecks();
            await Task.Delay(1400); dock.CheckVisibility();
            bool interactionResetTimeout = dock.IsDockVisible;
            await Task.Delay(1800); dock.CheckVisibility();
            Require(interactionResetTimeout && !dock.IsDockVisible, "Interaction reset or subsequent timeout failed.");

            dock.SetSwitchingForChecks(true);
            await RegressionProbe.SetMode(anchor, probeData, "normal");
            dock.RefreshOcclusion();
            Require(!dock.IsDockVisible, "Dock flashed while task switching.");
            dock.SetSwitchingForChecks(false);
            bool noImmediateFlash = !dock.IsDockVisible;
            await Task.Delay(400); dock.CheckVisibility();
            Require(noImmediateFlash && dock.IsDockVisible, "Task-switch stabilization failed.");
            dock.SetSwitchingForChecks(null);

            dock.InvokeSettingsEntry(); await Task.Delay(200);
            bool settingsOpened = settingsWindow is { IsVisible: true };
            Require(settingsOpened, "Settings did not open.");
            nint settingsHandle = new System.Windows.Interop.WindowInteropHelper(settingsWindow!).Handle;
            bool settingsExcluded = (Native.GetWindowLongPtr(settingsHandle, -20).ToInt64() & (0x80 | 0x40000)) == 0x80;
            dock.InvokeSettingsEntry(); await Task.Delay(150);
            bool settingsMinimized = settingsWindow?.WindowState == WindowState.Minimized;
            dock.InvokeSettingsEntry(); await Task.Delay(200);
            bool settingsRestored = settingsWindow is { IsVisible: true, WindowState: WindowState.Normal };
            Require(settingsExcluded && settingsMinimized && settingsRestored, "Settings taskbar toggle or Alt+Tab exclusion failed.");
            settingsWindow?.Close();

            string host = Environment.ProcessPath!;
            string prefix = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "\"" + typeof(Program).Assembly.Location + "\" " : "";
            var applicationChecks = new Dictionary<string, bool>();
            foreach (bool shortcut in new[] { false, true })
            {
                string launchData = Path.Combine(probeData, shortcut ? "shortcut" : "executable");
                Directory.CreateDirectory(launchData);
                string arguments = prefix + "--probe-window normal --allow-foreground " + Environment.ProcessId + " --data-dir \"" + launchData + "\"";
                DockItem item;
                if (shortcut)
                {
                    string link = Path.Combine(probeData, "Example.lnk");
                    RegressionProbe.CreateShortcut(link, host, arguments);
                    item = new("Example shortcut", link);
                }
                else item = new("Example executable", host, arguments);
                dock.LaunchForCheck(item);
                string ready = Path.Combine(launchData, "ready.json");
                await WaitFor(() => File.Exists(ready), "Application did not launch.");
                await Task.Delay(100);
                nint child = RegressionProbe.Handle(launchData);
                children.Add(child); testWindows.Add(child);
                var originalFrame = WindowOcclusion.Frame(child);
                await WaitFor(() => !dock.LaunchWatcherActive, "App identification watcher did not stop.");
                Require(dock.LastAppWindow == child, "Wrong app window was associated.");
                bool notMoved = WindowOcclusion.Frame(child) == originalFrame;
                dock.LaunchForCheck(item);
                var action = dock.LastAppAction;
                await WaitFor(() => Native.IsIconic(child), "Repeated active app click did not minimize: " + action);
                dock.LaunchForCheck(item);
                bool restoring = dock.LastAppAction == AppClickAction.Restore;
                await WaitFor(() => !Native.IsIconic(child), "App did not restore.");
                OpenSettings(); await Task.Delay(150);
                settingsWindow!.Activate();
                nint backgroundController = new System.Windows.Interop.WindowInteropHelper(settingsWindow).Handle;
                await WaitFor(() => Native.GetAncestor(Native.GetForegroundWindow(), 3) == backgroundController, "Test controller could not acquire foreground.");
                dock.LaunchForCheck(item);
                bool activated = dock.LastAppAction == AppClickAction.Activate;
                await WaitFor(() => Native.GetAncestor(Native.GetForegroundWindow(), 3) == child, "Background app did not activate.");
                settingsWindow?.Close();
                applicationChecks[shortcut ? "ShortcutToggleAndNoMove" : "ExecutableToggleAndNoMove"] = restoring && notMoved;
                applicationChecks[shortcut ? "ShortcutBackgroundActivate" : "ExecutableBackgroundActivate"] = activated;
                Native.PostMessage(child, 0x0010, 0, 0);
                await Task.Delay(250);
                testWindows.Remove(child);
                Require(restoring && notMoved && activated, "Application regression: " + JsonSerializer.Serialize(new { Shortcut = shortcut, Restored = restoring, NoMove = notMoved, BackgroundActivated = activated, Action = dock.LastAppAction.ToString() }));
            }

            await RegressionProbe.SetMode(anchor, probeData, "normal");
            dock.RefreshOcclusion(); await Task.Delay(400); dock.CheckVisibility();
            dock.ClearFrameSamples();
            for (int i = 0; i < 180; i++) { dock.PreviewWave(.5 + Math.Sin(i * .045) * .42); await Task.Delay(8); }
            var hoveredInput = dock.VerifyInputRegion();
            dock.ResetWave(); await Task.Delay(900);
            var input = dock.VerifyInputRegion();
            Require(dock.IsDockVisible && input.Passed && hoveredInput.Passed, "Shaped input region regression failed.");
            var intervals = dock.FrameIntervals.Skip(2).OrderBy(x => x).ToArray();
            Require(intervals.Length > 0 && intervals[intervals.Length / 2] < 25, "Animation update cadence regression.");

            var avoidance = new Dictionary<string, bool>();
            foreach (string mode in new[] { "covered", "maximized", "fullscreen" })
            {
                await RegressionProbe.SetMode(anchor, probeData, mode);
                dock.RefreshOcclusion();
                avoidance[mode] = !dock.IsDockVisible;
                await RegressionProbe.SetMode(anchor, probeData, "normal");
                dock.RefreshOcclusion(); await Task.Delay(400); dock.CheckVisibility();
            }
            Require(avoidance.Values.All(x => x) && dock.IsDockVisible, "Coverage, fullscreen or stable reveal regression.");
            process.Refresh(); var initialCpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            await Task.Delay(5000); process.Refresh();
            var report = new
            {
                Version = "0.1.0-preview.4", dock.HardwareRendering, dock.HostBackdrop, dock.IconCount,
                dock.RecallShortcut, dock.HotkeyRegistered, RecallTimedOut = recallTimedOut, InteractionResetTimeout = interactionResetTimeout,
                DockExcludedFromAltTab = dock.ExcludedFromAltTab, SettingsExcludedFromAltTab = settingsExcluded, NoTaskSwitchFlash = noImmediateFlash,
                SettingsOpened = settingsOpened, SettingsMinimized = settingsMinimized, SettingsRestored = settingsRestored,
                ApplicationChecks = applicationChecks, Avoidance = avoidance,
                InputRegion = input, HoveredInputRegion = hoveredInput,
                IdleCpuPercent = (process.TotalProcessorTime - initialCpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                WorkingSetMiB = process.WorkingSet64 / 1048576d, SampleSeconds = watch.Elapsed.TotalSeconds,
                MotionFrames = intervals.Length, MotionMedianMs = intervals[intervals.Length / 2],
                MotionP95Ms = intervals[(int)((intervals.Length - 1) * .95)],
                Notes = "Controlled test windows; app update cadence is not GPU presentation. Alt+Tab styles and transition policy checked without injecting keys."
            };
            File.WriteAllText(Path.Combine(data, "smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (nint child in children) if (Native.IsWindow(child)) Native.PostMessage(child, 0x0010, 0, 0);
            RegressionProbe.Close(anchor);
        }
        Quit();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose();
        taskbar.Dispose(); dock.Dispose(); desktop.Restore();
    }
}
