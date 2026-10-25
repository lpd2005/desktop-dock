using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

// Separate process: same-thread HTTRANSPARENT must not make a broken regression pass.
internal static class RegressionProbe
{
    public static int Run(string mode, string data)
    {
        var app = new System.Windows.Application();
        var screen = Forms.Screen.PrimaryScreen!;
        var window = new Window { Title = "Desktop Dock · 回归检查", Background = new SolidColorBrush(Color.FromRgb(230, 235, 244)),
            WindowStartupLocation = WindowStartupLocation.Manual, Width = 1000, Height = 700, ShowInTaskbar = true };
        void ApplyMode(string current)
        {
            double dpi = Native.GetDpiForWindow(new WindowInteropHelper(window).Handle) / 96d;
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.ResizeMode = ResizeMode.CanResize;
            window.Left = (screen.WorkingArea.Left + 30) / dpi;
            window.Top = (screen.WorkingArea.Top + 40) / dpi;
            window.Width = (screen.WorkingArea.Width - 60) / dpi;
            window.Height = (screen.WorkingArea.Height - 45) / dpi;
            if (current == "maximized") window.WindowState = WindowState.Maximized;
            if (current == "fullscreen")
            {
                window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
                window.Left = screen.Bounds.Left / dpi; window.Top = screen.Bounds.Top / dpi;
                window.Width = screen.Bounds.Width / dpi; window.Height = screen.Bounds.Height / dpi;
            }
            if (window.IsVisible) window.Activate();
        }
        window.SourceInitialized += (_, _) => ApplyMode(mode);
        var grid = new Grid { Margin = new(24) };
        var caption = new TextBlock { Text = mode == "normal" ? "Dock 交互回归检查" : mode == "maximized" ? "最大化避让检查" : "全屏避让检查",
            FontSize = 30, Foreground = new SolidColorBrush(Color.FromRgb(37, 51, 75)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        grid.Children.Add(caption);
        var row = new UniformGrid { Columns = 12, Height = 60, VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < 12; i++)
        {
            var button = new Button { Content = "点击检查 " + (i + 1), Margin = new(3) };
            button.Click += (_, _) => { button.Content = "已收到点击 ✓"; File.WriteAllText(Path.Combine(data, "clicked.json"), "{\"Clicked\":true}"); };
            row.Children.Add(button);
        }
        grid.Children.Add(row); window.Content = grid;
        string appliedMode = mode;
        window.Loaded += (_, _) =>
        {
            window.Activate();
            WriteShared(Path.Combine(data, "ready.json"), JsonSerializer.Serialize(new { Mode = appliedMode, Ready = true, WindowHandle = (long)new WindowInteropHelper(window).Handle }));
        };
        var modeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        modeTimer.Tick += (_, _) =>
        {
            string file = Path.Combine(data, "mode.txt");
            if (!File.Exists(file)) return;
            string requested = ReadShared(file).Trim();
            if (requested == appliedMode || requested is not ("normal" or "maximized" or "fullscreen")) return;
            ApplyMode(requested); appliedMode = requested;
            WriteShared(Path.Combine(data, "ready.json"), JsonSerializer.Serialize(new { Mode = appliedMode, Ready = true, WindowHandle = (long)new WindowInteropHelper(window).Handle }));
        };
        modeTimer.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timer.Tick += (_, _) => { timer.Stop(); modeTimer.Stop(); window.Close(); };
        timer.Start();
        return app.Run(window);
    }

    public static Process Start(string mode, string data)
    {
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--probe-window"); start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data);
        return Process.Start(start)!;
    }

    public static async Task WaitReady(Process process, string data, string? expectedMode = null)
    {
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(Path.Combine(data, "ready.json")))
            {
                try
                {
                    using var state = JsonDocument.Parse(ReadShared(Path.Combine(data, "ready.json")));
                    if (expectedMode is null || state.RootElement.GetProperty("Mode").GetString() == expectedMode) { await Task.Delay(300); return; }
                }
                catch (JsonException) { }
            }
            if (process.HasExited) throw new InvalidOperationException("Regression probe exited before initialization.");
            await Task.Delay(50);
        }
        throw new TimeoutException("Regression probe did not become ready.");
    }

    public static async Task SetMode(Process process, string data, string mode)
    {
        WriteShared(Path.Combine(data, "mode.txt"), mode);
        await WaitReady(process, data, mode);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteShared(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    public static void Close(Process process)
    {
        if (process.HasExited) return;
        process.Refresh();
        if (process.MainWindowHandle != 0) Native.PostMessage(process.MainWindowHandle, 0x0010, 0, 0);
    }
}
