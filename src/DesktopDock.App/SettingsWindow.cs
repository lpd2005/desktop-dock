using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class SettingsWindow : Window
{
    private readonly DockSettings settings;
    private readonly Action apply, reload;
    private readonly ListBox applications = new();
    private readonly TextBlock status = FeatureUi.Text("设置自动保存", 11, FeatureUi.Muted);
    private readonly TextBlock pageTitle = FeatureUi.Text("", 26);
    private readonly TextBlock pageDescription = FeatureUi.Text("", 12, FeatureUi.Muted);
    private readonly ContentControl pageContent = new();
    private readonly List<(CheckBox Check, Func<bool> Value)> widgetChecks = [];
    private readonly List<(Button Button, FrameworkElement Page, string Title, string Description)> pages = [];
    private readonly Dictionary<string, ImageSource> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Image> previewIcons = [];
    private readonly Border storageNotice = new() { Visibility = Visibility.Collapsed, CornerRadius = new(12), Background = Brush("#FFF2D9"), Padding = new(13), Margin = new(0, 0, 0, 14) };
    private bool storagePending;
    private readonly int? systemPageIndex;
    private readonly int aboutPageIndex;
    private bool refreshingWidgets;
    private static readonly ControlTemplate sliderTemplate = CreateSliderTemplate();
    private static readonly ControlTemplate switchTemplate = CreateSwitchTemplate();
    private sealed record PinnedView(string Name, ImageSource Icon);

    public SettingsWindow(DockSettings settings, Action apply, Action reload, Action quit, TaskbarEnhancement taskbar, FrameworkElement? systemPage = null)
    {
        this.settings = settings; this.apply = apply; this.reload = reload;
        Title = "Desktop Dock · 设置"; Icon = AppBrand.Image(); ShowInTaskbar = false; WindowStyle = WindowStyle.ToolWindow;
        Width = 960; Height = 750; MinWidth = 760; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F5F7FB"); Foreground = FeatureUi.Ink; FontFamily = new("Segoe UI Variable Display, Microsoft YaHei UI");
        SourceInitialized += (_, _) =>
        {
            FeatureUi.ToolWindow(this);
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            double dpi = Native.GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96d;
            MinWidth = Math.Min(MinWidth, work.Width / dpi - 24); MinHeight = Math.Min(MinHeight, work.Height / dpi - 24);
            Width = Math.Min(Width, work.Width / dpi - 24); Height = Math.Min(Height, work.Height / dpi - 24);
        };
        var shell = new Grid(); shell.ColumnDefinitions.Add(new() { Width = new(205) }); shell.ColumnDefinitions.Add(new()); Content = shell;
        var sidebar = new DockPanel { Margin = new(17, 25, 17, 19) };
        var brand = new StackPanel { Margin = new(10, 0, 0, 27) };
        brand.Children.Add(new Image { Source = AppBrand.Image(), Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 11) });
        var name = FeatureUi.Text("Desktop Dock", 19); name.FontWeight = FontWeights.SemiBold; brand.Children.Add(name);
        brand.Children.Add(FeatureUi.Text("偏好设置", 11, FeatureUi.Muted)); DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var footer = new StackPanel(); footer.Children.Add(FeatureUi.Text(AppVersion.Short, 10, FeatureUi.Muted));
        status.Margin = new(0, 6, 0, 14); footer.Children.Add(status);
        var exit = FeatureUi.Button("退出并恢复桌面", quit); exit.FontSize = 11; exit.Background = Brushes.Transparent; exit.Foreground = FeatureUi.Muted; footer.Children.Add(exit);
        DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        var navigation = new StackPanel(); sidebar.Children.Add(navigation);
        shell.Children.Add(new Border { Background = Brush("#EDF1F7"), BorderBrush = Brush("#E1E7F0"), BorderThickness = new(0, 0, 1, 0), Child = sidebar });
        var main = new DockPanel { Margin = new(29, 28, 29, 20) }; Grid.SetColumn(main, 1); shell.Children.Add(main);
        var heading = new StackPanel { Margin = new(0, 0, 0, 21) }; pageTitle.FontWeight = FontWeights.SemiBold;
        pageDescription.Margin = new(0, 7, 0, 0); pageDescription.TextWrapping = TextWrapping.Wrap;
        heading.Children.Add(pageTitle); heading.Children.Add(pageDescription); DockPanel.SetDock(heading, Dock.Top); main.Children.Add(heading);
        DockPanel.SetDock(storageNotice, Dock.Top); main.Children.Add(storageNotice);
        main.Children.Add(new ScrollViewer { Content = pageContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        void Page(string title, string description, FrameworkElement page)
        {
            int index = pages.Count; var button = FeatureUi.Button(title, () => SelectPage(index));
            button.Padding = new(13, 11, 13, 11); button.Margin = new(0, 3, 0, 3); button.FontSize = 13;
            navigation.Children.Add(button); pages.Add((button, page, title, description));
        }

        var appearance = new StackPanel();
        appearance.Children.Add(BuildPreview());
        var layout = Section(appearance, "布局与动画");
        AddSlider(layout, "图标大小", "调整整排图标的基础尺寸", 32, 80, settings.IconSize, v => settings.IconSize = v);
        AddSlider(layout, "悬停放大", "鼠标经过时平滑放大图标", 1, 1.8, settings.Magnification, v => settings.Magnification = v, true);
        AddSlider(layout, "底部间距", "调整 Dock 与屏幕底边的距离", 0, 100, settings.BottomGap, v => settings.BottomGap = v);
        AddCheck(layout, "图标倒影", "保留图标下方的半高渐淡倒影", settings.Reflections, v => settings.Reflections = v);
        AddCheck(layout, "减少动画", "即时切换状态，减少视觉动效", settings.ReducedMotion, v => settings.ReducedMotion = v);
        var behavior = Section(appearance, "显示与桌面");
        AddCheck(behavior, "自动隐藏 Dock", "移到底边停留唤出，离开后收起", settings.AutoHide, v => settings.AutoHide = v);
        AddCheck(behavior, "窗口预览", "悬停已运行应用，预览并选择窗口", settings.WindowPreviews, v => settings.WindowPreviews = v);
        AddCheck(behavior, "隐藏桌面图标", "在桌面面板中访问，退出 Dock 时恢复", settings.HideDesktopIcons, v => settings.HideDesktopIcons = v);
        Page("外观与动画", "透明图标、轻盈倒影，让桌面保持清爽。", appearance);

        var widgets = new StackPanel();
        var widgetList = Section(widgets, "桌面小组件");
        widgetChecks.Add((AddCheck(widgetList, "时间", "显示时、分、秒与本地日期", settings.Widgets.Clock, v => settings.Widgets.Clock = v), () => settings.Widgets.Clock));
        widgetChecks.Add((AddCheck(widgetList, "天气", "查看当前天气与当日高低温", settings.Widgets.Weather, v => settings.Widgets.Weather = v), () => settings.Widgets.Weather));
        widgetChecks.Add((AddCheck(widgetList, "轻薄透明背景", "关闭后略微加深透明遮罩，便于阅读", settings.Widgets.Light, v => settings.Widgets.Light = v), () => settings.Widgets.Light));
        var citySection = Section(widgets, "天气城市");
        citySection.Children.Add(Hint("填写城市名称，组件会显示解析后的地区。不自动获取设备位置。"));
        var cityRow = new DockPanel { Margin = new(0, 12, 0, 5) };
        var city = new TextBox { Text = settings.Widgets.City, MaxLength = 80, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(11, 9, 11, 9), FontSize = 13 };
        void SaveCity() { settings.Widgets.City = city.Text.Trim(); apply(); city.Text = settings.Widgets.City; SetStatus("天气城市已保存"); }
        city.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveCity(); e.Handled = true; } };
        var citySave = FeatureUi.Button("应用城市", SaveCity, true); citySave.Margin = new(9, 0, 0, 0); DockPanel.SetDock(citySave, Dock.Right); cityRow.Children.Add(citySave);
        cityRow.Children.Add(new Border { CornerRadius = new(10), Background = Brush("#F0F4FA"), Child = city }); citySection.Children.Add(cityRow);
        widgets.Children.Add(Hint("拖动组件空白区域调整位置；悬停后点 × 可单独移除。天气由 Open-Meteo 提供，约每 30 分钟刷新，断网时保留上次真实数据。"));
        Page("桌面小组件", "透明时钟与天气，一眼可见，随手可放。", widgets);

        var taskbarPage = new StackPanel();
        taskbarPage.Children.Add(new Border { CornerRadius = new(14), Background = Brush("#E8F0FD"), Padding = new(16), Margin = new(0, 0, 0, 15), Child = Hint("先在 Windows 中将主屏任务栏设为顶部，再启用增强。软件不会改变任务栏位置。") });
        var taskbarOptions = Section(taskbarPage, "顶部任务栏");
        AddCheck(taskbarOptions, "启用任务栏增强", "退出时恢复原来的任务栏状态", settings.TaskbarEnhancement, v => settings.TaskbarEnhancement = v);
        AddCheck(taskbarOptions, "自动隐藏任务栏", "顶边停留唤出，移开后平滑收起", settings.TaskbarAutoHide, v => settings.TaskbarAutoHide = v);
        AddCheck(taskbarOptions, "玻璃效果", "使用 TranslucentTB 提供透明背景", settings.TaskbarGlass, v => settings.TaskbarGlass = v);
        AddCheck(taskbarOptions, "浅色玻璃", "关闭后使用深色任务栏玻璃", settings.LightTheme, v => settings.LightTheme = v);
        var health = Section(taskbarPage, "当前状态"); var taskbarStatus = Hint("");
        void UpdateTaskbarStatus() => taskbarStatus.Text = taskbar.Status + "\n" + taskbar.GlassStatus;
        UpdateTaskbarStatus(); taskbar.StatusChanged += UpdateTaskbarStatus; Closed += (_, _) => taskbar.StatusChanged -= UpdateTaskbarStatus; health.Children.Add(taskbarStatus);
        var taskbarActions = new WrapPanel { Margin = new(0, 12, 0, 0) };
        taskbarActions.Children.Add(FeatureUi.Button("临时唤出", taskbar.Reveal));
        taskbarActions.Children.Add(FeatureUi.Button("恢复原生任务栏", () => { settings.TaskbarEnhancement = false; apply(); Close(); })); health.Children.Add(taskbarActions);
        taskbarPage.Children.Add(Hint("开始菜单和托盘交互期间保持可用。玻璃组件：TranslucentTB 2026.2 · GPLv3；许可证与源码随运行包提供。"));
        Page("顶部任务栏", "保留原生操作习惯，调整隐藏与玻璃效果。", taskbarPage);

        var pinned = new StackPanel(); var pinnedSection = Section(pinned, "固定的应用与文件夹");
        applications.Height = 265; applications.Background = Brushes.Transparent; applications.BorderThickness = new(0); applications.Foreground = FeatureUi.Ink;
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.WidthProperty, 29d); image.SetValue(Image.HeightProperty, 29d); image.SetValue(Image.MarginProperty, new Thickness(0, 0, 12, 0)); image.SetBinding(Image.SourceProperty, new Binding("Icon")); row.AppendChild(image);
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); text.SetValue(TextBlock.FontSizeProperty, 13d); text.SetBinding(TextBlock.TextProperty, new Binding("Name")); row.AppendChild(text);
        applications.ItemTemplate = new DataTemplate { VisualTree = row };
        var itemStyle = new Style(typeof(ListBoxItem)); itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 8, 10, 8))); itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 2, 0, 2))); applications.ItemContainerStyle = itemStyle;
        pinnedSection.Children.Add(applications);
        var actions = new WrapPanel { Margin = new(0, 13, 0, 0) };
        actions.Children.Add(FeatureUi.Button("＋ 添加应用", AddApplication, true)); actions.Children.Add(FeatureUi.Button("＋ 文件夹", AddFolder)); actions.Children.Add(FeatureUi.Button("移除所选", RemoveSelected)); pinnedSection.Children.Add(actions);
        pinned.Children.Add(Hint("桌面快捷方式可直接拖入 Dock；在 Dock 中拖动排序，拖远松手仅移除入口。最左侧桌面面板和最右侧设置入口会始终保留。"));
        Page("固定应用", "管理常用入口，原文件始终保留在原处。", pinned);
        if (systemPage is not null)
        {
            systemPageIndex = pages.Count;
            Page("启动与更新", "登录后自动启动，及时获取新版本。", systemPage);
        }
        aboutPageIndex = pages.Count;
        Page("关于", "了解 Desktop Dock，获取帮助与项目信息。", new AboutPage(systemPage is null ? null : SelectSystemPage));
        RefreshItems(); SelectPage(0);
    }
    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static TextBlock Hint(string text) { var block = FeatureUi.Text(text, 11, FeatureUi.Muted); block.TextWrapping = TextWrapping.Wrap; block.LineHeight = 18; return block; }
    internal static StackPanel Section(Panel parent, string title)
    {
        var content = new StackPanel(); var heading = FeatureUi.Text(title, 14); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 0, 0, 13); content.Children.Add(heading);
        parent.Children.Add(new Border { Child = content, CornerRadius = new(16), Background = Brushes.White, BorderBrush = Brush("#E5EAF2"), BorderThickness = new(1), Padding = new(19, 17, 19, 14), Margin = new(0, 0, 0, 15) });
        return content;
    }
    private void SelectPage(int index)
    {
        pageTitle.Text = pages[index].Title; pageDescription.Text = pages[index].Description; pageContent.Content = pages[index].Page;
        for (int i = 0; i < pages.Count; i++) { pages[i].Button.Background = i == index ? Brush("#DCE9FB") : Brushes.Transparent; pages[i].Button.Foreground = i == index ? FeatureUi.Blue : FeatureUi.Ink; }
    }
    internal void SelectPageForChecks(int index) => SelectPage(index);
    internal void SelectSystemPage() { if (systemPageIndex is int index) SelectPage(index); }
    internal void SelectAboutPage() => SelectPage(aboutPageIndex);
    internal void SetStorageNotice(string? message, string action, Action retry)
    {
        storagePending = message is not null;
        storageNotice.Visibility = storagePending ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null)
        {
            var panel = new StackPanel(); panel.Children.Add(Hint(message));
            var button = FeatureUi.Button(action, retry); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new(0, 8, 0, 0);
            panel.Children.Add(button); storageNotice.Child = panel;
        }
        else storageNotice.Child = null;
        SetStatus("设置自动保存");
    }
    private void SetStatus(string text) => status.Text = storagePending ? "更改尚未保存" : text;
    internal static ControlTemplate SwitchTemplate => switchTemplate;
    private FrameworkElement BuildPreview()
    {
        var panel = new DockPanel(); var title = FeatureUi.Text("Dock 预览", 12); DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var dock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 14, 0, 13) };
        foreach (var (target, active) in new[] { ("dock:apps", -1), ("dock:settings", 1), ("ms-settings:", 0) })
        {
            var item = new StackPanel { Margin = new(13, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center };
            var icon = new Image { Source = IconService.Bitmap(IconService.Load(new("Preview", target))), Width = 38, Height = 38 }; previewIcons.Add(icon); item.Children.Add(icon);
            item.Children.Add(new Border { Width = active == 1 ? 22 : 10, Height = 3, CornerRadius = new(1.5), Margin = new(0, 6, 0, 0), Background = active == 1 ? FeatureUi.Blue : Brushes.White, Opacity = active < 0 ? 0 : 1 }); dock.Children.Add(item);
        }
        DockPanel.SetDock(dock, Dock.Top); panel.Children.Add(dock);
        var hint = FeatureUi.Text("已开启：白色短线     当前使用：蓝色长线", 10, FeatureUi.Ink); hint.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(hint);
        return new Border { Child = panel, CornerRadius = new(18), Padding = new(19, 15, 19, 15), Margin = new(0, 0, 0, 18), Background = new LinearGradientBrush(Color.FromRgb(199, 216, 237), Color.FromRgb(230, 238, 249), 25) };
    }
    private static Grid Row(Panel parent, string title, string description, FrameworkElement control)
    {
        var row = new Grid { Margin = new(0, 4, 0, 12) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = new StackPanel { Margin = new(0, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(FeatureUi.Text(title, 13)); var detail = Hint(description); detail.Margin = new(0, 3, 0, 0); label.Children.Add(detail); row.Children.Add(label);
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control); parent.Children.Add(row); return row;
    }
    private void AddSlider(Panel content, string title, string description, double min, double max, double value, Action<double> setter, bool factor = false)
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 135, VerticalAlignment = VerticalAlignment.Center, SmallChange = factor ? .05 : 1, IsMoveToPointEnabled = true };
        slider.Template = sliderTemplate;
        var caption = FeatureUi.Text(factor ? $"{value:0.00}×" : $"{value:0}", 11); caption.Width = 43; caption.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += (_, _) => { setter(slider.Value); caption.Text = factor ? $"{slider.Value:0.00}×" : $"{slider.Value:0}"; foreach (var icon in previewIcons) icon.Width = icon.Height = Math.Clamp(settings.IconSize * .72, 24, 48); apply(); SetStatus("设置已保存"); };
        controls.Children.Add(slider); controls.Children.Add(caption); Row(content, title, description, controls);
    }
    private CheckBox AddCheck(Panel content, string title, string description, bool value, Action<bool> setter)
    {
        var box = new CheckBox { IsChecked = value, Width = 38, Height = 22, Cursor = System.Windows.Input.Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        box.Template = switchTemplate;
        box.Checked += (_, _) => { if (!refreshingWidgets) { setter(true); apply(); SetStatus("设置已保存"); } };
        box.Unchecked += (_, _) => { if (!refreshingWidgets) { setter(false); apply(); SetStatus("设置已保存"); } };
        Row(content, title, description, box); return box;
    }
    private static ControlTemplate CreateSliderTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Slider}">
              <Grid Height="24">
                <Track x:Name="PART_Track" VerticalAlignment="Center" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="#3783E4" CornerRadius="2" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="#DCE3EE" CornerRadius="2" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                  <Track.Thumb><Thumb Width="15" Height="15"><Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="White" Stroke="#3783E4" StrokeThickness="2"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
            """);
    private static ControlTemplate CreateSwitchTemplate()
    {
        var track = new FrameworkElementFactory(typeof(Border)); track.Name = "Track"; track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11)); track.SetValue(Border.BackgroundProperty, Brush("#CCD5E3"));
        var thumb = new FrameworkElementFactory(typeof(Border)); thumb.Name = "Thumb"; thumb.SetValue(Border.WidthProperty, 16d); thumb.SetValue(Border.HeightProperty, 16d); thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); thumb.SetValue(Border.BackgroundProperty, Brushes.White); thumb.SetValue(Border.MarginProperty, new Thickness(3)); thumb.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left); track.AppendChild(thumb);
        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = track };
        var enabled = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true }; enabled.Setters.Add(new Setter(Border.BackgroundProperty, FeatureUi.Blue, "Track")); enabled.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb")); template.Triggers.Add(enabled);
        return template;
    }
    internal void RefreshWidgets()
    {
        refreshingWidgets = true; try { foreach (var (check, value) in widgetChecks) check.IsChecked = value(); } finally { refreshingWidgets = false; }
    }
    internal void RefreshItems()
    {
        applications.ItemsSource = settings.Items.Select(item =>
        {
            if (!iconCache.TryGetValue(item.Target, out var icon)) iconCache[item.Target] = icon = IconService.Bitmap(IconService.Load(item));
            return new PinnedView(item.Name, icon);
        }).ToArray();
    }
    private void AddApplication()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "应用与快捷方式|*.exe;*.lnk;*.url|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string file in dialog.FileNames) settings.Items.Add(new(System.IO.Path.GetFileNameWithoutExtension(file), file)); SaveItems();
    }
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog(); if (dialog.ShowDialog(this) == true) { settings.Items.Add(new(System.IO.Path.GetFileName(dialog.FolderName), dialog.FolderName)); SaveItems(); }
    }
    private void RemoveSelected() { if (applications.SelectedIndex >= 0) { settings.Items.RemoveAt(applications.SelectedIndex); SaveItems(); } }
    private void SaveItems() { settings.Normalize(); RefreshItems(); reload(); SetStatus("入口已保存"); }
}
