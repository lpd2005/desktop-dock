using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopDock.App;

// Native acrylic supplies the live backdrop; these cached gradients only add
// tint and edge lighting. No wallpaper capture or per-frame blur is needed.
internal static class LiquidGlass
{
    public static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(247, 250, 255));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(191, 205, 225));
    public static Brush Tint(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 125, 157, 201));
    public static void Install(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            int dark = 1, rounded = 2, backdrop = 3, border = -2;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            bool applied = Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0
                && DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
            if (applied && HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            // Windows may decline the material in remote sessions or when
            // transparency is disabled. Keep the content legible there too.
            window.Background = applied ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(30, 43, 64));
        };
    }
    public static Border Card(UIElement content, double radius = 24, bool clear = true) => new()
    {
        Child = content, CornerRadius = new(radius), Padding = new(22),
        Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(clear ? (byte)148 : (byte)205, 45, 64, 92), 0),
            new(Color.FromArgb(clear ? (byte)112 : (byte)180, 15, 28, 48), .45),
            new(Color.FromArgb(clear ? (byte)138 : (byte)205, 30, 51, 78), 1)
        }, new Point(0, 0), new Point(.8, 1)),
        BorderBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(145, 244, 250, 255), 0), new(Color.FromArgb(28, 229, 240, 255), .45),
            new(Color.FromArgb(78, 200, 222, 251), 1)
        }, new Point(0, 0), new Point(.75, 1)), BorderThickness = new(1)
    };
    public static Button Button(string text, Action action, bool selected = false)
    {
        var button = FeatureUi.Button(text, action);
        button.Foreground = Ink; button.Background = Tint(selected ? (byte)92 : (byte)28);
        return button;
    }
    public static void Scale(ScaleTransform transform, double to, bool reduced)
    {
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            double current = (double)transform.GetValue(property);
            transform.BeginAnimation(property, null); transform.SetValue(property, to);
            if (!reduced) transform.BeginAnimation(property, new DoubleAnimation(current, to, TimeSpan.FromMilliseconds(to > 1 ? 170 : 130))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }
    public static Style ScrollBarStyle()
    {
        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 6d));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ScrollBar">
              <Track x:Name="PART_Track" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" IsDirectionReversed="True" Orientation="Vertical">
                <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border CornerRadius="3" Background="#527D9DC9" Margin="1,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
              </Track>
            </ControlTemplate>
            """)));
        return style;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
