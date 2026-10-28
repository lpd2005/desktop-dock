using System.Windows.Media.Imaging;

namespace DesktopDock.App;

internal static class AppBrand
{
    private static readonly Lazy<BitmapImage> image = new(CreateImage);
    public static BitmapImage Image() => image.Value;
    private static BitmapImage CreateImage()
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/Assets/Brand.png"));
        image.Freeze(); return image;
    }
    public static System.Drawing.Icon TrayIcon()
    {
        using var source = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Brand.ico")).Stream;
        using var icon = new System.Drawing.Icon(source, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
}
