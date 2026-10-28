using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

// One cached bitmap, composed by DWM. No focus, Alt+Tab entry, or mouse surface.
internal sealed class BitmapOverlay : Forms.NativeWindow, IDisposable
{
    private nint dc, bitmap, previous, bits;
    private readonly int width, height;
    private byte opacity = 255;
    private Native.Point position;
    private bool shown;
    private bool pixelsChanged;
    public BitmapOverlay(byte[] pixels, int width, int height)
    {
        this.width = width; this.height = height;
        CreateHandle(new Forms.CreateParams { Caption = "Desktop Dock · animation", Style = unchecked((int)0x80000000),
            ExStyle = 0x80000 | 0x20 | 0x80 | 0x08000000 | 8, Width = width, Height = height });
        dc = CreateCompatibleDC(0);
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        bitmap = CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
        if (dc == 0 || bitmap == 0) { Dispose(); throw new System.ComponentModel.Win32Exception(); }
        Marshal.Copy(pixels, 0, bits, checked(width * height * 4));
        previous = SelectObject(dc, bitmap);
    }
    public void Place(int x, int y, byte alpha = 255)
    {
        position = new() { X = x, Y = y };
        if (!shown || alpha != opacity || pixelsChanged)
        {
            opacity = alpha;
            var size = new Native.Point { X = width, Y = height };
            var origin = new Native.Point();
            var blend = new Blend { Alpha = alpha, Format = 1 };
            if (!UpdateLayeredWindow(Handle, 0, ref position, ref size, dc, ref origin, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception();
            pixelsChanged = false;
        }
        else Native.SetWindowPos(Handle, 0, x, y, 0, 0, 0x0015 | 0x0200);
        if (!shown) { shown = true; Native.ShowWindow(Handle, 8); }
    }
    public void SetPixels(byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("Bitmap dimensions do not match.", nameof(pixels));
        Marshal.Copy(pixels, 0, bits, pixels.Length); pixelsChanged = true;
    }
    public static (byte[] Pixels, int Side, int Margin) Icon(byte[] pixels, int resolution, int size, bool removing = false)
    {
        int margin = Math.Max(12, size / 4), side = size + margin * 2;
        using var source = new Bitmap(resolution, resolution, PixelFormat.Format32bppPArgb);
        var sourceData = source.LockBits(new Rectangle(0, 0, resolution, resolution), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(pixels, 0, sourceData.Scan0, pixels.Length); }
        finally { source.UnlockBits(sourceData); }
        using var result = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
        using (var drawing = Graphics.FromImage(result))
        {
            drawing.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            path.AddEllipse(margin - 3, margin + size * .66f, size + 6, size * .45f);
            using var shadow = new PathGradientBrush(path) { CenterColor = Color.FromArgb(75, 0, 0, 0), SurroundColors = [Color.Transparent] };
            drawing.FillPath(shadow, path);
            drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
            drawing.DrawImage(source, margin, margin - 3, size, size);
            if (removing)
            {
                drawing.SmoothingMode = SmoothingMode.AntiAlias;
                float r = Math.Max(9, size * .23f), cx = margin + size - 3, cy = margin + size - 3;
                using var red = new SolidBrush(Color.FromArgb(255, 240, 62, 76));
                using var rim = new Pen(Color.FromArgb(225, 255, 255, 255), Math.Max(1, size / 48f));
                drawing.FillEllipse(red, cx - r, cy - r, 2 * r, 2 * r); drawing.DrawEllipse(rim, cx - r, cy - r, 2 * r, 2 * r);
                using var pen = new Pen(Color.White, Math.Max(1.3f, size / 32f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                drawing.DrawLine(pen, cx - r * .48f, cy - r * .35f, cx + r * .48f, cy - r * .35f);
                drawing.DrawLines(pen, new PointF[] { new(cx - r * .18f, cy - r * .38f), new(cx - r * .18f, cy - r * .56f), new(cx + r * .18f, cy - r * .56f), new(cx + r * .18f, cy - r * .38f) });
                drawing.DrawLines(pen, new PointF[] { new(cx - r * .36f, cy - r * .12f), new(cx - r * .29f, cy + r * .48f), new(cx + r * .29f, cy + r * .48f), new(cx + r * .36f, cy - r * .12f) });
                drawing.DrawLine(pen, cx - r * .12f, cy, cx - r * .1f, cy + r * .26f);
                drawing.DrawLine(pen, cx + r * .12f, cy, cx + r * .1f, cy + r * .26f);
            }
        }
        var data = result.LockBits(new Rectangle(0, 0, side, side), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        byte[] output = new byte[side * side * 4];
        try { Marshal.Copy(data.Scan0, output, 0, output.Length); }
        finally { result.UnlockBits(data); }
        return (output, side, margin);
    }
    public static byte[]? CaptureTaskbar(TaskbarSnapshot snapshot)
    {
        int width = snapshot.Frame.Right - snapshot.Frame.Left, height = snapshot.Frame.Bottom - snapshot.Frame.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 4_000_000) return null;
        foreach (int fraction in new[] { 1, 2, 3 })
        {
            var point = new Native.Point { X = snapshot.Frame.Left + width * fraction / 4, Y = snapshot.Frame.Top + height / 2 };
            if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != (nint)snapshot.Handle) return null;
        }
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), image = 0, old = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            image = CreateDIBSection(memory, ref info, 0, out nint bits, 0, 0);
            if (image == 0) return null;
            old = SelectObject(memory, image);
            if (!BitBlt(memory, 0, 0, width, height, screen, snapshot.Frame.Left, snapshot.Frame.Top, 0x00CC0020 | 0x40000000)) return null;
            var output = new byte[width * height * 4]; Marshal.Copy(bits, output, 0, output.Length);
            for (int i = 3; i < output.Length; i += 4) output[i] = 255;
            return output;
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            if (image != 0) Native.DeleteObject(image);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    internal static uint ReadScreenPixel(int x, int y)
    {
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = 40, Width = 1, Height = -1, Planes = 1, BitCount = 32 };
        nint image = CreateDIBSection(memory, ref info, 0, out nint bits, 0, 0);
        nint old = SelectObject(memory, image);
        try
        {
            if (image == 0 || !BitBlt(memory, 0, 0, 1, 1, screen, x, y, 0x00CC0020 | 0x40000000)) return 0;
            return (uint)Marshal.ReadInt32(bits) & 0x00ffffff;
        }
        finally
        {
            SelectObject(memory, old); if (image != 0) Native.DeleteObject(image);
            DeleteDC(memory); ReleaseDC(0, screen);
        }
    }
    public void Dispose()
    {
        if (dc != 0) { if (previous != 0) SelectObject(dc, previous); if (bitmap != 0) Native.DeleteObject(bitmap); DeleteDC(dc); dc = bitmap = previous = 0; }
        if (Handle != 0) DestroyHandle();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint screen, ref Native.Point position,
        ref Native.Point size, nint memory, ref Native.Point origin, uint key, ref Blend blend, uint flags);
}
