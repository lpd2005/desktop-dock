namespace DesktopDock.Core;

public readonly record struct DockSlot(double X, double Size);
public sealed record DockGeometry(DockSlot[] Slots, double Width, double IconSize, double SeparatorX = -1)
{
    public const double Padding = 14;
    public const double Foot = 10;
    public const double IconInset = 9;
    public double BodyLeft(double width) => (width - Width) / 2;
    public double BodyTop(double height) => height - Foot - IconSize - 22;
    public double Baseline(double height) => height - Foot - IconInset;
    public int HitIcon(double x, double y, double height)
    {
        double bottom = Baseline(height);
        for (int i = 0; i < Slots.Length; i++)
            if (x >= Slots[i].X && x <= Slots[i].X + Slots[i].Size && y >= bottom - Slots[i].Size && y <= bottom) return i;
        return -1;
    }
}

public static class DockLayout
{
    // Influence uses stable, unmagnified anchors so the pointer cannot create layout feedback.
    public static DockGeometry Calculate(int count, double iconSize, double magnification,
        double? pointerX, double availableWidth, double hover = 1, int separatorBefore = -1)
    {
        if (count < 0 || count > 80) throw new ArgumentOutOfRangeException(nameof(count));
        iconSize = Math.Clamp(iconSize, 32, 80);
        magnification = Math.Clamp(magnification, 1, 1.8);
        if (count == 0) return new([], 32, iconSize);
        bool separated = separatorBefore > 0 && separatorBefore < count;
        double separatorGap = separated ? 14 : 0;
        double gap = Math.Min(8, availableWidth / (count * 5)), padding = DockGeometry.Padding;
        // Reserve room for the entire wave, not just the unexpanded row.
        double budget = Math.Max(180, availableWidth) - padding * 2 - (count - 1) * gap - separatorGap;
        iconSize = Math.Min(iconSize, Math.Max(8, budget / (count + 3 * (magnification - 1))));
        var sizes = new double[count];
        double baseline = count * iconSize + (count - 1) * gap + padding * 2 + separatorGap;
        for (int i = 0; i < count; i++)
        {
            double anchor = (availableWidth - baseline) / 2 + padding + i * (iconSize + gap) + iconSize / 2 + (separated && i >= separatorBefore ? separatorGap : 0);
            double distance = pointerX.HasValue ? Math.Abs(pointerX.Value - anchor) / (iconSize * 2.2) : 1;
            double influence = distance < 1 ? (1 + Math.Cos(distance * Math.PI)) / 2 : 0;
            sizes[i] = iconSize * (1 + (magnification - 1) * influence * Math.Clamp(hover, 0, 1));
        }
        double width = sizes.Sum() + (count - 1) * gap + padding * 2 + separatorGap;
        double x = (availableWidth - width) / 2 + padding;
        var slots = new DockSlot[count];
        double separatorX = -1;
        for (int i = 0; i < count; i++)
        {
            if (separated && i == separatorBefore) { separatorX = x + (separatorGap - gap) / 2; x += separatorGap; }
            slots[i] = new(x, sizes[i]); x += sizes[i] + gap;
        }
        return new(slots, width, iconSize, separatorX);
    }
}
