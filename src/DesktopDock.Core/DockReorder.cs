namespace DesktopDock.Core;

public static class DockReorder
{
    public const double DragThreshold = 6;
    public static int Target(DockGeometry geometry, int count, int source, double x, int start = 0)
    {
        if (start < 0 || source < start || source >= start + count || start + count > geometry.Slots.Length) return -1;
        int target = source;
        while (target + 1 < start + count && x > Center(geometry.Slots[target + 1])) target++;
        while (target > start && x < Center(geometry.Slots[target - 1])) target--;
        return target;
    }
    public static int Insertion(DockGeometry geometry, int count, double x, int start = 0)
    {
        for (int i = 0; i < Math.Min(count, geometry.Slots.Length - start); i++)
            if (x < Center(geometry.Slots[start + i])) return i;
        return count;
    }
    public static int[] Order(int total, int from, int to, int movable, int start = 0)
    {
        var order = Enumerable.Range(0, total).ToList();
        if (start < 0 || from < start || from >= start + movable || to < start || to >= start + movable || start + movable > total) return order.ToArray();
        order.RemoveAt(from); order.Insert(to, from);
        return order.ToArray();
    }
    public static bool Moved(double dx, double dy) => dx * dx + dy * dy >= DragThreshold * DragThreshold;
    public static bool RemoveOnRelease(DockGeometry geometry, double width, double height, double x, double y)
    {
        double dx = x - Math.Clamp(x, geometry.BodyLeft(width), geometry.BodyLeft(width) + geometry.Width);
        double dy = y - Math.Clamp(y, geometry.BodyTop(height), geometry.Baseline(height));
        return dx * dx + dy * dy >= 80 * 80;
    }
    private static double Center(DockSlot slot) => slot.X + slot.Size / 2;
}
