using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class DesktopPanel : Window
{
    private readonly DockSettings settings;
    private readonly Action save, changed;
    private readonly Action<DockItem> launch, pin;
    private readonly string[]? directories;
    private readonly StackPanel groups = new();
    private readonly UniformGrid grid = new() { Columns = 6, Rows = 4 };
    private readonly TextBox search = new() { FontSize = 14, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(14, 10, 14, 10) };
    private readonly TextBlock caption = TransparentUi.Text("正在整理桌面…", 11, TransparentUi.Muted);
    private readonly TextBlock pageLabel = TransparentUi.Text("", 12, TransparentUi.Ink);
    private readonly TextBlock sectionTitle = TransparentUi.Text("全部桌面项目", 14, TransparentUi.Ink);
    private readonly Dictionary<string, string> categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly PageWheel wheel = new();
    private readonly Stopwatch interactionClock = Stopwatch.StartNew();
    private readonly Button previousPage, nextPage;
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Image Image, TextBlock Placeholder)> tiles = new(StringComparer.OrdinalIgnoreCase);
    private DockItem[] items = [], filtered = [];
    private string selectedGroup = "all";
    private int page, generation, transition;
    private bool hiding;
    private int PageSize => grid.Columns * grid.Rows;
    internal bool Open => IsVisible && !hiding;
    internal IReadOnlyList<DockItem> ItemsForChecks => items;
    internal int PageForChecks => page;

    public DesktopPanel(DockSettings settings, Action save, Action changed, Action<DockItem> launch, Action<DockItem> pin, string[]? directories = null)
    {
        this.settings = settings; this.save = save; this.changed = changed; this.launch = launch; this.pin = pin;
        this.directories = directories;
        Title = "Desktop Dock · 桌面应用"; Icon = AppBrand.Image();
        Width = 1040; Height = 690; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = Brushes.Transparent; Foreground = TransparentUi.Ink;
        FontFamily = new("Segoe UI Variable Display, Microsoft YaHei UI");
        SourceInitialized += (_, _) => FeatureUi.ToolWindow(this);
        TransparentUi.Install(this);
        var layout = new DockPanel();
        var header = new Grid { Margin = new(0, 0, 0, 18) }; header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = new StackPanel { Orientation = Orientation.Horizontal };
        identity.Children.Add(new Image { Source = AppBrand.Image(), Width = 36, Height = 36, Margin = new(0, 0, 12, 0) });
        var titles = new StackPanel(); titles.Children.Add(TransparentUi.Text("桌面应用", 24, TransparentUi.Ink)); titles.Children.Add(TransparentUi.Text("DESKTOP LIBRARY", 9, TransparentUi.Muted)); identity.Children.Add(titles);
        header.Children.Add(identity);
        var close = TransparentUi.Button("✕", Dismiss); close.ToolTip = "关闭 · Esc"; Grid.SetColumn(close, 1); header.Children.Add(close);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var searchArea = new Grid(); searchArea.Children.Add(search);
        search.Foreground = TransparentUi.Ink; search.CaretBrush = Brushes.White;
        var placeholder = TransparentUi.Text("搜索应用、文件和文件夹", 13, TransparentUi.Muted); placeholder.Margin = new(14, 0, 0, 0); placeholder.IsHitTestVisible = false; searchArea.Children.Add(placeholder);
        search.TextChanged += (_, _) => { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; page = 0; RefreshGrid(); };
        var searchFrame = new Border { Background = TransparentUi.Tint(22), CornerRadius = new(15), Child = searchArea, Margin = new(0, 0, 0, 20) };
        DockPanel.SetDock(searchFrame, Dock.Top); layout.Children.Add(searchFrame);
        var footer = new Grid { Margin = new(0, 10, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(caption);
        var pages = new StackPanel { Orientation = Orientation.Horizontal };
        previousPage = TransparentUi.Button("‹", () => ChangePage(-1)); nextPage = TransparentUi.Button("›", () => ChangePage(1));
        previousPage.ToolTip = "上一页 · 向上滚动"; nextPage.ToolTip = "下一页 · 向下滚动"; pageLabel.MinWidth = 57; pageLabel.TextAlignment = TextAlignment.Center;
        pages.Children.Add(previousPage); pages.Children.Add(pageLabel); pages.Children.Add(nextPage);
        pages.Children.Add(TransparentUi.Button("↻", () => _ = Reload())); Grid.SetColumn(pages, 1); footer.Children.Add(pages);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new(186) }); body.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { Margin = new(0, 0, 16, 0) };
        var addGroup = new StackPanel { Margin = new(0, 12, 0, 0) };
        var groupName = new TextBox { MaxLength = 24, Padding = new(9), FontSize = 12, ToolTip = "填写分组名称", BorderThickness = new(0), Background = Brushes.Transparent, Foreground = TransparentUi.Ink, CaretBrush = Brushes.White };
        void AddGroup()
        {
            if (settings.DesktopGroups.Count >= 40) return;
            string name = string.IsNullOrWhiteSpace(groupName.Text) ? "新分组" : groupName.Text.Trim();
            var group = new DesktopGroup { Name = name }; settings.DesktopGroups.Add(group); groupName.Clear(); selectedGroup = group.Id; page = 0; save(); RefreshGroups(); RefreshGrid();
        }
        groupName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddGroup(); e.Handled = true; } };
        addGroup.Children.Add(TransparentUi.Text("分组名称（可选）", 10, TransparentUi.Muted));
        addGroup.Children.Add(new Border { CornerRadius = new(10), Background = TransparentUi.Tint(25), Child = groupName }); addGroup.Children.Add(TransparentUi.Button("＋ 新建分组", AddGroup));
        DockPanel.SetDock(addGroup, Dock.Bottom); sidebar.Children.Add(addGroup);
        var groupScroll = new ScrollViewer { Content = groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        groupScroll.Resources.Add(typeof(ScrollBar), TransparentUi.ScrollBarStyle()); sidebar.Children.Add(groupScroll);
        body.Children.Add(sidebar);
        var collection = new DockPanel { Margin = new(10, 0, 0, 0) };
        sectionTitle.Margin = new(8, 3, 0, 12); DockPanel.SetDock(sectionTitle, Dock.Top); collection.Children.Add(sectionTitle); collection.Children.Add(grid);
        grid.RenderTransform = new TranslateTransform();
        grid.PreviewMouseWheel += (_, e) => { int delta = wheel.Update(e.Delta, interactionClock.Elapsed.TotalSeconds); if (delta != 0) ChangePage(delta); e.Handled = true; };
        Grid.SetColumn(collection, 1); body.Children.Add(collection); layout.Children.Add(body);
        var card = TransparentUi.Card(layout, 26); card.Padding = new(28); card.Margin = new(0);
        Content = card;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
            else if (e.Key == Key.PageDown) { ChangePage(1); e.Handled = true; }
            else if (e.Key == Key.PageUp) { ChangePage(-1); e.Handled = true; }
            else if (e.Key == Key.Enter && search.IsKeyboardFocusWithin && filtered.Length > 0) { OpenItem(filtered[page * PageSize]); e.Handled = true; }
        };
        Deactivated += (_, _) => { if (IsVisible && !hiding) Dismiss(); };
        Closing += (_, e) => { if (!disposing) { e.Cancel = true; Dismiss(); } };
    }

    private bool disposing;
    internal void DisposeWindow() { disposing = true; generation++; Close(); }

    public async Task Present()
    {
        transition++; hiding = false; if (!IsVisible) Opacity = 0;
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double dpi = Native.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle()) / 96d;
        Width = Math.Min(1040, work.Width / dpi - 36); Height = Math.Min(690, work.Height / dpi - 120);
        grid.Columns = Width < 850 ? 4 : 6;
        grid.Rows = Height < 580 ? 3 : 4;
        Left = work.Left / dpi + (work.Width / dpi - Width) / 2;
        Top = work.Top / dpi + Math.Max(10, (work.Height / dpi - Height) / 2 - 20);
        Show(); Activate(); FeatureUi.Fade(this, true, settings.ReducedMotion); changed();
        search.Clear(); search.Focus(); await Reload();
    }
    public void Dismiss()
    {
        if (!IsVisible || hiding) return;
        hiding = true; int version = ++transition;
        FeatureUi.Fade(this, false, settings.ReducedMotion, () => { if (version == transition) { Hide(); hiding = false; changed(); } });
    }
    internal void DismissImmediately()
    {
        transition++; generation++; hiding = true;
        BeginAnimation(OpacityProperty, null); Hide(); hiding = false; changed();
    }
    public async Task Reload()
    {
        int version = ++generation;
        var next = await Task.Run(() =>
        {
            var entries = directories is null ? DesktopCatalog.Read() : DesktopLibrary.Read(directories);
            return (Entries: entries, Categories: entries.ToDictionary(x => x.Target, x => DesktopLibrary.Category(x, Directory.Exists(x.Target)), StringComparer.OrdinalIgnoreCase));
        });
        if (version != generation || disposing) return;
        items = next.Entries; categories.Clear(); foreach (var pair in next.Categories) categories[pair.Key] = pair.Value;
        RefreshGroups(); RefreshGrid();
        var missing = items.Where(x => !icons.ContainsKey(x.Target)).ToArray();
        foreach (var item in missing)
        {
            var pixels = await Task.Run(() => IconService.Load(item));
            if (version != generation || disposing) return;
            icons[item.Target] = IconService.Bitmap(pixels);
            if (tiles.TryGetValue(item.Target, out var tile)) { tile.Image.Source = icons[item.Target]; tile.Placeholder.Visibility = Visibility.Collapsed; }
        }
        foreach (string stale in icons.Keys.Except(items.Select(x => x.Target), StringComparer.OrdinalIgnoreCase).ToArray()) icons.Remove(stale);
    }
    private void RefreshGroups()
    {
        groups.Children.Clear();
        void Add(string id, string title, DesktopGroup? group = null)
        {
            var button = TransparentUi.Button("", () => { selectedGroup = id; page = 0; RefreshGroups(); RefreshGrid(); }, id == selectedGroup);
            var row = new DockPanel { Width = 132 }; var count = TransparentUi.Text(SelectItems(id, "").Length.ToString(), 10, TransparentUi.Muted);
            DockPanel.SetDock(count, Dock.Right); row.Children.Add(count); row.Children.Add(TransparentUi.Text(title, 12, TransparentUi.Ink)); button.Content = row;
            button.Padding = new(10, 10, 10, 10); button.Margin = new(0, 3, 0, 3);
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.AllowDrop = id == "ungrouped" || group is not null;
            button.Drop += (_, e) => { if (button.AllowDrop && e.Data.GetData(typeof(DockItem)) is DockItem item) { Assign(item, id == "ungrouped" ? null : id); e.Handled = true; } };
            button.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(DockItem)) && button.AllowDrop ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            if (group is not null)
            {
                var menu = new ContextMenu();
                var rename = new TextBox { Text = group.Name, MaxLength = 24, MinWidth = 120, Margin = new(5), ToolTip = "输入新名称后按 Enter" };
                rename.KeyDown += (_, e) => { if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(rename.Text)) { group.Name = rename.Text.Trim(); save(); menu.IsOpen = false; RefreshGroups(); e.Handled = true; } };
                menu.Items.Add(new MenuItem { Header = rename, StaysOpenOnClick = true });
                var remove = new MenuItem { Header = "删除分组（保留原文件）" };
                remove.Click += (_, _) => { settings.DesktopGroups.Remove(group); selectedGroup = "all"; save(); RefreshGroups(); RefreshGrid(); }; menu.Items.Add(remove); button.ContextMenu = menu;
            }
            groups.Children.Add(button);
        }
        Add("all", "全部项目");
        var smartLabel = TransparentUi.Text("自动归类", 10, TransparentUi.Muted); smartLabel.Margin = new(10, 17, 0, 6); groups.Children.Add(smartLabel);
        foreach (var preset in DesktopLibrary.Presets) Add("preset:" + preset.Id, preset.Glyph + "  " + preset.Name);
        var groupLabel = TransparentUi.Text("我的分组", 10, TransparentUi.Muted); groupLabel.Margin = new(10, 17, 0, 6); groups.Children.Add(groupLabel);
        Add("ungrouped", "未分组");
        foreach (var group in settings.DesktopGroups) Add(group.Id, group.Name, group);
    }
    private void Assign(DockItem item, string? id) { DesktopLibrary.Assign(settings.DesktopGroups, item.Target, id); save(); RefreshGroups(); RefreshGrid(); }
    private DockItem[] SelectItems(string id, string query) => id.StartsWith("preset:", StringComparison.Ordinal)
        ? DesktopLibrary.Filter(items.Where(x => categories.GetValueOrDefault(x.Target) == id[7..]), settings.DesktopGroups, "all", query)
        : DesktopLibrary.Filter(items, settings.DesktopGroups, id, query);
    private void ChangePage(int delta)
    {
        int next = Math.Clamp(page + delta, 0, Math.Max(0, (filtered.Length - 1) / PageSize));
        if (next == page) return;
        page = next; RefreshGrid();
        if (!settings.ReducedMotion)
        {
            grid.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop });
            ((TranslateTransform)grid.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta > 0 ? 12 : -12, 0, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }
    internal void FilterForChecks(string query, string group = "all") { search.Text = query; selectedGroup = group; page = 0; RefreshGrid(); }
    internal int FilteredCountForChecks => filtered.Length;
    private void RefreshGrid()
    {
        filtered = SelectItems(selectedGroup, search.Text);
        sectionTitle.Text = selectedGroup == "all" ? "全部桌面项目" : selectedGroup == "ungrouped" ? "未分组" : selectedGroup.StartsWith("preset:", StringComparison.Ordinal)
            ? DesktopLibrary.Presets.First(x => x.Id == selectedGroup[7..]).Name : settings.DesktopGroups.FirstOrDefault(x => x.Id == selectedGroup)?.Name ?? "我的分组";
        page = Math.Clamp(page, 0, Math.Max(0, (filtered.Length - 1) / PageSize));
        grid.Children.Clear(); tiles.Clear();
        foreach (var item in filtered.Skip(page * PageSize).Take(PageSize))
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            icons.TryGetValue(item.Target, out var image);
            var icon = new Image { Source = image, Width = 52, Height = 52 };
            var placeholder = TransparentUi.Text("▦", 38, FeatureUi.Blue); placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            var zoom = new ScaleTransform(1, 1);
            var imageFrame = new Grid { Width = 52, Height = 52, Margin = new(0, 0, 0, 11), RenderTransformOrigin = new(.5, .5), RenderTransform = zoom };
            imageFrame.Children.Add(icon); imageFrame.Children.Add(placeholder); content.Children.Add(imageFrame); tiles[item.Target] = (icon, placeholder);
            var name = TransparentUi.Text(item.Name, 11, TransparentUi.Ink); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextAlignment = TextAlignment.Center; name.MaxWidth = 92; content.Children.Add(name);
            var button = TransparentUi.Button("", () => OpenItem(item)); button.Content = content; button.ToolTip = item.Name; button.Padding = new(7, 10, 7, 7); button.Background = Brushes.Transparent;
            void Zoom(double value) => TransparentUi.Scale(zoom, value, settings.ReducedMotion);
            button.MouseEnter += (_, _) => { Zoom(1.15); button.Background = TransparentUi.Tint(30); button.Opacity = 1; };
            button.MouseLeave += (_, _) => { Zoom(button.IsKeyboardFocusWithin ? 1.15 : 1); button.Background = Brushes.Transparent; };
            button.GotKeyboardFocus += (_, _) => Zoom(1.15); button.LostKeyboardFocus += (_, _) => Zoom(button.IsMouseOver ? 1.15 : 1);
            button.PreviewMouseLeftButtonUp += (_, _) => Zoom(button.IsMouseOver ? 1.15 : 1);
            var menu = new ContextMenu(); var pinItem = new MenuItem { Header = "固定到 Dock" }; pinItem.Click += (_, _) => pin(item); menu.Items.Add(pinItem);
            var move = new MenuItem { Header = "移到分组" };
            foreach (var group in settings.DesktopGroups) { var entry = new MenuItem { Header = group.Name }; entry.Click += (_, _) => Assign(item, group.Id); move.Items.Add(entry); }
            var none = new MenuItem { Header = "未分组" }; none.Click += (_, _) => Assign(item, null); move.Items.Add(none); menu.Items.Add(move); button.ContextMenu = menu;
            System.Windows.Point pressed = default;
            button.PreviewMouseLeftButtonDown += (_, e) => { pressed = e.GetPosition(button); Zoom(1.05); };
            button.PreviewMouseMove += (_, e) =>
            {
                var p = e.GetPosition(button);
                if (e.LeftButton == MouseButtonState.Pressed && (p - pressed).Length > 8)
                { Zoom(1); DragDrop.DoDragDrop(button, new DataObject(typeof(DockItem), item), DragDropEffects.Move); }
            };
            grid.Children.Add(button);
        }
        if (filtered.Length == 0) grid.Children.Add(TransparentUi.Text(search.Text.Length > 0 ? "没有找到匹配项目" : "这里还没有项目", 13, TransparentUi.Muted));
        caption.Text = $"{filtered.Length} 个项目 · 滚轮翻页 · 拖到我的分组整理";
        pageLabel.Text = $"{page + 1} / {Math.Max(1, (filtered.Length + PageSize - 1) / PageSize)}";
        previousPage.IsEnabled = page > 0; previousPage.Opacity = page > 0 ? 1 : .3;
        nextPage.IsEnabled = (page + 1) * PageSize < filtered.Length; nextPage.Opacity = nextPage.IsEnabled ? 1 : .3;
    }
    private void OpenItem(DockItem item) { launch(item); Dismiss(); }
}
