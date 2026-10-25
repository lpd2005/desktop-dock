using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopDock.App;

/// <summary>Runs an unmodified, separately licensed portable utility. Never attaches to an existing instance.</summary>
internal sealed class GlassCompanion(string data) : IDisposable
{
    private Process? process;
    private string? lastConfig;
    public string Status { get; private set; } = "玻璃效果未启用";
    public uint ProcessId => process is { HasExited: false } ? (uint)process.Id : 0;
    public long ProcessStart => process is { HasExited: false } ? process.StartTime.ToUniversalTime().Ticks : 0;
    public bool IsRunning => ProcessId != 0;
    public void Apply(bool enabled, bool light)
    {
        if (!enabled) { Dispose(); Status = "玻璃效果未启用"; return; }
        try
        {
            string directory = Path.Combine(data, "taskbar-glass");
            string config = JsonSerializer.Serialize(new
            {
                desktop_appearance = new { accent = "acrylic", color = light ? "#DDE7F066" : "#18213188", show_peek = false, show_line = false, blur_radius = 18.0 },
                visible_window_appearance = new { enabled = false },
                maximized_window_appearance = new { enabled = false },
                start_opened_appearance = new { enabled = false },
                search_opened_appearance = new { enabled = false },
                task_view_opened_appearance = new { enabled = true, accent = "normal" },
                battery_saver_appearance = new { enabled = true, accent = "normal" },
                hide_tray = true, disable_saving = true, verbosity = "off"
            });
            if (IsRunning)
            {
                if (lastConfig != config) File.WriteAllText(Path.Combine(directory, "settings.json"), config);
                lastConfig = config;
                return;
            }
            if (process is not null) { Status = "玻璃组件已退出；关闭后重新启用可重试"; return; }
            if (Native.FindWindow("TrayWindow", "TranslucentTB") != 0 || AnotherInstance())
            { Status = "已有 TranslucentTB 正在运行，玻璃效果由它管理"; return; }
            string source = Path.Combine(AppContext.BaseDirectory, "components", "TranslucentTB");
            string manifest = Path.Combine(source, "component.json");
            if (!File.Exists(manifest)) { Status = "缺少玻璃组件，请使用完整运行包"; return; }
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest)) ?? throw new InvalidDataException();
            if (!files.ContainsKey("TranslucentTB.exe")) throw new InvalidDataException();
            Directory.CreateDirectory(directory);
            foreach (var (name, hash) in files)
            {
                if (Path.GetFileName(name) != name || name is "." or "..") throw new InvalidDataException();
                string from = Path.Combine(source, name), to = Path.Combine(directory, name);
                using var stream = File.OpenRead(from);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                // Do not overwrite a DLL still loaded by Explorer from a previous session.
                if (!File.Exists(to) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(to))).Equals(hash, StringComparison.OrdinalIgnoreCase)) File.Copy(from, to, true);
            }
            File.WriteAllText(Path.Combine(directory, "settings.json"), config);
            lastConfig = config;
            process = Process.Start(new ProcessStartInfo(Path.Combine(directory, "TranslucentTB.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory });
            Status = IsRunning ? "玻璃组件运行中 · TranslucentTB 2026.2" : "玻璃组件未能启动";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
        { Status = "玻璃组件不可用，保留原生外观"; }
    }
    public void RefreshStatus()
    {
        if (process is { HasExited: true }) Status = "玻璃组件已退出；关闭后重新启用可重试";
    }
    private static bool AnotherInstance()
    {
        var instances = Process.GetProcessesByName("TranslucentTB");
        foreach (var instance in instances) instance.Dispose();
        return instances.Length != 0;
    }
    public static void StopInDirectory(string data)
    {
        foreach (var instance in Process.GetProcessesByName("TranslucentTB"))
            using (instance) StopOwned((uint)instance.Id, TaskbarNative.ProcessStart((uint)instance.Id), data);
    }
    public static void StopOwned(uint id, long start, string data)
    {
        if (id == 0 || !TaskbarNative.SameProcess(id, start)) return;
        try
        {
            using var p = Process.GetProcessById((int)id);
            string expected = Path.GetFullPath(Path.Combine(data, "taskbar-glass", "TranslucentTB.exe"));
            if (!string.Equals(p.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return;
            Native.EnumWindows((hwnd, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == id && TaskbarNative.Class(hwnd) == "TrayWindow") Native.PostMessage(hwnd, 0x0010, 0, 0);
                return true;
            }, 0);
            if (!p.WaitForExit(1500)) { p.Kill(); p.WaitForExit(1500); }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        Status = "玻璃效果未启用";
        if (process is null) return;
        StopOwned(ProcessId, ProcessStart, data);
        process.Dispose(); process = null; lastConfig = null;
    }
}
