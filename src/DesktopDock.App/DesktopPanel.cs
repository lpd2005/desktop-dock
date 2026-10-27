using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly TextBlock caption = FeatureUi.Text("正在整理桌面…", 12, FeatureUi.Muted);
    private readonly TextBlock pageLabel = FeatureUi.Text("", 12, FeatureUi.Muted);
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
        Width = 1040; Height = 690; AllowsTransparency = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = Brushes.Transparent; Foreground = FeatureUi.Ink;
        FontFamily = new("Segoe UI Variable Display, Microsoft YaHei UI");
        SourceInitialized += (_, _) => FeatureUi.ToolWindow(this);
        var layout = new DockPanel();
        var header = new Grid { Margin = new(0, 0, 0, 18) }; header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = new StackPanel { Orientation = Orientation.Horizontal };
        identity.Children.Add(new Image { Source = AppBrand.Image(), Width = 36, Height = 36, Margin = new(0, 0, 12, 0) });
        var titles = new StackPanel(); titles.Children.Add(FeatureUi.Text("桌面应用", 24)); titles.Children.Add(FeatureUi.Text("清晰收纳，轻点即达。", 11, FeatureUi.Muted)); identity.Children.Add(titles);
        header.Children.Add(identity);
        var close = FeatureUi.Button("✕", Dismiss); Grid.SetColumn(close, 1); header.Children.Add(close);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var searchArea = new Grid(); searchArea.Children.Add(search);
        var placeholder = FeatureUi.Text("搜索桌面上的应用、文件和文件夹", 13, FeatureUi.Muted); placeholder.Margin = new(14, 0, 0, 0); placeholder.IsHitTestVisible = false; searchArea.Children.Add(placeholder);
        search.TextChanged += (_, _) => { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; page = 0; RefreshGrid(); };
        var searchFrame = new Border { Background = new SolidColorBrush(Color.FromArgb(175, 231, 237, 246)), CornerRadius = new(13), Child = searchArea, Margin = new(0, 0, 0, 19) };
        DockPanel.SetDock(searchFrame, Dock.Top); layout.Children.Add(searchFrame);
        var footer = new Grid { Margin = new(0, 10, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(caption);
        var pages = new StackPanel { Orientation = Orientation.Horizontal };
        pages.Children.Add(FeatureUi.Button("‹", () => ChangePage(-1))); pages.Children.Add(pageLabel); pages.Children.Add(FeatureUi.Button("›", () => ChangePage(1)));
        pages.Children.Add(FeatureUi.Button("刷新", () => _ = Reload())); Grid.SetColumn(pages, 1); footer.Children.Add(pages);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new(166) }); body.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { Margin = new(0, 0, 16, 0) };
        var addGroup = new StackPanel { Margin = new(0, 12, 0, 0) };
        var groupName = new TextBox { MaxLength = 24, Padding = new(8), FontSize = 12, ToolTip = "填写分组名称", BorderThickness = new(0), Background = new SolidColorBrush(Color.FromRgb(232, 238, 247)) };
        void AddGroup()
        {
            if (settings.DesktopGroups.Count >= 40) return;
            string name = string.IsNullOrWhiteSpace(groupName.Text) ? "新分组" : groupName.Text.Trim();
            var group = new DesktopGroup { Name = name }; settings.DesktopGroups.Add(group); groupName.Clear(); selectedGroup = group.Id; page = 0; save(); RefreshGroups(); RefreshGrid();
        }
        groupName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddGroup(); e.Handled = true; } };
        addGroup.Children.Add(FeatureUi.Text("分组名称（可选）", 10, FeatureUi.Muted));
        addGroup.Children.Add(groupName); addGroup.Children.Add(FeatureUi.Button("＋ 新建分组", AddGroup));
        DockPanel.SetDock(addGroup, Dock.Bottom); sidebar.Children.Add(addGroup);
        sidebar.Children.Add(new ScrollViewer { Content = groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(sidebar); Grid.SetColumn(grid, 1); body.Children.Add(grid); layout.Children.Add(body);
        var card = FeatureUi.Card(layout, 28); card.Padding = new(27); card.Margin = new(17);
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
        transition++; hiding = false; Opacity = 0;
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
    public async Task Reload()
    {
        int version = ++generation;
        var next = await Task.Run(() => directories is null ? DesktopCatalog.Read() : DesktopLibrary.Read(directories));
        if (version != generation || disposing) return;
        items = next; RefreshGroups(); RefreshGrid();
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
            var button = FeatureUi.Button(title, () => { selectedGroup = id; page = 0; RefreshGroups(); RefreshGrid(); }, id == selectedGroup);
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.AllowDrop = true;
            button.Drop += (_, e) => { if (id != "all" && e.Data.GetData(typeof(DockItem)) is DockItem item) { Assign(item, id == "ungrouped" ? null : id); e.Handled = true; } };
            button.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(DockItem)) && id != "all" ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
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
        Add("all", "全部桌面项目"); Add("ungrouped", "未分组");
        groups.Children.Add(FeatureUi.Text("我的分组", 10, FeatureUi.Muted));
        foreach (var group in settings.DesktopGroups) Add(group.Id, group.Name, group);
    }
    private void Assign(DockItem item, string? id) { DesktopLibrary.Assign(settings.DesktopGroups, item.Target, id); save(); RefreshGroups(); RefreshGrid(); }
    private void ChangePage(int delta) { page = Math.Clamp(page + delta, 0, Math.Max(0, (filtered.Length - 1) / PageSize)); RefreshGrid(); }
    internal void FilterForChecks(string query, string group = "all") { search.Text = query; selectedGroup = group; page = 0; RefreshGrid(); }
    internal int FilteredCountForChecks => filtered.Length;
    private void RefreshGrid()
    {
        filtered = DesktopLibrary.Filter(items, settings.DesktopGroups, selectedGroup, search.Text);
        page = Math.Clamp(page, 0, Math.Max(0, (filtered.Length - 1) / PageSize));
        grid.Children.Clear(); tiles.Clear();
        foreach (var item in filtered.Skip(page * PageSize).Take(PageSize))
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            icons.TryGetValue(item.Target, out var image);
            var icon = new Image { Source = image, Width = 48, Height = 48 };
            var placeholder = FeatureUi.Text("▦", 38, FeatureUi.Blue); placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            var imageFrame = new Grid { Width = 48, Height = 48, Margin = new(0, 0, 0, 9) };
            imageFrame.Children.Add(icon); imageFrame.Children.Add(placeholder); content.Children.Add(imageFrame); tiles[item.Target] = (icon, placeholder);
            var name = FeatureUi.Text(item.Name, 11); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextAlignment = TextAlignment.Center; name.MaxWidth = 92; content.Children.Add(name);
            var button = FeatureUi.Button("", () => OpenItem(item)); button.Content = content; button.ToolTip = item.Name; button.Padding = new(7, 10, 7, 7); button.Background = Brushes.Transparent;
            var menu = new ContextMenu(); var pinItem = new MenuItem { Header = "固定到 Dock" }; pinItem.Click += (_, _) => pin(item); menu.Items.Add(pinItem);
            var move = new MenuItem { Header = "移到分组" };
            foreach (var group in settings.DesktopGroups) { var entry = new MenuItem { Header = group.Name }; entry.Click += (_, _) => Assign(item, group.Id); move.Items.Add(entry); }
            var none = new MenuItem { Header = "未分组" }; none.Click += (_, _) => Assign(item, null); move.Items.Add(none); menu.Items.Add(move); button.ContextMenu = menu;
            System.Windows.Point pressed = default;
            button.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(button);
            button.PreviewMouseMove += (_, e) =>
            {
                var p = e.GetPosition(button);
                if (e.LeftButton == MouseButtonState.Pressed && (p - pressed).Length > 8)
                    DragDrop.DoDragDrop(button, new DataObject(typeof(DockItem), item), DragDropEffects.Move);
            };
            grid.Children.Add(button);
        }
        if (filtered.Length == 0) grid.Children.Add(FeatureUi.Text(search.Text.Length > 0 ? "没有找到匹配项目" : "这里还没有桌面项目", 13, FeatureUi.Muted));
        caption.Text = $"{filtered.Length} 个项目 · 拖到左侧分组，或右键整理";
        pageLabel.Text = $"{page + 1} / {Math.Max(1, (filtered.Length + PageSize - 1) / PageSize)}";
    }
    private void OpenItem(DockItem item) { launch(item); Dismiss(); }
}
