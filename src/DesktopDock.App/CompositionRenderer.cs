using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using DesktopDock.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using WColor = Windows.UI.Color;

namespace DesktopDock.App;

internal sealed class CompositionRenderer : IDisposable
{
    private readonly Compositor compositor;
    private readonly DesktopWindowTarget target;
    private readonly ContainerVisual root;
    private readonly ContainerVisual row;
    private readonly ContainerVisual reflectionRow;
    private readonly ContainerVisual indicatorRow;
    private readonly ID3D11Device d3d;
    private readonly ID2D1Device d2d;
    private readonly CompositionGraphicsDevice graphics;
    private readonly List<IconVisual> icons = [];
    private readonly SpriteVisual insertion;
    private readonly ShapeVisual placeholder;
    private readonly CompositionRoundedRectangleGeometry placeholderGeometry;
    private bool disposed;
    private bool reducedMotion;
    private readonly Stopwatch feedbackTime = Stopwatch.StartNew();

    public bool HardwareRendering { get; }
    public bool HostBackdrop => false;
    public int IconCount => icons.Count;
    public bool OrderingActive { get; private set; }
    public bool PressFeedbackActive { get; private set; }
    public bool StateAnimationActive { get; private set; }
    internal IReadOnlyList<object> IconIdentities => icons.Select(icon => (object)icon.Image).ToArray();
    internal float ImageSizeForChecks(int index) => icons[index].Image.Scale.X * IconService.Resolution;
    internal (DockAppState State, bool Visible, float Width, WColor Color) StateForChecks(int index)
    {
        var icon = icons[index];
        return (icon.State, icon.Indicator.Visual.IsVisible, icon.Indicator.Geometry.Size.X, icon.Indicator.Fill.Color);
    }

    private sealed class StateAdornment : IDisposable
    {
        public ShapeVisual Visual { get; }
        public CompositionRoundedRectangleGeometry Geometry { get; }
        public CompositionColorBrush Fill { get; }
        private readonly CompositionColorBrush stroke;
        private readonly CompositionSpriteShape shape;
        public StateAdornment(Compositor compositor, WColor fill, WColor border, float thickness)
        {
            Visual = compositor.CreateShapeVisual();
            Geometry = compositor.CreateRoundedRectangleGeometry();
            Fill = compositor.CreateColorBrush(fill);
            stroke = compositor.CreateColorBrush(border);
            shape = compositor.CreateSpriteShape(Geometry);
            shape.FillBrush = Fill; shape.StrokeBrush = stroke; shape.StrokeThickness = thickness;
            Visual.Shapes.Add(shape);
            Visual.IsVisible = false;
        }
        public void SetBounds(Vector3 offset, Vector2 size, float radius)
        {
            Visual.Offset = offset; Visual.Size = size + new Vector2(2);
            Geometry.Offset = new Vector2(1); Geometry.Size = size; Geometry.CornerRadius = new(radius);
        }
        public void Dispose() { Visual.Dispose(); shape.Dispose(); Geometry.Dispose(); Fill.Dispose(); stroke.Dispose(); }
    }

    private sealed class IconVisual(SpriteVisual image, SpriteVisual reflection,
        CompositionDrawingSurface imageSurface, CompositionDrawingSurface reflectionSurface,
        StateAdornment indicator) : IDisposable
    {
        public SpriteVisual Image { get; } = image;
        public SpriteVisual Reflection { get; } = reflection;
        public SpringMotion X { get; } = new();
        public TimedTransition Press { get; } = new();
        public StateAdornment Indicator { get; } = indicator;
        public TimedTransition Running { get; } = new();
        public TimedTransition Active { get; } = new();
        public DockAppState State { get; set; }
        public void Dispose()
        {
            Image.Brush?.Dispose(); Reflection.Brush?.Dispose();
            Image.Dispose(); Reflection.Dispose(); imageSurface.Dispose(); reflectionSurface.Dispose();
            Indicator.Dispose();
        }
    }

