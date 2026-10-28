using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DesktopDock.App;

internal static class FeatureUi
{
    public static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(38, 47, 64));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(125, 136, 153));
    public static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(55, 131, 228));
    private static readonly ControlTemplate buttonTemplate = CreateButtonTemplate();
    public static TextBlock Text(string text, double size = 13, Brush? color = null) => new()
    { Text = text, FontSize = size, Foreground = color ?? Ink, VerticalAlignment = VerticalAlignment.Center };

    public static Button Button(string text, Action action, bool prominent = false)
    {
        var button = new Button { Content = text, Padding = new(13, 8, 13, 8), Margin = new(3), Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12, Foreground = prominent ? Brushes.White : Ink,
            Background = prominent ? Blue : new SolidColorBrush(Color.FromArgb(105, 215, 225, 238)), BorderThickness = new(0) };
        button.Template = buttonTemplate;
        button.MouseEnter += (_, _) => button.Opacity = .78;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.Click += (_, _) => action(); return button;
    }
    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
    public static Border Card(UIElement content, double radius = 24) => new()
    {
        Child = content, CornerRadius = new(radius), Padding = new(20),
        Background = new SolidColorBrush(Color.FromArgb(242, 249, 251, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), BorderThickness = new(1),
        Effect = new DropShadowEffect { BlurRadius = 25, ShadowDepth = 6, Opacity = .15, Color = Color.FromRgb(31, 47, 72) }
    };
    public static void ToolWindow(Window window, bool noActivate = false)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        long style = Native.GetWindowLongPtr(handle, -20).ToInt64();
        Native.SetWindowLongPtr(handle, -20, new((style | 0x80L | (noActivate ? 0x08000000L : 0L)) & ~0x40000L));
    }
    public static void Fade(Window window, bool show, bool reduced, Action? finished = null)
    {
        double current = window.Opacity;
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = current;
        if (reduced) { window.Opacity = show ? 1 : 0; finished?.Invoke(); return; }
        var animation = new DoubleAnimation(current, show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 170 : 110))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { window.Opacity = show ? 1 : 0; finished?.Invoke(); };
        window.BeginAnimation(UIElement.OpacityProperty, animation);
    }
}
