namespace DesktopDock.Core;

public static class IconReflection
{
    public const double Depth = .5;
    public const double Opacity = .28;

    // Cache the entire mirrored icon. The compositor compresses its height to
    // Depth while keeping the width, so the complete reflection fits below it.
    public static byte[] Create(byte[] source, int side)
    {
        if (side <= 0 || source.Length != checked(side * side * 4)) throw new ArgumentException("Invalid square icon.");
        byte[] result = new byte[source.Length];
        for (int y = 0; y < side; y++)
        {
            double fade = side > 1 ? 1 - (double)y / (side - 1) : 1;
            double alpha = Opacity * fade * fade;
            int row = (side - 1 - y) * side * 4;
            for (int x = 0; x < side * 4; x++) result[y * side * 4 + x] = (byte)Math.Round(source[row + x] * alpha);
        }
        return result;
    }
}
