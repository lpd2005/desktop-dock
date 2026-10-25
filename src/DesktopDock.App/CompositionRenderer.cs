using System.Numerics;
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
    private readonly SpriteVisual glass;
    private readonly SpriteVisual tint;
    private readonly CompositionRoundedRectangleGeometry pill;
    private readonly CompositionRoundedRectangleGeometry tintPill;
    private readonly ID3D11Device d3d;
    private readonly ID2D1Device d2d;
    private readonly CompositionGraphicsDevice graphics;
    private readonly List<SpriteVisual> icons = [];
    private readonly List<CompositionDrawingSurface> surfaces = [];
    private readonly ShapeVisual rim;
    private readonly CompositionRoundedRectangleGeometry rimGeometry;
    private readonly CompositionSpriteShape rimStroke;
    private readonly SpriteVisual separator;
    private bool disposed;

    public bool HardwareRendering { get; }
    public bool HostBackdrop { get; private set; }
    public int IconCount => icons.Count;

    public CompositionRenderer(nint hwnd)
    {
        compositor = new();
        target = CompositionInterop.CreateTarget(compositor, hwnd);
        root = compositor.CreateContainerVisual();
        row = compositor.CreateContainerVisual();
        target.Root = root;
        try { d3d = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport); HardwareRendering = true; }
        catch { d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport); }
        using (var dxgi = d3d.QueryInterface<IDXGIDevice>()) d2d = D2D1.D2D1CreateDevice(dxgi, null);
        graphics = CompositionInterop.CreateGraphics(compositor, d2d.NativePointer);
        glass = compositor.CreateSpriteVisual();
        pill = compositor.CreateRoundedRectangleGeometry();
        glass.Clip = compositor.CreateGeometricClip(pill);
        tint = compositor.CreateSpriteVisual();
        tintPill = compositor.CreateRoundedRectangleGeometry();
        tint.Clip = compositor.CreateGeometricClip(tintPill);
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 14;
        shadow.Opacity = .18f;
        shadow.Offset = new Vector3(0, 3, 0);
        glass.Shadow = shadow;
        rim = compositor.CreateShapeVisual();
        rimGeometry = compositor.CreateRoundedRectangleGeometry();
        rimStroke = compositor.CreateSpriteShape(rimGeometry);
        rimStroke.StrokeThickness = 1;
        rim.Shapes.Add(rimStroke);
        separator = compositor.CreateSpriteVisual();
        root.Children.InsertAtTop(glass);
        root.Children.InsertAtTop(tint);
        root.Children.InsertAtTop(rim);
        root.Children.InsertAtTop(separator);
        root.Children.InsertAtTop(row);
    }

    public void SetTheme(DockSettings settings)
    {
        HostBackdrop = false;
        var oldGlass = glass.Brush;
        if (settings.Blur && HardwareRendering)
        {
            try { glass.Brush = compositor.CreateHostBackdropBrush(); HostBackdrop = true; }
            catch { glass.Brush = compositor.CreateColorBrush(settings.LightTheme ? WColor.FromArgb(225, 224, 230, 239) : WColor.FromArgb(225, 35, 40, 49)); }
        }
        else glass.Brush = compositor.CreateColorBrush(settings.LightTheme ? WColor.FromArgb(240, 224, 230, 239) : WColor.FromArgb(240, 35, 40, 49));
        oldGlass?.Dispose();
        var gradient = compositor.CreateLinearGradientBrush();
        gradient.StartPoint = Vector2.Zero; gradient.EndPoint = Vector2.UnitY;
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, settings.LightTheme ? WColor.FromArgb(146, 255, 255, 255) : WColor.FromArgb(116, 87, 94, 110)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, settings.LightTheme ? WColor.FromArgb(98, 215, 226, 244) : WColor.FromArgb(132, 27, 33, 46)));
        var oldTint = tint.Brush; tint.Brush = gradient; oldTint?.Dispose();
        var oldStroke = rimStroke.StrokeBrush;
        rimStroke.StrokeBrush = compositor.CreateColorBrush(settings.LightTheme ? WColor.FromArgb(190, 255, 255, 255) : WColor.FromArgb(85, 225, 235, 255));
        oldStroke?.Dispose();
        var oldSeparator = separator.Brush;
        separator.Brush = compositor.CreateColorBrush(settings.LightTheme ? WColor.FromArgb(65, 32, 46, 65) : WColor.FromArgb(65, 235, 241, 250));
        oldSeparator?.Dispose();
    }

    public void ClearIcons()
    {
        row.Children.RemoveAll();
        foreach (var icon in icons) { icon.Brush?.Dispose(); icon.Dispose(); }
        foreach (var surface in surfaces) surface.Dispose();
        icons.Clear(); surfaces.Clear();
    }

    public unsafe void AddIcon(byte[] pixels)
    {
        int resolution = IconService.Resolution;
        var surface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(resolution, resolution),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
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
        surfaces.Add(surface);
        var icon = compositor.CreateSpriteVisual();
        icon.Brush = compositor.CreateSurfaceBrush(surface);
        icon.Size = new Vector2(resolution);
        row.Children.InsertAtTop(icon);
        icons.Add(icon);
    }

    public void Layout(DockGeometry geometry, float width, float height, float dpiScale)
    {
        root.Size = new(width, height);
        row.Size = root.Size;
        var size = new Vector2((float)geometry.Width * dpiScale, (float)(geometry.IconSize + 22) * dpiScale);
        var position = new Vector3((width - size.X) / 2, (float)geometry.BodyTop(height / dpiScale) * dpiScale, 0);
        pill.CornerRadius = tintPill.CornerRadius = new(20 * dpiScale);
        pill.Size = tintPill.Size = size;
        glass.Size = tint.Size = size;
        glass.Offset = tint.Offset = position;
        rim.Size = size; rim.Offset = position;
        rimGeometry.Offset = new(.5f * dpiScale);
        rimGeometry.Size = size - new Vector2(dpiScale);
        rimGeometry.CornerRadius = new(20 * dpiScale);
        rimStroke.StrokeThickness = dpiScale;
        separator.IsVisible = geometry.SeparatorX >= 0;
        separator.Size = new(dpiScale, size.Y - 24 * dpiScale);
        separator.Offset = new((float)geometry.SeparatorX * dpiScale, position.Y + 12 * dpiScale, 0);
        for (int i = 0; i < Math.Min(icons.Count, geometry.Slots.Length); i++)
        {
            var slot = geometry.Slots[i];
            float actualSize = (float)slot.Size * dpiScale;
            var icon = icons[i];
            icon.Offset = new((float)slot.X * dpiScale, (float)(geometry.Baseline(height / dpiScale) - slot.Lift) * dpiScale - actualSize, 0);
            icon.Scale = new(actualSize / IconService.Resolution, actualSize / IconService.Resolution, 1);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        target.Root = null;
        ClearIcons();
        root.Dispose(); target.Dispose(); graphics.Dispose(); d2d.Dispose(); d3d.Dispose(); compositor.Dispose();
    }
}
