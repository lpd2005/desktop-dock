using System.Numerics;
using Windows.UI.Composition;
using DesktopDock.Core;
using WColor = Windows.UI.Color;

namespace DesktopDock.App;

internal sealed class SpectrumVisual : IDisposable
{
    private readonly ContainerVisual root;
    private readonly ShapeVisual[] bars = new ShapeVisual[SpectrumAnalyzer.Bands];
    private readonly CompositionRoundedRectangleGeometry[] geometry = new CompositionRoundedRectangleGeometry[SpectrumAnalyzer.Bands];
    private readonly CompositionSpriteShape[] shapes = new CompositionSpriteShape[SpectrumAnalyzer.Bands];
    private readonly CompositionColorBrush white;
    private readonly Vector3KeyFrameAnimation heightAnimation;
    private SpectrumLayout layout;
    internal bool Visible => root.IsVisible;
    internal int Count => layout.Count;
    internal SpectrumLayout LayoutForChecks => layout;

    public SpectrumVisual(Compositor compositor, ContainerVisual parent)
    {
        root = compositor.CreateContainerVisual(); root.IsVisible = false;
        white = compositor.CreateColorBrush(WColor.FromArgb(245, 255, 255, 255));
        heightAnimation = compositor.CreateVector3KeyFrameAnimation();
        heightAnimation.Duration = TimeSpan.FromMilliseconds(90);
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = compositor.CreateShapeVisual(); geometry[i] = compositor.CreateRoundedRectangleGeometry();
            shapes[i] = compositor.CreateSpriteShape(geometry[i]); shapes[i].FillBrush = white;
            bars[i].Shapes.Add(shapes[i]); root.Children.InsertAtTop(bars[i]);
        }
        parent.Children.InsertAtTop(root);
    }

    public void Arrange(SpectrumLayout next, float scale)
    {
        layout = next;
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].IsVisible = i < layout.Count;
            bars[i].StopAnimation("Scale");
            if (i >= layout.Count) continue;
            float width = (float)Math.Min(4, layout.Width / layout.Count * .35) * scale;
            float maximum = (float)layout.MaximumHeight * scale;
            bars[i].Size = new(width, maximum);
            bars[i].Offset = new((float)(layout.Left + (i + .5) * layout.Width / layout.Count) * scale - width / 2,
                (float)layout.Baseline * scale - maximum, 0);
            bars[i].CenterPoint = new(width / 2, maximum, 0);
            geometry[i].Size = new(width, maximum); geometry[i].CornerRadius = new(width / 2);
            bars[i].Scale = new(1, .04f, 1);
        }
    }

    public void Present(ReadOnlySpan<float> levels, float opacity, bool energySaver)
    {
        root.IsVisible = opacity > .005f && layout.MaximumHeight > 0;
        root.Opacity = opacity;
        if (!root.IsVisible) return;
        heightAnimation.Duration = TimeSpan.FromMilliseconds(energySaver ? 120 : 75);
        for (int i = 0; i < layout.Count; i++)
        {
            // Aggregate every source band when the Dock is narrow; sampling isolated bands loses tones.
            float level = SpectrumLayout.LevelForBar(levels, layout.Count, i);
            heightAnimation.InsertKeyFrame(1, new Vector3(1, .04f + .96f * level, 1));
            bars[i].StartAnimation("Scale", heightAnimation);
        }
    }

    public void Hide()
    {
        root.IsVisible = false;
        foreach (var bar in bars) bar.StopAnimation("Scale");
    }
    public void Dispose()
    {
        Hide(); root.Children.RemoveAll();
        for (int i = 0; i < bars.Length; i++) { shapes[i].Dispose(); geometry[i].Dispose(); bars[i].Dispose(); }
        heightAnimation.Dispose(); white.Dispose(); root.Dispose();
    }
}
