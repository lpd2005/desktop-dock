using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class AboutPage : StackPanel
{
    private const string Project = "https://github.com/" + ReleaseFeed.Repository;
    private readonly TextBlock status = Hint("");

    internal AboutPage(Action? showUpdates, Action<string>? openLink = null)
    {
        var identity = new Grid(); identity.ColumnDefinitions.Add(new() { Width = new(102) }); identity.ColumnDefinitions.Add(new());
        identity.Children.Add(new Image { Source = AppBrand.Image(), Width = 84, Height = 84, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
        var introduction = new StackPanel(); Grid.SetColumn(introduction, 1); identity.Children.Add(introduction);
        var name = FeatureUi.Text("Desktop Dock", 25); name.FontWeight = FontWeights.SemiBold; introduction.Children.Add(name);
        var tagline = Hint("让 Windows 桌面简洁、有序。"); tagline.Margin = new(0, 5, 0, 13); introduction.Children.Add(tagline);
        var version = FeatureUi.Text("版本 " + AppVersion.Current + (AppVersion.Current.Contains('-') ? " · 测试版" : ""), 11, FeatureUi.Blue);
        introduction.Children.Add(new Border { Child = version, Padding = new(10, 5, 10, 5), CornerRadius = new(8), Background = new SolidColorBrush(Color.FromRgb(232, 240, 253)), HorizontalAlignment = HorizontalAlignment.Left });
        Children.Add(new Border { Child = identity, Background = Brushes.White, CornerRadius = new(16), Padding = new(22), Margin = new(0, 0, 0, 15) });

        var product = SettingsWindow.Section(this, "关于这个应用");
        product.Children.Add(Hint("面向 Windows 11 的桌面 Dock，提供透明图标、半高倒影、窗口切换与预览，以及时间和天气小组件。"));
        var platform = Hint("Windows 11 22H2 或更新版本 · x64"); platform.Margin = new(0, 9, 0, 13); product.Children.Add(platform);
        var updateActions = new WrapPanel();
        var update = FeatureUi.Button("检查更新", () => showUpdates?.Invoke(), true);
        update.IsEnabled = showUpdates is not null;
        if (showUpdates is null) update.ToolTip = "请在正常运行模式的“启动与更新”中检查版本。";
        updateActions.Children.Add(update);
        updateActions.Children.Add(Link("版本与下载", ReleaseFeed.Page)); product.Children.Add(updateActions);

        var resources = SettingsWindow.Section(this, "项目与帮助");
        var links = new WrapPanel(); resources.Children.Add(links);
        links.Children.Add(Link("项目主页", Project));
        links.Children.Add(Link("使用说明", Project + "/blob/main/docs/USER_GUIDE.md"));
        links.Children.Add(Link("反馈问题", Project + "/issues"));
        links.Children.Add(Link("更新记录", Project + "/blob/main/CHANGELOG.md"));

        var notices = SettingsWindow.Section(this, "隐私与致谢");
        notices.Children.Add(Hint("设置与图标缓存保存在本机，不上传桌面文件、应用列表或窗口画面。天气按填写的城市查询，版本检查使用公开的 GitHub 发布信息。"));
        var credit = Hint("感谢开源组件与数据服务：TranslucentTB 提供可选的任务栏玻璃效果，Open-Meteo 提供天气数据。"); credit.Margin = new(0, 9, 0, 10); notices.Children.Add(credit);
        var credits = new WrapPanel(); notices.Children.Add(credits);
        credits.Children.Add(Link("第三方许可", Project + "/tree/main/docs/licenses"));
        credits.Children.Add(Link("TranslucentTB", "https://github.com/TranslucentTB/TranslucentTB"));
        credits.Children.Add(Link("Open-Meteo", "https://open-meteo.com/"));
        status.Margin = new(0, 1, 0, 0); status.Visibility = Visibility.Collapsed; Children.Add(status);

        Button Link(string label, string address)
        {
            var button = FeatureUi.Button(label, () =>
            {
                try
                {
                    if (openLink is not null) openLink(address);
                    else using (Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })) { }
                    status.Visibility = Visibility.Collapsed;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                { status.Text = "暂时无法打开链接，请稍后重试。\n" + address; status.Visibility = Visibility.Visible; }
            });
            button.ToolTip = address; return button;
        }
    }

    private static TextBlock Hint(string text)
    {
        var label = FeatureUi.Text(text, 11, FeatureUi.Muted); label.TextWrapping = TextWrapping.Wrap; label.LineHeight = 18; return label;
    }
}
