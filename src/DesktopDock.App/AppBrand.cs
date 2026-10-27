using System.Windows.Media.Imaging;

namespace DesktopDock.App;

internal static class AppBrand
{
    public static BitmapImage Image()
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
