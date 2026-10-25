using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class SettingsWindow : Window
{
    private readonly DockSettings settings;
    private readonly Action apply;
    private readonly Action reload;
    private readonly ListBox applications;
    private readonly TextBlock status;
    private readonly FrameworkElement surface;

    public SettingsWindow(DockSettings settings, Action apply, Action reload, Action quit)
    {
        this.settings = settings; this.apply = apply; this.reload = reload;
        Title = "Desktop Dock · 设置";
        Width = 570; Height = 730; MinWidth = 500; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 34));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var content = new StackPanel { Margin = new Thickness(30, 25, 30, 25) };
        surface = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = surface;
        content.Children.Add(new TextBlock { Text = "DESKTOP DOCK  /  0.1", FontSize = 11, Foreground = Brushes.LightSteelBlue });
        content.Children.Add(new TextBlock { Text = "让桌面，轻一点。", FontSize = 29, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 8) });
        content.Children.Add(new TextBlock { Text = "悬停放大 · 原生合成 · 随时恢复", Foreground = Brushes.LightSlateGray, Margin = new(0, 0, 0, 20) });
        AddSlider(content, "图标大小", 32, 80, settings.IconSize, v => settings.IconSize = v);
        AddSlider(content, "悬停放大", 1, 1.8, settings.Magnification, v => settings.Magnification = v);
        AddSlider(content, "底部间距", 0, 100, settings.BottomGap, v => settings.BottomGap = v);
        AddCheck(content, "背景毛玻璃", settings.Blur, v => settings.Blur = v);
        AddCheck(content, "浅色玻璃", settings.LightTheme, v => settings.LightTheme = v);
        AddCheck(content, "减少动画", settings.ReducedMotion, v => settings.ReducedMotion = v);
        AddCheck(content, "应用启动反馈", settings.LaunchAnimation, v => settings.LaunchAnimation = v);
        AddCheck(content, "从 Dock 打开的普通窗口放在 Dock 上方", settings.KeepLaunchedWindowsAboveDock, v => settings.KeepLaunchedWindowsAboveDock = v);
        AddCheck(content, "自动隐藏 Dock", settings.AutoHide, v => settings.AutoHide = v);
        AddCheck(content, "其他应用最大化时隐藏（全屏始终避让）", settings.HideWhenMaximized, v => settings.HideWhenMaximized = v);
        AddCheck(content, "隐藏桌面图标（退出时恢复）", settings.HideDesktopIcons, v => settings.HideDesktopIcons = v);
        content.Children.Add(new TextBlock { Text = "固定的入口", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 22, 0, 10) });
        applications = new ListBox { Height = 125, Background = new SolidColorBrush(Color.FromRgb(32, 38, 52)), Foreground = Brushes.White, BorderThickness = new(0), Padding = new(8) };
        RefreshItems(); content.Children.Add(applications);
        var actions = new WrapPanel { Margin = new(0, 10, 0, 8) };
        actions.Children.Add(Button("＋ 添加应用", AddApplication));
        actions.Children.Add(Button("＋ 文件夹", AddFolder));
        actions.Children.Add(Button("移除所选", RemoveSelected));
        content.Children.Add(actions);
        content.Children.Add(new TextBlock { Text = "Dock 内拖动图标可调整顺序。原生任务栏请在 Windows 中设置。", FontSize = 11, Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap });
        status = new TextBlock { FontSize = 11, Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
        content.Children.Add(status);
        content.Children.Add(Button("退出并恢复桌面", quit));
    }

    public void ShowAboveDock(WindowBounds available)
    {
        // WPF uses DIP; native placement and DWM bounds use physical pixels.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        double dpi = Native.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle) / 96d;
        WindowStartupLocation = WindowStartupLocation.Manual;
        double width = Math.Max(240, (available.Right - available.Left) / dpi);
        double height = Math.Max(200, (available.Bottom - available.Top) / dpi);
        MinWidth = Math.Min(500, width); MinHeight = Math.Min(500, height);
        Width = Math.Min(570, width); Height = Math.Min(730, height);
        Left = available.Left / dpi + (width - Width) / 2;
        Top = available.Top / dpi + (height - Height) / 2;
        Show();
        if (settings.ReducedMotion || !settings.LaunchAnimation) return;
        var transform = new TranslateTransform();
        surface.RenderTransform = transform;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);
        surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    private void AddSlider(Panel content, string name, double min, double max, double value, Action<double> setter)
    {
        var caption = new TextBlock { Text = $"{name}   {value:0.##}", Foreground = Brushes.LightGray, FontSize = 12, Margin = new(0, 0, 0, 4) };
        content.Children.Add(caption);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new(0, 0, 0, 14) };
        slider.ValueChanged += (_, _) => { setter(slider.Value); caption.Text = $"{name}   {slider.Value:0.##}"; apply(); };
        content.Children.Add(slider);
    }

    private void AddCheck(Panel content, string text, bool value, Action<bool> setter)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Foreground = Brushes.White, Margin = new(0, 5, 0, 5) };
        box.Checked += (_, _) => { setter(true); apply(); };
        box.Unchecked += (_, _) => { setter(false); apply(); };
        content.Children.Add(box);
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new(12, 6, 12, 6), Margin = new(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(48, 61, 83)), Foreground = Brushes.White, BorderThickness = new(0) };
        button.Click += (_, _) => action(); return button;
    }

    private void RefreshItems() => applications.ItemsSource = settings.Items.Select(x => x.Name).ToArray();
    private void AddApplication()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "应用与快捷方式|*.exe;*.lnk;*.url|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) settings.Items.Add(new(System.IO.Path.GetFileNameWithoutExtension(file), file));
        SaveItems();
    }
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) { settings.Items.Add(new(System.IO.Path.GetFileName(dialog.FolderName), dialog.FolderName)); SaveItems(); }
    }
    private void RemoveSelected()
    {
        if (applications.SelectedIndex >= 0) { settings.Items.RemoveAt(applications.SelectedIndex); SaveItems(); }
    }
    private void SaveItems() { settings.Normalize(); RefreshItems(); reload(); status.Text = "入口已保存。"; }
}