    public CompositionRenderer(nint hwnd)
    {
        compositor = new();
        target = CompositionInterop.CreateTarget(compositor, hwnd);
        root = compositor.CreateContainerVisual();
        row = compositor.CreateContainerVisual();
        reflectionRow = compositor.CreateContainerVisual();
        indicatorRow = compositor.CreateContainerVisual();
        target.Root = root;
        try { d3d = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport); HardwareRendering = true; }
        catch { d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport); }
        using (var dxgi = d3d.QueryInterface<IDXGIDevice>()) d2d = D2D1.D2D1CreateDevice(dxgi, null);
        graphics = CompositionInterop.CreateGraphics(compositor, d2d.NativePointer);
        insertion = compositor.CreateSpriteVisual();
        insertion.Brush = compositor.CreateColorBrush(WColor.FromArgb(245, 110, 180, 255));
        placeholder = compositor.CreateShapeVisual();
        placeholderGeometry = compositor.CreateRoundedRectangleGeometry();
        var outline = compositor.CreateSpriteShape(placeholderGeometry);
        outline.StrokeThickness = 1.5f;
        outline.StrokeBrush = compositor.CreateColorBrush(WColor.FromArgb(160, 200, 222, 250));
        outline.FillBrush = compositor.CreateColorBrush(WColor.FromArgb(28, 225, 238, 255));
        placeholder.Shapes.Add(outline);
        root.Children.InsertAtTop(reflectionRow);
        root.Children.InsertAtTop(placeholder);
        root.Children.InsertAtTop(row);
        root.Children.InsertAtTop(indicatorRow);
        root.Children.InsertAtTop(insertion);
    }

    public void SetTheme(DockSettings settings)
    {
        // The desktop itself is the background: no enclosing panel or border.
        reflectionRow.IsVisible = settings.Reflections;
        reducedMotion = settings.ReducedMotion;
        if (reducedMotion)
        {
            ResetPressFeedback();
            foreach (var icon in icons) { icon.Running.Reset(icon.State.Running ? 1 : 0); icon.Active.Reset(icon.State.Active ? 1 : 0); }
            StateAnimationActive = false;
        }
    }

    public bool SetAppStates(IReadOnlyList<DockAppState> states)
    {
        bool changed = false;
        double now = feedbackTime.Elapsed.TotalSeconds;
        for (int i = 0; i < icons.Count; i++)
        {
            var state = i < states.Count ? states[i] : default;
            state = state with { Active = state.Running && state.Active };
            var icon = icons[i];
            if (icon.State == state) continue;
            changed = true; icon.State = state;
            if (reducedMotion) { icon.Running.Reset(state.Running ? 1 : 0); icon.Active.Reset(state.Active ? 1 : 0); }
            else { icon.Running.Begin(state.Running ? 1 : 0, now, .14); icon.Active.Begin(state.Active ? 1 : 0, now, .14); }
        }
        return changed;
    }

    public void SetPressed(int index, bool down)
    {
        if (index < 0 || index >= icons.Count) return;
        if (reducedMotion) icons[index].Press.Reset(0);
        else icons[index].Press.Begin(down ? 1 : 0, feedbackTime.Elapsed.TotalSeconds, down ? .07 : .14);
    }

    public void ReleasePressFeedback()
    {
        for (int i = 0; i < icons.Count; i++) SetPressed(i, false);
    }

    public void ResetPressFeedback()
    {
        foreach (var icon in icons) icon.Press.Reset(0);
        PressFeedbackActive = false;
    }

    private void ClearIcons()
    {
        row.Children.RemoveAll();
        reflectionRow.Children.RemoveAll();
        indicatorRow.Children.RemoveAll();
        foreach (var icon in icons) icon.Dispose();
        icons.Clear();
    }

    private unsafe (SpriteVisual Image, CompositionDrawingSurface Surface) CreateImage(byte[] pixels)
    {
        int resolution = IconService.Resolution;
        var surface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(resolution, resolution),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        SpriteVisual? icon = null;
        try
        {
        CompositionInterop.Draw(surface, (pointer, offset) =>
        {
            Marshal.AddRef(pointer);
            using var context = new ID2D1DeviceContext(pointer);
            context.Clear(new Color4(0, 0, 0, 0));
            context.SetDpi(96, 96);
            fixed (byte* data = pixels)
            {
                using var bitmap = context.CreateBitmap(new SizeI(resolution, resolution), (nint)data,
                    (uint)(resolution * 4), new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                context.DrawBitmap(bitmap, new Vortice.RawRectF(offset.X, offset.Y, offset.X + resolution, offset.Y + resolution),
                    1, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            }
        });
        icon = compositor.CreateSpriteVisual();
        icon.Brush = compositor.CreateSurfaceBrush(surface);
        icon.Size = new Vector2(resolution);
        return (icon, surface);
        }
        catch { icon?.Brush?.Dispose(); icon?.Dispose(); surface.Dispose(); throw; }
    }

    private IconVisual CreateIcon(byte[] pixels)
    {
        var icon = CreateImage(pixels);
        (SpriteVisual Image, CompositionDrawingSurface Surface)? reflection = null;
        StateAdornment? indicator = null;
        try
        {
            reflection = CreateImage(IconReflection.Create(pixels, IconService.Resolution));
            indicator = new(compositor, WColor.FromArgb(245, 238, 243, 251), WColor.FromArgb(150, 25, 35, 48), .65f);
            return new(icon.Image, reflection.Value.Image, icon.Surface, reflection.Value.Surface, indicator);
        }
        catch
        {
            icon.Image.Brush?.Dispose(); icon.Image.Dispose(); icon.Surface.Dispose();
            if (reflection is { } mirror) { mirror.Image.Brush?.Dispose(); mirror.Image.Dispose(); mirror.Surface.Dispose(); }
            indicator?.Dispose(); throw;
        }
    }

    public void SynchronizeIcons(IReadOnlyList<DockItem> previous, IReadOnlyList<DockItem> desired,
        byte[][] pixels, DockGeometry geometry, float width, float height, float scale)
    {
        var available = new Dictionary<string, Queue<IconVisual>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min(previous.Count, icons.Count); i++)
        {
            if (!available.TryGetValue(previous[i].Target, out var queue)) available[previous[i].Target] = queue = new();
            queue.Enqueue(icons[i]);
        }
        var next = new List<IconVisual>();
        var created = new List<IconVisual>();
        try
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (available.TryGetValue(desired[i].Target, out var queue) && queue.Count > 0) next.Add(queue.Dequeue());
                else { var icon = CreateIcon(pixels[i]); created.Add(icon); next.Add(icon); }
            }
        }
        catch { foreach (var icon in created) icon.Dispose(); throw; }

        // Keep the existing tree attached while new surfaces are uploaded. Lay
        // out every new visual before attaching it, and remove obsolete ones last.
        var old = icons.ToArray();
        icons.Clear(); icons.AddRange(next);
        Layout(geometry, width, height, scale);
        foreach (var icon in created)
        {
            row.Children.InsertAtTop(icon.Image);
            reflectionRow.Children.InsertAtTop(icon.Reflection);
            indicatorRow.Children.InsertAtTop(icon.Indicator.Visual);
        }
        var retained = next.ToHashSet();
        foreach (var icon in old.Where(icon => !retained.Contains(icon)))
        {
            DetachIcon(icon);
        }
    }

    public void MoveIcon(int from, int to)
    {
        var icon = icons[from]; icons.RemoveAt(from); icons.Insert(to, icon);
    }

    public void RemoveIcon(int index)
    {
        var icon = icons[index]; icons.RemoveAt(index);
        DetachIcon(icon);
    }

    private void DetachIcon(IconVisual icon)
    {
        row.Children.Remove(icon.Image); reflectionRow.Children.Remove(icon.Reflection);
        indicatorRow.Children.Remove(icon.Indicator.Visual); icon.Dispose();
    }

    public void Layout(DockGeometry geometry, float width, float height, float dpiScale,
        IReadOnlyList<int>? order = null, bool animateOrder = false, double dt = 0, int hidden = -1, int insertBefore = -1)
    {
        root.Size = new(width, height);
        row.Size = root.Size;
        reflectionRow.Size = root.Size;
        indicatorRow.Size = root.Size;
        OrderingActive = false;
        PressFeedbackActive = false;
        StateAnimationActive = false;
        double now = feedbackTime.Elapsed.TotalSeconds;
        placeholder.IsVisible = hidden >= 0;
        insertion.IsVisible = insertBefore >= 0;
        if (insertBefore >= 0)
        {
            int index = Math.Min(insertBefore, geometry.Slots.Length - 1);
            float x = (float)(geometry.Slots[index].X - 4) * dpiScale;
            insertion.Size = new(3 * dpiScale, (float)(geometry.IconSize + 5) * dpiScale);
            insertion.Offset = new(x, (float)(geometry.Baseline(height / dpiScale) - geometry.IconSize - 3) * dpiScale, 0);
        }
        for (int positionIndex = 0; positionIndex < Math.Min(icons.Count, geometry.Slots.Length); positionIndex++)
        {
            int i = order is null ? positionIndex : order[positionIndex];
            var slot = geometry.Slots[positionIndex];
            float actualSize = (float)slot.Size * dpiScale;
            var entry = icons[i];
            float feedbackSize = actualSize * (1 - .06f * (float)entry.Press.Sample(now));
            PressFeedbackActive |= entry.Press.Active;
            var icon = entry.Image;
            if (!animateOrder) entry.X.Reset(slot.X);
            else if (dt > 0) OrderingActive |= entry.X.Step(slot.X, dt, 38);
            else OrderingActive |= Math.Abs(entry.X.Value - slot.X) > .01;
            icon.IsVisible = i != hidden;
            icon.Offset = new((float)entry.X.Value * dpiScale + (actualSize - feedbackSize) / 2,
                (float)geometry.Baseline(height / dpiScale) * dpiScale - feedbackSize, 0);
            icon.Scale = new(feedbackSize / IconService.Resolution, feedbackSize / IconService.Resolution, 1);
            var reflection = entry.Reflection;
            reflection.IsVisible = icon.IsVisible;
            reflection.Offset = new(icon.Offset.X, (float)geometry.Baseline(height / dpiScale) * dpiScale + 2 * dpiScale, 0);
            reflection.Scale = new(icon.Scale.X, icon.Scale.Y * (float)IconReflection.Depth, 1);
            float running = (float)entry.Running.Sample(now), active = (float)entry.Active.Sample(now);
            StateAnimationActive |= entry.Running.Active || entry.Active.Active;
            entry.Indicator.Visual.IsVisible = icon.IsVisible && running > .001f;
            entry.Indicator.Visual.Opacity = running;
            float markerWidth = (10f + 12f * active) * dpiScale;
            entry.Indicator.SetBounds(new((float)entry.X.Value * dpiScale + (actualSize - markerWidth) / 2 - 1,
                (float)geometry.Baseline(height / dpiScale) * dpiScale + 7 * dpiScale - 1, 0),
                new(markerWidth, 3.5f * dpiScale), 1.75f * dpiScale);
            entry.Indicator.Fill.Color = WColor.FromArgb(245, (byte)(255 - 178 * active), (byte)(255 - 85 * active), 255);
            if (i == hidden)
            {
                placeholder.Size = new(actualSize + 4 * dpiScale);
                placeholder.Offset = new((float)slot.X * dpiScale - 2 * dpiScale, (float)geometry.Baseline(height / dpiScale) * dpiScale - actualSize - 2 * dpiScale, 0);
                placeholderGeometry.Offset = new(1 * dpiScale);
                placeholderGeometry.Size = new(actualSize + 2 * dpiScale);
                placeholderGeometry.CornerRadius = new(10 * dpiScale);
            }
        }
    }

    internal IDisposable PresentationProbe(float x, float y, float size)
    {
        var probe = compositor.CreateSpriteVisual();
        probe.Brush = compositor.CreateColorBrush(WColor.FromArgb(255, 253, 0, 251));
        probe.Size = new(size); probe.Offset = new(x, y, 0);
        root.Children.InsertAtTop(probe);
        return new ProbeLease(() => { root.Children.Remove(probe); probe.Brush.Dispose(); probe.Dispose(); });
    }
    private sealed class ProbeLease(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        target.Root = null;
        ClearIcons();
        root.Dispose(); target.Dispose(); graphics.Dispose(); d2d.Dispose(); d3d.Dispose(); compositor.Dispose();
    }
}
