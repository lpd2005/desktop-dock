using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed class WidgetWindow : Window
{
    internal readonly string Kind;
    internal readonly StackPanel Body = new();
    private readonly Border card;
    public WidgetWindow(string kind, string title, double height, bool light, Action remove, Action<WidgetWindow> moved)
    {
        Kind = kind; Title = "Desktop Dock · " + title; Icon = AppBrand.Image();
        Width = 262; Height = height; ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        FontFamily = new("Segoe UI Variable Display, Microsoft YaHei UI");
        var content = new DockPanel();
        var header = new DockPanel { Margin = new(0, 0, 0, 8) };
        var close = LiquidGlass.Button("×", remove); close.Padding = new(4, 0, 4, 0); close.Margin = new(0); close.Opacity = 0; close.Background = Brushes.Transparent; close.ToolTip = "移除此小组件";
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(FeatureUi.Text(title, 11, LiquidGlass.Muted));
        DockPanel.SetDock(header, Dock.Top); content.Children.Add(header); content.Children.Add(Body);
        card = LiquidGlass.Card(content, 22, light); card.Padding = new(20, 16, 20, 16);
        Foreground = LiquidGlass.Ink;
        Content = card;
        MouseEnter += (_, _) => close.Opacity = 1;
        MouseLeave += (_, _) => close.Opacity = 0;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node is not null) { if (node is ButtonBase) return; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
            try { DragMove(); moved(this); e.Handled = true; } catch (InvalidOperationException) { }
        };
        SourceInitialized += (_, _) => FeatureUi.ToolWindow(this, noActivate: true);
        LiquidGlass.Install(this);
    }
    internal nint Handle => new WindowInteropHelper(this).Handle;
}

internal sealed class DesktopWidgets : IDisposable
{
    private readonly DockSettings settings;
    private readonly Action save;
    private readonly string cacheFile;
    private readonly bool network;
    private readonly WeatherService weatherService;
    private readonly Dictionary<string, WidgetWindow> windows = [];
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer clockTimer, weatherTimer;
    private readonly Native.WinEvent foregroundEvent;
    private readonly nint foregroundHook;
    private CancellationTokenSource cancellation = new();
    private (bool Clock, bool Weather, bool Light, string City)? configuration;
    private TextBlock? clockText, clockDate, weatherCity, temperature, weatherDescription, weatherDetail, weatherStatus, weatherGlyph;
    private DateOnly date = DateOnly.MinValue;
    private WeatherSnapshot? lastWeather;
    private bool weatherFailed;
    private bool fetching, disposed, restackPending;
    private int weatherGeneration;
    internal IReadOnlyCollection<WidgetWindow> WindowsForChecks => windows.Values;
    internal bool WeatherFetchingForChecks => fetching;
    internal bool ClockTickingForChecks => clockTimer.IsEnabled;

