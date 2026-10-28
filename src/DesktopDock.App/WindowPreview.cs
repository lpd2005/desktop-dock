using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class WindowPreview : IDisposable
{
    private readonly Window window;
    private readonly StackPanel root = new();
    private readonly Border body;
    private sealed class PreviewCard(Border frame)
    {
        public Border Frame { get; } = frame;
        public SolidColorBrush Background { get; } = new(RestColor);
        public SolidColorBrush Outline { get; } = new(RestColor);
        public TimedTransition Highlight { get; } = new();
    }
    private static readonly Color RestColor = Color.FromRgb(232, 235, 240);
    private static readonly Color HoverColor = Color.FromRgb(255, 255, 255);
    private static readonly Color HoverOutline = Color.FromRgb(151, 183, 228);
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(33, 38, 47));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(100, 109, 122));
    private readonly List<PreviewCard> cards = [];
    private readonly List<DwmPreviewSurface> surfaces = [];
    private readonly List<(Border Area, DwmPreviewSurface Surface)> placements = [];
    private readonly List<(TextBlock Label, PreviewTarget Target)> titles = [];
    private readonly Action<PreviewTarget> activate;
    private readonly TimedTransition fade = new();
    private readonly AnimationClock animation;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly PageWheel wheel = new();
    private PreviewTarget[] targets = [];
    private WindowPreviewLayout layout;
    private ImageSource? icon;
    private string appName = "";
    private Native.Rect work;
    private double anchorX, anchorTop, scale;
    private int page;
    private bool reduced, disposed;
    private bool hoverPending;
    private int hoveredCard = -1;
    private int? hoverIndexForChecks;
    public bool Open { get; private set; }
    public nint Handle => new WindowInteropHelper(window).Handle;
    internal int ThumbnailCount => surfaces.Count(x => x.Available);
    internal int Page => page;
    internal bool Animating => fade.Active || cards.Any(x => x.Highlight.Active);
    internal int HoveredCardForChecks => hoveredCard;
    internal IReadOnlyList<Color> CardColorsForChecks => cards.Select(x => x.Background.Color).ToArray();
    internal Border BodyForChecks => body;
    internal void HoverForChecks(int? index)
    {
        hoverIndexForChecks = index;
        if (index is >= 0 && index < surfaces.Count) Native.SendMessage(surfaces[index.Value].Handle, 0x0200, 0, new(20 | 20 << 16));
        else QueueHover();
    }
    internal IReadOnlyList<PreviewTarget> Targets => targets;
    internal IReadOnlyList<nint> SurfaceHandlesForChecks => surfaces.Select(x => x.Handle).ToArray();
    internal void WheelForChecks(int delta) => TurnWheel(delta);
    internal void InspectForChecks()
    {
        window.ShowInTaskbar = true;
        long style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
        Native.SetWindowLongPtr(Handle, -20, new((style & ~0x80L) | 0x40000));
    }

    public WindowPreview(Action<PreviewTarget> activate)
    {
        this.activate = activate;
        window = new Window
        {
            Title = "Desktop Dock · 窗口预览", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, FontFamily = new("Segoe UI Variable Display, Microsoft YaHei UI")
        };
        // Only the rounded exterior/shadow is alpha; the complete preview body
        // and individual cards are opaque so underlying windows cannot show through.
        window.AllowsTransparency = true; window.Background = Brushes.Transparent;
        body = new Border { Child = root, Margin = new(8), Padding = new(7), CornerRadius = new(14),
            Background = new SolidColorBrush(RestColor), BorderBrush = new SolidColorBrush(Color.FromRgb(201, 208, 219)), BorderThickness = new(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .2, Color = Colors.Black } };
        window.Content = body;
        window.SourceInitialized += (_, _) =>
        {
            FeatureUi.ToolWindow(window, noActivate: true);
            HwndSource.FromHwnd(Handle).AddHook((nint hwnd, int message, nint wparam, nint lparam, ref bool handled) =>
            { if (message == 0x0021) { handled = true; return 3; } return 0; });
        };
        window.PreviewMouseWheel += (_, e) => { TurnWheel(e.Delta); e.Handled = true; };
        window.MouseMove += (_, _) => QueueHover();
        window.MouseLeave += (_, _) => QueueHover();
        animation = new(window.Dispatcher, Frame);
    }
    public void Show(string name, IEnumerable<nint> windows, ImageSource image, double centerX, double bottom,
        Native.Rect workingArea, double dpi, bool reducedMotion)
    {
        if (disposed) return;
        ClearSurfaces(); appName = name; icon = image;
        targets = windows.Select(PreviewTarget.Capture).Where(x => x.Valid).Distinct().ToArray();
        if (targets.Length == 0) { Hide(true); return; }
        anchorX = centerX; anchorTop = bottom; work = workingArea; scale = dpi; reduced = reducedMotion; page = 0;
        layout = WindowPreviewLayout.Calculate((work.Right - work.Left - 16) / scale, (work.Bottom - work.Top - 16) / scale);
        Open = true; BuildPage(); Animate(true);
    }
    public void Refresh(IEnumerable<nint> windows)
    {
        if (!Open) return;
        var available = windows.Select(PreviewTarget.Capture).Where(x => x.Valid).Distinct().ToArray();
        // Preserve the displayed order when foreground/z-order changes.
        var ordered = targets.Where(available.Contains).Concat(available.Where(x => !targets.Contains(x))).ToArray();
        if (ordered.Length == 0) { Hide(); return; }
        if (ordered.SequenceEqual(targets)) { UpdateTitles(); Present(); return; }
        targets = ordered; page = Math.Min(page, (targets.Length - 1) / layout.Capacity); BuildPage();
    }
    private void BuildPage()
    {
        ClearSurfaces(); root.Children.Clear(); titles.Clear();
        int pages = (targets.Length + layout.Capacity - 1) / layout.Capacity;
        var row = new StackPanel { Orientation = Orientation.Horizontal }; root.Children.Add(row);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (pages > 1)
        {
            var previous = NavButton("‹", -1); previous.IsEnabled = page > 0; nav.Children.Add(previous);
            nav.Children.Add(FeatureUi.Text($"{page + 1} / {pages}", 11, Muted));
            var next = NavButton("›", 1); next.IsEnabled = page + 1 < pages; nav.Children.Add(next);
        }
        if (pages > 1) { nav.Height = WindowPreviewLayout.Header; root.Children.Add(nav); }
        var visible = targets.Skip(page * layout.Capacity).Take(layout.Capacity).ToArray();
        window.Width = layout.Width(visible.Length); window.Height = layout.HeightFor(pages > 1);
        window.Left = Math.Clamp(anchorX / scale - window.Width / 2, (work.Left + 8) / scale, (work.Right - 8) / scale - window.Width);
        window.Top = Math.Max((work.Top + 8) / scale, anchorTop / scale - window.Height);
        if (!window.IsVisible) window.Show();
        foreach (var target in visible)
        {
            var column = new StackPanel();
            var frame = new Border { Child = column, Width = layout.CardWidth, Padding = new(WindowPreviewLayout.ItemPadding - 1),
                BorderThickness = new(1), CornerRadius = new(10), Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new(row.Children.Count > 0 ? WindowPreviewLayout.Gap : 0, 0, 0, 0) };
            var card = new PreviewCard(frame); frame.Background = card.Background; frame.BorderBrush = card.Outline; cards.Add(card);
            var title = new StringBuilder(512); Native.GetWindowText(target.Window, title, title.Capacity);
            var titleRow = new DockPanel { Height = WindowPreviewLayout.Title };
            titleRow.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new(0, 0, 7, 0) });
            var text = FeatureUi.Text(title.Length > 0 ? title.ToString() : appName, 12, Ink); text.TextTrimming = TextTrimming.CharacterEllipsis;
            titles.Add((text, target));
            titleRow.Children.Add(text); column.Children.Add(titleRow);
            var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            placeholder.Children.Add(new Image { Source = icon, Height = 40, Margin = new(0, 0, 0, 8) });
            var fallback = FeatureUi.Text(Native.IsIconic(target.Window) ? "已最小化 · 点击恢复" : "点击切换窗口", 11, Muted);
            fallback.HorizontalAlignment = HorizontalAlignment.Center; placeholder.Children.Add(fallback);
            var area = new Border { Height = layout.ImageHeight, Background = new SolidColorBrush(Color.FromRgb(219, 225, 233)), CornerRadius = new(6), Child = placeholder,
                Cursor = System.Windows.Input.Cursors.Hand };
            column.Children.Add(area);
            Point? pressedAt = null;
            frame.MouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(frame);
            frame.MouseLeave += (_, _) => { pressedAt = null; QueueHover(); };
            frame.MouseLeftButtonUp += (_, e) =>
            {
                var released = e.GetPosition(frame);
                if (pressedAt is Point pressed && !DockReorder.Moved(released.X - pressed.X, released.Y - pressed.Y)) Activate(target);
                pressedAt = null; e.Handled = true;
            };
            row.Children.Add(frame);
            var surface = new DwmPreviewSurface(Handle, target, () => window.Dispatcher.BeginInvoke(() => Activate(target)),
                delta => window.Dispatcher.BeginInvoke(() => TurnWheel(delta)), QueueHover);
            surfaces.Add(surface); placements.Add((area, surface));
        }
        UpdateTitles(); window.UpdateLayout();
        foreach (var (area, surface) in placements)
        {
            if (!surface.Available) continue;
            var point = area.PointToScreen(new(0, 0));
            surface.Place((int)Math.Round(point.X), (int)Math.Round(point.Y), (int)Math.Round(area.ActualWidth * scale), (int)Math.Round(area.ActualHeight * scale), scale);
        }
        Present(); QueueHover();
    }
    private Button NavButton(string title, int direction)
    {
        var button = FeatureUi.Button(title, () => ChangePage(direction)); button.Background = Brushes.Transparent; button.Foreground = Ink;
        button.Padding = new(8, 0, 8, 1);
        button.Margin = new(4, 0, 4, 0); button.FontSize = 19; button.Focusable = false; return button;
    }
    private void TurnWheel(int delta)
    {
        int direction = wheel.Update(delta, clock.Elapsed.TotalSeconds);
        if (direction != 0) ChangePage(direction);
    }
    private void ChangePage(int direction)
    {
        if (!Open) return;
        int next = Math.Clamp(page + direction, 0, (targets.Length - 1) / layout.Capacity);
        if (next == page) return;
        page = next; BuildPage();
    }
    private void Activate(PreviewTarget target)
    {
        if (!Open || !target.Valid) return;
        activate(target); Hide();
    }
    private void UpdateTitles()
    {
        foreach (var (label, target) in titles)
        {
            var title = new StringBuilder(512); Native.GetWindowText(target.Window, title, title.Capacity);
            string text = title.Length > 0 ? title.ToString() : appName;
            if (Native.IsIconic(target.Window)) text += " · 已最小化";
            label.Text = text;
        }
    }
    public bool ContainsPointer()
    {
        if (!Open) return false;
        Native.GetCursorPos(out var cursor);
        nint hit = Native.WindowFromPoint(cursor);
        return hit == Handle || surfaces.Any(x => x.Handle == hit);
    }
    public void Hide(bool immediate = false)
    {
        if (disposed) return;
        Open = false;
        if (immediate) { animation.Stop(); fade.Reset(0); Present(); FinishHide(); }
        else Animate(false);
    }
    private void Animate(bool show)
    {
        if (reduced) { animation.Stop(); fade.Reset(show ? 1 : 0); Present(); if (!show) FinishHide(); return; }
        fade.Begin(show ? 1 : 0, clock.Elapsed.TotalSeconds, show ? .16 : .1);
        if (fade.Active) animation.Start(); else if (!show) FinishHide();
    }
    private void Frame()
    {
        double now = clock.Elapsed.TotalSeconds;
        bool fading = fade.Active;
        fade.Sample(now); if (fading) Present();
        foreach (var card in cards)
        {
            if (!card.Highlight.Active) continue;
            card.Highlight.Sample(now); PaintCard(card);
        }
        if (Animating) return;
        animation.Stop(); if (!Open) FinishHide();
    }
    private void Present()
    {
        window.Opacity = fade.Value;
        foreach (var surface in surfaces) surface.Present((byte)Math.Round(fade.Value * 255));
    }
    private void QueueHover()
    {
        if (disposed || !Open || hoverPending) return;
        hoverPending = true;
        window.Dispatcher.BeginInvoke(() =>
        {
            hoverPending = false;
            if (disposed || !Open) return;
            int index = hoverIndexForChecks ?? CardUnderPointer();
            if (index == hoveredCard) return;
            hoveredCard = index;
            double now = clock.Elapsed.TotalSeconds;
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (reduced) card.Highlight.Reset(i == index ? 1 : 0);
                else card.Highlight.Begin(i == index ? 1 : 0, now, .12);
                PaintCard(card);
            }
            if (Animating) animation.Start();
        });
    }
    private int CardUnderPointer()
    {
        Native.GetCursorPos(out var point); nint hit = Native.WindowFromPoint(point);
        for (int i = 0; i < surfaces.Count; i++) if (surfaces[i].Handle == hit) return i;
        if (hit != Handle) return -1;
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i].Frame; var local = card.PointFromScreen(new(point.X, point.Y));
            if (local.X >= 0 && local.Y >= 0 && local.X < card.ActualWidth && local.Y < card.ActualHeight) return i;
        }
        return -1;
    }
    private static void PaintCard(PreviewCard card)
    {
        static Color Blend(Color from, Color to, double progress) => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * progress), (byte)Math.Round(from.G + (to.G - from.G) * progress), (byte)Math.Round(from.B + (to.B - from.B) * progress));
        card.Background.Color = Blend(RestColor, HoverColor, card.Highlight.Value);
        card.Outline.Color = Blend(RestColor, HoverOutline, card.Highlight.Value);
    }
    private void ClearSurfaces() { foreach (var surface in surfaces) surface.Dispose(); surfaces.Clear(); placements.Clear(); cards.Clear(); hoveredCard = -1; }
    private void FinishHide() { ClearSurfaces(); window.Hide(); root.Children.Clear(); titles.Clear(); targets = []; icon = null; }
    public void Dispose()
    {
        if (disposed) return;
        Hide(true); disposed = true; animation.Dispose(); window.Close();
    }
}
