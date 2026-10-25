namespace DesktopDock.Core;

public readonly record struct RoundedBounds(double X, double Y, double Width, double Height, double Radius)
{
    public bool Contains(double x, double y)
    {
        if (x < X || y < Y || x >= X + Width || y >= Y + Height) return false;
        double radius = Math.Min(Radius, Math.Min(Width, Height) / 2);
        double cx = Math.Clamp(x, X + radius, X + Width - radius);
        double cy = Math.Clamp(y, Y + radius, Y + Height - radius);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
    }
}

public static class DockFootprint
{
    public static RoundedBounds[] Create(DockGeometry geometry, double width, double height)
    {
        // Includes the small visible shadow, never the full transparent hosting strip.
        double top = geometry.BodyTop(height);
        var result = new List<RoundedBounds> { new(geometry.BodyLeft(width) - 5, top - 5, geometry.Width + 10, geometry.IconSize + 32, 24) };
        foreach (var slot in geometry.Slots)
        {
            double y = geometry.Baseline(height) - slot.Size;
            if (y < top) result.Add(new(slot.X - 1, y - 1, slot.Size + 2, slot.Size + 3, 8));
        }
        return result.ToArray();
    }
}
