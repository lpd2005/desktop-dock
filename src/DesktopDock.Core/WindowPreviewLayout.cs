namespace DesktopDock.Core;

public readonly record struct PreviewRectangle(double X, double Y, double Width, double Height);

public readonly record struct WindowPreviewLayout(int Capacity, double CardWidth, double ImageHeight)
{
    public const double Padding = 16, Gap = 12, Header = 32, Title = 32;
    public double Height => Padding * 2 + Header + Title + ImageHeight;
    public double Width(int visibleCount) => Padding * 2 + visibleCount * CardWidth + Math.Max(0, visibleCount - 1) * Gap;
    public static WindowPreviewLayout Calculate(double availableWidth, double availableHeight)
    {
        double card = Math.Min(240, Math.Max(1, availableWidth - Padding * 2));
        int capacity = Math.Clamp((int)((availableWidth - Padding * 2 + Gap) / (card + Gap)), 1, 4);
        double image = Math.Max(1, Math.Min(card * .6, availableHeight - Padding * 2 - Header - Title));
        return new(capacity, card, image);
    }
    public static PreviewRectangle Fit(double sourceWidth, double sourceHeight, double width, double height)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return new(0, 0, width, height);
        double ratio = Math.Min(width / sourceWidth, height / sourceHeight);
        double w = sourceWidth * ratio, h = sourceHeight * ratio;
        return new((width - w) / 2, (height - h) / 2, w, h);
    }
}
