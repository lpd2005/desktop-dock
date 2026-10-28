using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class SystemSettingsPage : StackPanel
{
    private readonly UpdateService updates;
    private readonly StartupRegistration? startup;
    private readonly TextBlock startupStatus = Hint(""), updateStatus = Hint(""), notes = Hint("");
    private readonly CheckBox startupCheck = new() { Width = 38, Height = 22, Template = SettingsWindow.SwitchTemplate };
    private readonly Button check, download, install, cancel;
    private readonly ProgressBar progress = new() { Height = 5, Margin = new(0, 10, 0, 10), Maximum = 100 };
    private bool refreshing, installing;
    internal SystemSettingsPage(DockSettings settings, UpdateService updates, StartupRegistration? startup, Action save, Action quit)
    {
        this.updates = updates; this.startup = startup;
        var login = Section("开机自启");
        var loginRow = new DockPanel { Margin = new(0, 3, 0, 12) }; DockPanel.SetDock(startupCheck, Dock.Right); loginRow.Children.Add(startupCheck);
        loginRow.Children.Add(FeatureUi.Text("登录 Windows 后自动启动", 13)); login.Children.Add(loginRow); login.Children.Add(startupStatus);
        System.Windows.Automation.AutomationProperties.SetName(startupCheck, "开机自启");
        startupCheck.Click += (_, _) =>
        {
            if (refreshing || startup is null) return;
            try { startup.Set(startupCheck.IsChecked == true); RefreshStartup(); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            { startupStatus.Text = "设置失败，请确认当前用户有权限修改启动项。"; RefreshStartup(keepMessage: true); }
        };
        var startupOptions = FeatureUi.Button("Windows 启动应用设置", () => Open("ms-settings:startupapps"));
        startupOptions.HorizontalAlignment = HorizontalAlignment.Left; login.Children.Add(startupOptions);
        var update = Section("软件更新");
        update.Children.Add(FeatureUi.Text("当前版本  " + AppVersion.Current, 13));
        var automatic = new CheckBox { Content = "自动检查新版本（每天最多一次）", IsChecked = settings.AutoCheckUpdates, Margin = new(0, 12, 0, 12), FontSize = 13 };
        automatic.Click += (_, _) => { settings.AutoCheckUpdates = automatic.IsChecked == true; save(); }; update.Children.Add(automatic);
        updateStatus.Margin = new(0, 0, 0, 6); update.Children.Add(updateStatus); update.Children.Add(progress);
        var actions = new WrapPanel(); update.Children.Add(actions);
        check = FeatureUi.Button("检查更新", async () => await updates.CheckAsync(), true); actions.Children.Add(check);
        download = FeatureUi.Button("下载更新", async () => await updates.DownloadAsync(), true); actions.Children.Add(download);
        install = FeatureUi.Button("安装并重启", async () =>
        {
            if (installing) return; installing = true; Render();
            try { if (await updates.InstallAsync()) quit(); }
            finally { installing = false; Render(); }
        }, true); actions.Children.Add(install);
        cancel = FeatureUi.Button("取消下载", updates.Cancel); actions.Children.Add(cancel);
        actions.Children.Add(FeatureUi.Button("发布页面", () => Open(ReleaseFeed.Page)));
        notes.Margin = new(0, 14, 0, 0); update.Children.Add(notes);
        Children.Add(Hint("更新通过 GitHub Releases 提供。下载安装程序后按提示完成升级，原有应用列表、分组和组件配置会保留。"));
        Loaded += (_, _) => { RefreshStartup(); updates.Changed += Render; Render(); };
        Unloaded += (_, _) => updates.Changed -= Render;
    }
    private static void Open(string target)
    {
        try { using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
    private void RefreshStartup(bool keepMessage = false)
    {
        refreshing = true;
        try
        {
            startupCheck.IsEnabled = startup is not null; startupCheck.IsChecked = startup?.Enabled == true;
            if (!keepMessage) startupStatus.Text = startup is null ? "预览或开发启动模式不修改系统启动项。" : startup.DisabledByWindows && startup.Enabled
                ? "已登记启动项，但 Windows 已禁用；可在系统启动应用设置中重新开启。" : startup.Enabled ? "已开启。下次登录 Windows 后自动启动。" : "未开启。仅为当前 Windows 用户设置，无需管理员权限。";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { startupCheck.IsEnabled = false; startupStatus.Text = "无法读取系统启动项。"; }
        finally { refreshing = false; }
    }
    private void Render()
    {
        updateStatus.Text = updates.Status;
        check.IsEnabled = !updates.Busy && !installing && updates.Stage != UpdateStage.Ready;
        check.Visibility = updates.Stage == UpdateStage.Ready ? Visibility.Collapsed : Visibility.Visible;
        download.Visibility = updates.Latest is not null && updates.Stage is UpdateStage.Available or UpdateStage.Error ? Visibility.Visible : Visibility.Collapsed;
        install.Visibility = updates.Stage == UpdateStage.Ready ? Visibility.Visible : Visibility.Collapsed; install.IsEnabled = !installing;
        cancel.Visibility = updates.Busy ? Visibility.Visible : Visibility.Collapsed;
        cancel.Content = updates.Stage == UpdateStage.Checking ? "取消检查" : "取消下载";
        progress.Visibility = updates.Stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed; progress.Value = updates.Progress;
        notes.Text = updates.Latest?.Notes ?? "";
    }
    private StackPanel Section(string title)
    {
        var content = new StackPanel(); var heading = FeatureUi.Text(title, 15); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 0, 0, 12); content.Children.Add(heading);
        Children.Add(new Border { Child = content, Background = System.Windows.Media.Brushes.White, CornerRadius = new(16), Padding = new(19), Margin = new(0, 0, 0, 15) }); return content;
    }
    private static TextBlock Hint(string text) { var label = FeatureUi.Text(text, 11, FeatureUi.Muted); label.TextWrapping = TextWrapping.Wrap; label.LineHeight = 18; return label; }
}
