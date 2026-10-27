using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopDock.Core;
using Color = System.Windows.Media.Color;

namespace DesktopDock.App;

internal static class IconService
{
    public const int Resolution = 128;
    public static byte[] Load(DockItem item)
    {
        nint icon = 0;
        try
        {
            string target = Environment.ExpandEnvironmentVariables(item.Target);
            if (File.Exists(target) || Directory.Exists(target))
            {
                Native.SHGetFileInfo(target, 0, out var info, (uint)Marshal.SizeOf<Native.ShellFileInfo>(), 0x4000);
                Guid iid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
                if (Native.SHGetImageList(4, ref iid, out nint list) == 0)
                {
                    try { icon = GetListIcon(list, info.IconIndex); }
                    finally { Marshal.Release(list); }
                }
                if (icon == 0)
                {
                    Native.SHGetFileInfo(target, 0, out info, (uint)Marshal.SizeOf<Native.ShellFileInfo>(), 0x100);
                    icon = info.Icon;
                }
            }
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (icon != 0)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    dc.DrawImage(source, new Rect(0, 0, Resolution, Resolution));
                }
                else if (item.Target == "dock:settings") dc.DrawImage(AppBrand.Image(), new Rect(0, 0, Resolution, Resolution));
                else
                {
                    DrawFallback(dc, item.Target);
                }
            }
            var bitmap = new RenderTargetBitmap(Resolution, Resolution, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            return FitTransparentIcon(bitmap);
        }
        finally { if (icon != 0) Native.DestroyIcon(icon); }
    }

    private static byte[] FitTransparentIcon(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        byte[] bytes = new byte[width * height * 4]; converted.CopyPixels(bytes, width * 4, 0);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (bytes[(y * width + x) * 4 + 3] < 8) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX < minX || maxY < minY) return new byte[Resolution * Resolution * 4];
        int contentWidth = maxX - minX + 1, contentHeight = maxY - minY + 1;
        double sizeScale = (double)Resolution / Math.Max(contentWidth, contentHeight);
        double drawWidth = contentWidth * sizeScale, drawHeight = contentHeight * sizeScale;
        var crop = new CroppedBitmap(converted, new Int32Rect(minX, minY, contentWidth, contentHeight));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Keep the original silhouette and alpha, with the artwork resting
            // on the reflection plane. Do not add a plate or round off logos.
            dc.DrawImage(crop, new Rect((Resolution - drawWidth) / 2, Resolution - drawHeight, drawWidth, drawHeight));
        }
        var normalized = new RenderTargetBitmap(Resolution, Resolution, 96, 96, PixelFormats.Pbgra32);
        normalized.Render(visual);
        byte[] pixels = new byte[Resolution * Resolution * 4];
        normalized.CopyPixels(pixels, Resolution * 4, 0);
        return pixels;
    }

    private static void DrawFallback(DrawingContext dc, string target)
    {
        if (target == "dock:apps")
        {
            var fill = new LinearGradientBrush(Color.FromRgb(214, 235, 255), Color.FromRgb(104, 162, 239), 90);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(155, 255, 255, 255)), 1.2);
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
                dc.DrawRoundedRectangle(fill, pen, new Rect(9 + x * 39, 9 + y * 39, 30, 30), 8, 8);
            return;
        }
        bool preferences = target is "dock:settings" or "ms-settings:";
        bool bin = target == "shell:RecycleBinFolder";
        Color top = preferences ? Color.FromRgb(177, 193, 216) : bin ? Color.FromRgb(105, 211, 177) : Color.FromRgb(80, 183, 250);
        Color bottom = preferences ? Color.FromRgb(102, 122, 153) : bin ? Color.FromRgb(44, 155, 137) : Color.FromRgb(46, 111, 226);
        var text = new FormattedText(preferences ? "\uE713" : bin ? "\uE74D" : "\uE774", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe MDL2 Assets"), 104,
            new LinearGradientBrush(top, bottom, 90), 1);
        dc.DrawText(text, new System.Windows.Point((128 - text.Width) / 2, (124 - text.Height) / 2));
    }

    internal static BitmapSource Bitmap(byte[] pixels)
    {
        var bitmap = BitmapSource.Create(Resolution, Resolution, 96, 96, PixelFormats.Pbgra32, null, pixels, Resolution * 4);
        bitmap.Freeze(); return bitmap;
    }

    private static unsafe nint GetListIcon(nint list, int index)
    {
        nint icon = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)(*(nint**)list)[10];
        return call(list, index, 1, &icon) == 0 ? icon : 0;
    }
}
