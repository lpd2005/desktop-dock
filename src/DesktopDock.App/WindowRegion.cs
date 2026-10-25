using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class WindowRegion(nint hwnd)
{
    private int lastHash;
    public void Update(RoundedBounds[] shapes, float scale)
    {
        var hash = new HashCode();
        var rectangles = shapes.Select(shape => new[] { (int)Math.Floor(shape.X * scale), (int)Math.Floor(shape.Y * scale),
            (int)Math.Ceiling((shape.X + shape.Width) * scale), (int)Math.Ceiling((shape.Y + shape.Height) * scale), (int)(shape.Radius * 2 * scale) }).ToArray();
        foreach (var rectangle in rectangles) foreach (int part in rectangle) hash.Add(part);
        int value = hash.ToHashCode();
        if (value == lastHash) return;
        nint region = Native.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new System.ComponentModel.Win32Exception();
        foreach (var r in rectangles)
        {
            nint part = Native.CreateRoundRectRgn(r[0], r[1], r[2] + 1, r[3] + 1, r[4], r[4]);
            if (part == 0) { Native.DeleteObject(region); throw new System.ComponentModel.Win32Exception(); }
            int combined = Native.CombineRgn(region, region, part, 2); // RGN_OR
            Native.DeleteObject(part);
            if (combined == 0) { Native.DeleteObject(region); throw new System.ComponentModel.Win32Exception(); }
        }
        if (Native.SetWindowRgn(hwnd, region, true) == 0)
        {
            Native.DeleteObject(region);
            throw new System.ComponentModel.Win32Exception();
        }
        // The system owns the region after SetWindowRgn succeeds.
        lastHash = value;
    }
}
