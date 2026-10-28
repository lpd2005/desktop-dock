using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DesktopDock.App;

// Per-pixel alpha exposes the real background without a blur/backdrop effect.
internal static class TransparentUi
{
    public static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(247, 250, 255));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(220, 230, 241));
    private static readonly DropShadowEffect labelShadow = CreateLabelShadow();
    private static DropShadowEffect CreateLabelShadow()
    {
        var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = .85 };
        shadow.Freeze(); return shadow;
    }
    public static TextBlock Text(string text, double size = 13, Brush? color = null)
    {
        var label = FeatureUi.Text(text, size, color ?? Ink); label.Effect = labelShadow; return label;
    }
    public static Brush Tint(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
    public static void Install(Window window)
    {
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
    }
    public static Border Card(UIElement content, double radius = 24, bool clear = true) => new()
    {
        Child = content, CornerRadius = new(radius), Padding = new(22),
        Background = new SolidColorBrush(Color.FromArgb(clear ? (byte)64 : (byte)104, 10, 15, 23)),
        BorderThickness = new(0)
    };
    public static Button Button(string text, Action action, bool selected = false)
    {
        var button = FeatureUi.Button(text, action);
        button.Foreground = Ink; button.Background = Tint(selected ? (byte)48 : (byte)15);
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
                <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border CornerRadius="3" Background="#55FFFFFF" Margin="1,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
              </Track>
            </ControlTemplate>
            """)));
        return style;
    }
}
