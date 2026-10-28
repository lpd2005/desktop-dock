using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class WindowRegion(nint hwnd)
{
    private int lastHash;
    private readonly record struct RegionRectangle(int Left, int Top, int Right, int Bottom, int Diameter);
    public void Update(RoundedBounds[] shapes, float scale)
    {
        var hash = new HashCode();
        Span<RegionRectangle> rectangles = shapes.Length <= 128 ? stackalloc RegionRectangle[shapes.Length] : new RegionRectangle[shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
        {
            var shape = shapes[i];
            var rectangle = new RegionRectangle((int)Math.Floor(shape.X * scale), (int)Math.Floor(shape.Y * scale),
                (int)Math.Ceiling((shape.X + shape.Width) * scale), (int)Math.Ceiling((shape.Y + shape.Height) * scale), (int)(shape.Radius * 2 * scale));
            rectangles[i] = rectangle;
            hash.Add(rectangle.Left); hash.Add(rectangle.Top); hash.Add(rectangle.Right); hash.Add(rectangle.Bottom); hash.Add(rectangle.Diameter);
        }
        int value = hash.ToHashCode();
        if (value == lastHash) return;
        nint region = Native.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new System.ComponentModel.Win32Exception();
        foreach (var r in rectangles)
        {
            nint part = Native.CreateRoundRectRgn(r.Left, r.Top, r.Right + 1, r.Bottom + 1, r.Diameter, r.Diameter);
            if (part == 0) { Native.DeleteObject(region); throw new System.ComponentModel.Win32Exception(); }
            int combined = Native.CombineRgn(region, region, part, 2); // RGN_OR
            Native.DeleteObject(part);
            if (combined == 0) { Native.DeleteObject(region); throw new System.ComponentModel.Win32Exception(); }
        }
        if (Native.SetWindowRgn(hwnd, region, false) == 0)
        {
            Native.DeleteObject(region);
            throw new System.ComponentModel.Win32Exception();
        }
        // The system owns the region after SetWindowRgn succeeds.
        lastHash = value;
    }
}
