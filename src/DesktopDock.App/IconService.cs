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
                else
                {
                    DrawFallback(dc, item.Target);
                }
            }
            var bitmap = new RenderTargetBitmap(Resolution, Resolution, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[Resolution * Resolution * 4];
            bitmap.CopyPixels(pixels, Resolution * 4, 0);
            return pixels;
        }
        finally { if (icon != 0) Native.DestroyIcon(icon); }
    }

    private static void DrawFallback(DrawingContext dc, string target)
    {
        if (target == "shell:RecycleBinFolder")
        {
            var silver = new LinearGradientBrush(Color.FromRgb(250, 253, 255), Color.FromRgb(164, 185, 205), 0);
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(126, 148, 170)), 1.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), null, new(64, 112), 34, 6);
            dc.DrawRoundedRectangle(silver, edge, new Rect(32, 24, 64, 84), 12, 12);
            for (int x = 44; x <= 84; x += 10)
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(125, 136, 160, 183)), 2.5), new(x, 43), new(x, 91));
            dc.DrawEllipse(new LinearGradientBrush(Color.FromRgb(242, 248, 255), Color.FromRgb(177, 195, 216), 90), edge, new(64, 26), 36, 9);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(117, 139, 160)), new Pen(Brushes.White, 1), new(64, 26), 28, 4);
            return;
        }
        bool preferences = target is "dock:settings" or "ms-settings:";
        Color top = preferences ? Color.FromRgb(243, 248, 255) : Color.FromRgb(80, 192, 255);
        Color bottom = preferences ? Color.FromRgb(158, 178, 202) : Color.FromRgb(26, 97, 221);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), null, new Rect(9, 13, 110, 108), 25, 25);
        dc.DrawRoundedRectangle(new LinearGradientBrush(top, bottom, 90), new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), 1.5), new Rect(8, 7, 112, 112), 25, 25);
        var text = new FormattedText(preferences ? "\uE713" : "\uE774", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe MDL2 Assets"), preferences ? 78 : 66,
            preferences ? new LinearGradientBrush(Color.FromRgb(90, 109, 132), Color.FromRgb(39, 53, 72), 90) : Brushes.White, 1);
        dc.DrawText(text, new System.Windows.Point((128 - text.Width) / 2, (126 - text.Height) / 2));
    }

    private static unsafe nint GetListIcon(nint list, int index)
    {
        nint icon = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)(*(nint**)list)[10];
        return call(list, index, 1, &icon) == 0 ? icon : 0;
    }
}
