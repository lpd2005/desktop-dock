using System.IO;
using System.Windows;
using DesktopDock.Core;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

internal sealed partial class Controller : IDisposable
{
    private readonly System.Windows.Application application;
    private readonly string data;
    private readonly DesktopIcons desktop;
    private readonly DockSettings settings;
    private readonly string settingsPath;
    private readonly bool preview;
    private readonly bool showWidgets;
    private readonly Forms.NotifyIcon tray;
    private readonly DockWindow dock;
    private readonly TaskbarEnhancement taskbar;
    private readonly DesktopWidgets widgets;
    private readonly UpdateService updates;
    private DesktopPanel? desktopPanel;
    private readonly System.Drawing.Icon brandIcon;
    private (bool Enabled, bool AutoHide, bool Glass, bool Light, bool ReducedMotion)? taskbarConfiguration;
    private SettingsWindow? settingsWindow;
    private bool disposed;
    private bool recoveryRequired, storageNoticeQueued;
    private string? storageMessage;

    public Controller(System.Windows.Application application, string data, DesktopIcons desktop, bool preview, bool stress, bool showWidgets = true)
    {
        this.application = application; this.data = data; this.desktop = desktop; this.preview = preview;
        this.showWidgets = showWidgets;
        settingsPath = Path.Combine(data, "settings.json");
        var loaded = SettingsStore.Read(settingsPath);
        bool firstRun = loaded.State == SettingsLoadState.Missing;
        settings = loaded.Settings;
        recoveryRequired = loaded.NeedsRecovery;
        if (recoveryRequired) storageMessage = loaded.State == SettingsLoadState.BackupAvailable
            ? "原配置无法读取，已临时载入最近有效备份。确认后会保留原件并保存当前配置；恢复前不修改桌面和任务栏。"
            : "原配置无法读取，当前使用临时配置。确认后会保留原件并保存当前配置；恢复前不修改桌面和任务栏。";
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
        if (preview) { settings.HideDesktopIcons = false; recoveryRequired = false; storageMessage = null; }
        if (stress && preview)
        {
            var defaults = settings.Items.ToArray();
            settings.Items = Enumerable.Range(0, 19).Select(i => defaults[i % defaults.Length] with { Name = "Sample " + i }).ToList();
        }
        if (preview) settings.TaskbarEnhancement = false;
        taskbar = new(data);
        updates = new(data);
        dock = new(settings, OpenSettings, DockChanged, Recover, Quit, toggleSettings: ToggleSettings,
            settingsHandle: () => settingsWindow is null ? 0 : new System.Windows.Interop.WindowInteropHelper(settingsWindow).Handle,
            toggleApplications: ToggleApplications,
            applicationsHandle: () => desktopPanel is null ? 0 : new System.Windows.Interop.WindowInteropHelper(desktopPanel).Handle,
            applicationsOpen: () => desktopPanel?.Open == true);
        widgets = new(settings, () => { Save(); settingsWindow?.RefreshWidgets(); }, data, network: !preview);
        dock.RestoreTaskbarRequested = RestoreTaskbar;
        brandIcon = AppBrand.TrayIcon();
        tray = new Forms.NotifyIcon { Text = "Desktop Dock", Icon = brandIcon, Visible = true };
        updates.AutomaticUpdateFound += version => tray.ShowBalloonTip(6000, "Desktop Dock 有新版本", "发现 " + version + "，请在设置的“启动与更新”中下载安装。", Forms.ToolTipIcon.Info);
        tray.DoubleClick += (_, _) => application.Dispatcher.Invoke(OpenSettings);
        tray.BalloonTipClicked += (_, _) => application.Dispatcher.Invoke(OpenUpdates);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 Dock", null, (_, _) => application.Dispatcher.Invoke(dock.Summon));
        menu.Items.Add("Dock 设置…", null, (_, _) => application.Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("软件更新…", null, (_, _) => application.Dispatcher.Invoke(OpenUpdates));
        menu.Items.Add("桌面应用…", null, (_, _) => application.Dispatcher.Invoke(ToggleApplications));
        menu.Items.Add("恢复桌面图标", null, (_, _) => application.Dispatcher.Invoke(Recover));
        menu.Items.Add("唤出顶部任务栏", null, (_, _) => application.Dispatcher.Invoke(taskbar.Reveal));
        menu.Items.Add("恢复原生任务栏", null, (_, _) => application.Dispatcher.Invoke(RestoreTaskbar));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出并恢复桌面", null, (_, _) => application.Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu;
        dock.EnvironmentChanged = change =>
        {
            if ((change & (DesktopChange.Display | DesktopChange.Dpi | DesktopChange.Shell)) != 0) taskbar.EnvironmentChanged();
            desktopPanel?.DismissImmediately(); widgets.Reposition();
            if ((change & DesktopChange.Shell) != 0 && !preview && !recoveryRequired) desktop.Apply(settings.HideDesktopIcons);
        };
        dock.SuspensionChanged = value =>
        {
            if (value) desktopPanel?.DismissImmediately();
            widgets.SetSuspended(value); taskbar.SetSuspended(value);
        };
    }

    public async Task Start()
    {
        await dock.LoadIcons();
        desktop.Apply(!recoveryRequired && settings.HideDesktopIcons);
        if (!recoveryRequired) { ApplyTaskbar(); if (showWidgets) widgets.Apply(); }
        else if (!preview) OpenSettings();
        if (!preview) _ = updates.RunAutomaticChecks(() => settings.AutoCheckUpdates);
    }
    internal void RepositionAfterStartup() => dock.Reposition();
    private void Save()
    {
        if (preview) return;
        if (recoveryRequired) { RefreshStorageNotice(); return; }
        try { SettingsStore.Save(settingsPath, settings); storageMessage = null; }
        catch (SettingsRecoveryRequiredException)
        { recoveryRequired = true; storageMessage = "原配置已无法正常读取，更改尚未保存。确认恢复后会保留原件并保存当前配置。"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { storageMessage = "暂时无法保存配置。更改尚未保存，请检查文件权限或占用，解除后重试。"; }
        RefreshStorageNotice();
    }
    private void RecoverSettings()
    {
        if (preview || !recoveryRequired) return;
        try { SettingsStore.Recover(settingsPath, settings); recoveryRequired = false; storageMessage = null; ApplyRuntime(); RefreshStorageNotice(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { storageMessage = "恢复尚未完成。原配置或备份可能正在被使用，请解除占用后重试；如果文件已被其他程序修改，请重新启动 Dock 后再检查。"; RefreshStorageNotice(); }
    }
    private void RetrySave() { Save(); if (storageMessage is null) ApplyRuntime(); }
    private void RefreshStorageNotice()
    {
        settingsWindow?.SetStorageNotice(storageMessage, recoveryRequired ? "确认恢复并保存" : "重试保存", recoveryRequired ? RecoverSettings : RetrySave);
        if (storageMessage is not null && settingsWindow is null && !storageNoticeQueued)
        {
            storageNoticeQueued = true;
            application.Dispatcher.BeginInvoke(() => { storageNoticeQueued = false; if (!disposed && storageMessage is not null) OpenSettings(); });
        }
    }
    private void DockChanged() { Save(); settingsWindow?.RefreshItems(); }
    private void Apply()
    {
        Save(); if (storageMessage is null) ApplyRuntime();
    }
    private void ApplyRuntime()
    {
        desktop.Apply(!recoveryRequired && settings.HideDesktopIcons); dock.ApplySettings();
        if (!recoveryRequired) { ApplyTaskbar(); if (showWidgets) widgets.Apply(); }
        else { taskbar.Stop(); taskbarConfiguration = null; }
    }
    public void ToggleApplications()
    {
        desktopPanel ??= new(settings, Save, dock.RefreshOcclusion, item => dock.OpenFromPanel(item), item => _ = PinFromPanel(item));
        if (desktopPanel.Open) desktopPanel.Dismiss(); else _ = desktopPanel.Present();
    }
    private async Task PinFromPanel(DockItem item)
    {
        if (settings.Items.Count >= 40 || settings.Items.Any(x => x.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase))) return;
        settings.Items.Add(item); Save(); settingsWindow?.RefreshItems(); await dock.LoadIcons();
    }
    private void ApplyTaskbar()
    {
        var current = (settings.TaskbarEnhancement, settings.TaskbarAutoHide, settings.TaskbarGlass, settings.LightTheme, settings.ReducedMotion);
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
            StartupRegistration? startup = !preview && string.Equals(Path.GetFileName(Environment.ProcessPath), "DesktopDock.exe", StringComparison.OrdinalIgnoreCase)
                ? new(Environment.ProcessPath!) : null;
            settingsWindow = new(settings, Apply, () => { Save(); _ = dock.LoadIcons(); }, Quit, taskbar,
                new SystemSettingsPage(settings, updates, startup, Save, Quit));
            settingsWindow.Closed += (_, _) => { settingsWindow = null; dock.RefreshOcclusion(); };
            settingsWindow.SetStorageNotice(storageMessage, recoveryRequired ? "确认恢复并保存" : "重试保存", recoveryRequired ? RecoverSettings : RetrySave);
            settingsWindow.Show();
            dock.RefreshOcclusion();
        }
        else
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }
    }
    public void OpenUpdates() { OpenSettings(); settingsWindow?.SelectSystemPage(); }
    public void OpenAbout() { OpenSettings(); settingsWindow?.SelectAboutPage(); }
    private void ToggleSettings(nint? foregroundAtPress = null)
    {
        if (settingsWindow is { IsVisible: true } && settingsWindow.WindowState != WindowState.Minimized
            && (foregroundAtPress ?? Native.GetAncestor(Native.GetForegroundWindow(), 3)) == new System.Windows.Interop.WindowInteropHelper(settingsWindow).Handle)
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose();
        updates.Dispose(); taskbar.Dispose(); dock.Dispose(); desktop.Restore();
        desktopPanel?.DisposeWindow(); widgets.Dispose(); brandIcon.Dispose();
    }
}