    public DesktopWidgets(DockSettings settings, Action save, string data, bool network = true, WeatherService? service = null)
    {
        this.settings = settings; this.save = save; this.network = network; weatherService = service ?? new(); dispatcher = Dispatcher.CurrentDispatcher;
        cacheFile = Path.Combine(data, "weather-cache.json");
        try { if (File.Exists(cacheFile)) lastWeather = JsonSerializer.Deserialize<WeatherSnapshot>(File.ReadAllText(cacheFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        clockTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) }; clockTimer.Tick += (_, _) => RefreshTime();
        weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) }; weatherTimer.Tick += (_, _) => _ = RefreshWeather();
        foregroundEvent = (_, _, _, _, _, _, _) =>
        {
            if (disposed || restackPending) return; restackPending = true;
            dispatcher.BeginInvoke(() => { restackPending = false; if (!disposed) Restack(); }, DispatcherPriority.Background);
        };
        foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundEvent, 0, 0, 0);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }
    public void Apply()
    {
        if (disposed) return;
        var options = settings.Widgets;
        var next = (options.Clock, options.Weather, options.Light, options.City);
        if (configuration == next) return;
        configuration = next; weatherGeneration++; fetching = false; cancellation.Cancel(); cancellation.Dispose(); cancellation = new();
        foreach (var window in windows.Values) window.Close(); windows.Clear();
        clockText = clockDate = weatherCity = temperature = weatherDescription = weatherDetail = weatherStatus = weatherGlyph = null;
        Brush ink = LiquidGlass.Ink;
        if (options.Clock)
        {
            var window = Create("clock", "本地时间", 158);
            clockText = FeatureUi.Text("", 38, ink); clockText.FontWeight = FontWeights.Medium;
            Typography.SetNumeralAlignment(clockText, FontNumeralAlignment.Tabular);
            clockDate = FeatureUi.Text("", 12, LiquidGlass.Muted); clockDate.Margin = new(0, 7, 0, 0);
            window.Body.Children.Add(clockText); window.Body.Children.Add(clockDate);
        }
        if (options.Weather)
        {
            var window = Create("weather", "天气", 228);
            weatherCity = FeatureUi.Text(options.City, 16, ink); weatherCity.FontWeight = FontWeights.SemiBold; window.Body.Children.Add(weatherCity);
            var headline = new DockPanel(); weatherGlyph = FeatureUi.Text("☁", 38, new SolidColorBrush(Color.FromRgb(241, 168, 55))); weatherGlyph.Margin = new(5, 0, 5, 0);
            DockPanel.SetDock(weatherGlyph, Dock.Right); headline.Children.Add(weatherGlyph);
            temperature = FeatureUi.Text("—°", 43, ink); headline.Children.Add(temperature); window.Body.Children.Add(headline);
            weatherDescription = FeatureUi.Text("正在获取天气", 12, ink); window.Body.Children.Add(weatherDescription);
            weatherDetail = FeatureUi.Text("", 10, LiquidGlass.Muted); weatherDetail.Margin = new(0, 6, 0, 0); window.Body.Children.Add(weatherDetail);
            var footer = new DockPanel { Margin = new(0, 6, 0, 0) };
            var retry = SmallButton("↻", () => _ = RefreshWeather()); retry.ToolTip = "刷新天气"; DockPanel.SetDock(retry, Dock.Right); footer.Children.Add(retry);
            var credit = SmallButton("Open-Meteo", () => Process.Start(new ProcessStartInfo("https://open-meteo.com/") { UseShellExecute = true }));
            credit.FontSize = 8; credit.Padding = new(0); credit.Background = Brushes.Transparent; credit.Foreground = LiquidGlass.Muted; credit.ToolTip = "天气数据来源 · Open-Meteo";
            DockPanel.SetDock(credit, Dock.Right); footer.Children.Add(credit);
            weatherStatus = FeatureUi.Text("", 9, LiquidGlass.Muted); weatherStatus.ToolTip = "天气数据：Open-Meteo · 每 30 分钟更新"; footer.Children.Add(weatherStatus); window.Body.Children.Add(footer);
            if (lastWeather?.City == options.City) DisplayWeather(lastWeather, stale: weatherFailed || !WeatherFresh());
        }
        date = DateOnly.MinValue; RefreshTime();
        foreach (var window in windows.Values) { Place(window); window.Show(); }
        Restack();
        if (options.Clock) clockTimer.Start(); else clockTimer.Stop();
        if (options.Weather) { weatherTimer.Start(); if (!WeatherFresh()) _ = RefreshWeather(); } else weatherTimer.Stop();
    }
    private static Button SmallButton(string text, Action action) { var button = LiquidGlass.Button(text, action); button.Padding = new(5, 2, 5, 2); button.FontSize = 10; return button; }
    private WidgetWindow Create(string kind, string title, double height)
    {
        var window = new WidgetWindow(kind, title, height, settings.Widgets.Light, () => Remove(kind), Moved);
        windows.Add(kind, window); return window;
    }
    internal void Remove(string kind)
    {
        if (kind == "clock") settings.Widgets.Clock = false;
        else if (kind == "weather") settings.Widgets.Weather = false;
        save(); Apply();
    }
    private void DisplayChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(() => { if (!disposed) { foreach (var window in windows.Values) Place(window); Restack(); } });
    private void Place(WidgetWindow window)
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        double dpi = Native.GetDpiForWindow(new WindowInteropHelper(window).EnsureHandle()) / 96d;
        var position = settings.Widgets.Positions.GetValueOrDefault(window.Kind)
            ?? new WidgetPosition(window.Kind == "weather" ? 300 : 22, 22);
        window.Left = work.Left / dpi + Math.Clamp(position.X, 0, Math.Max(0, work.Width / dpi - window.Width));
        window.Top = work.Top / dpi + Math.Clamp(position.Y, 0, Math.Max(0, work.Height / dpi - window.Height));
    }
    private void Moved(WidgetWindow window)
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea; double dpi = Native.GetDpiForWindow(window.Handle) / 96d;
        settings.Widgets.Positions[window.Kind] = new(Math.Clamp(window.Left - work.Left / dpi, 0, Math.Max(0, work.Width / dpi - window.Width)),
            Math.Clamp(window.Top - work.Top / dpi, 0, Math.Max(0, work.Height / dpi - window.Height)));
        Place(window); save(); Restack();
    }
    internal void RememberPositionForChecks(WidgetWindow window) => Moved(window);
    internal void RefreshTime()
    {
        var now = DateTime.Now;
        if (clockText is not null) clockText.Text = now.ToString("HH:mm:ss");
        var today = DateOnly.FromDateTime(now);
        if (date != today && clockDate is not null) { date = today; clockDate.Text = now.ToString("M月d日  dddd", CultureInfo.GetCultureInfo("zh-CN")); }
    }
    internal async Task RefreshWeather()
    {
        if (fetching || disposed || !settings.Widgets.Weather || weatherStatus is null) return;
        if (!network) { if (lastWeather is null) { weatherDescription!.Text = "预览模式 · 天气未联网"; weatherStatus.Text = "Open-Meteo"; } return; }
        fetching = true; int version = weatherGeneration; string city = settings.Widgets.City; var token = cancellation.Token;
        weatherStatus.Text = "正在更新 · Open-Meteo";
        try
        {
            var result = await weatherService.Fetch(city, token);
            if (disposed || version != weatherGeneration) return;
            lastWeather = result; weatherFailed = false; DisplayWeather(result, stale: false);
            try { string temporary = cacheFile + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(result)); File.Move(temporary, cacheFile, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (disposed || version != weatherGeneration) return;
            weatherFailed = true;
            if (lastWeather?.City == city) DisplayWeather(lastWeather, stale: true);
            else { temperature!.Text = "—°"; weatherDescription!.Text = "天气暂不可用"; weatherStatus.Text = "检查网络或城市 · 点 ↻ 重试"; }
        }
        finally { if (version == weatherGeneration) fetching = false; }
    }
    private bool WeatherFresh() => lastWeather?.City == settings.Widgets.City
        && DateTimeOffset.UtcNow - lastWeather.FetchedAt >= TimeSpan.Zero
        && DateTimeOffset.UtcNow - lastWeather.FetchedAt < TimeSpan.FromMinutes(30);
    private void DisplayWeather(WeatherSnapshot value, bool stale)
    {
        if (temperature is null) return;
        weatherCity!.Text = value.Location ?? value.City; weatherCity.TextTrimming = TextTrimming.CharacterEllipsis; temperature.Text = $"{value.Temperature:0}°"; weatherGlyph!.Text = value.Glyph;
        weatherDescription!.Text = $"{value.Description}  ·  最高 {value.High:0}°  最低 {value.Low:0}°";
        weatherDetail!.Text = $"体感 {value.FeelsLike:0}°   湿度 {value.Humidity}%   风 {value.Wind:0} km/h";
        var localTime = value.FetchedAt.ToLocalTime();
        string updated = localTime.Date == DateTime.Today ? localTime.ToString("HH:mm") : localTime.ToString("M/d HH:mm");
        weatherStatus!.Text = $"{(stale ? "上次更新" : "更新于")} {updated}";
        weatherStatus.TextTrimming = TextTrimming.CharacterEllipsis;
    }
    private void Restack()
    {
        if (disposed || windows.Count == 0) return;
        nint after = 0;
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint pid);
            bool ownDialog = pid == Environment.ProcessId && (Native.GetWindowLongPtr(window, -16).ToInt64() & 0x00C00000) != 0;
            if (!Native.IsWindowVisible(window) || Native.IsIconic(window)
                || !ownDialog && (Native.GetWindow(window, 4) != 0 || !WindowClose.CanClose(window))) return true;
            long style = Native.GetWindowLongPtr(window, -20).ToInt64();
            if ((style & (0x08000000 | 8)) != 0 || !ownDialog && (style & 0x80) != 0) return true;
            if (Native.DwmGetIntAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowRect(window, out var frame);
            if (frame.Right - frame.Left >= 160 && frame.Bottom - frame.Top >= 100) after = window;
            return true;
        }, 0);
        foreach (var widget in windows.Values)
        {
            if (Native.IsIconic(widget.Handle)) Native.ShowWindow(widget.Handle, 4);
            Native.SetWindowPos(widget.Handle, after, 0, 0, 0, 0, 0x0413);
        }
    }
    internal void RestackForChecks() => Restack();
    public void Dispose()
    {
        if (disposed) return; disposed = true; weatherGeneration++;
        clockTimer.Stop(); weatherTimer.Stop(); cancellation.Cancel(); cancellation.Dispose(); weatherService.Dispose();
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        foreach (var window in windows.Values) window.Close(); windows.Clear();
    }
}
