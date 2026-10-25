using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DesktopDock.App;

internal sealed record TaskbarRecoveryState(TaskbarSnapshot Snapshot, bool WorkChanged, uint GlassProcess = 0, long GlassStart = 0);

internal static class TaskbarRecovery
{
    private const string FileName = "taskbar-state.json";
    public static string Journal(string data) => Path.Combine(data, FileName);
    public static void Save(string data, TaskbarRecoveryState state)
    {
        Directory.CreateDirectory(data);
        string file = Journal(data);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state));
        File.Move(file + ".tmp", file, true);
    }
    public static bool Restore(string data)
    {
        string file = Journal(data);
        if (!File.Exists(file)) return true;
        try
        {
            var state = JsonSerializer.Deserialize<TaskbarRecoveryState>(File.ReadAllText(file));
            if (state is null || !state.Snapshot.IsTop) return false;
            var saved = state.Snapshot;
            GlassCompanion.StopOwned(state.GlassProcess, state.GlassStart, data);
            GlassCompanion.StopInDirectory(data); // Also covers parent death immediately after Process.Start, before PID journaling.
            var current = TaskbarNative.Capture();
            if (current is null) return false; // Explorer not ready: keep the recovery record.
            if (TaskbarNative.Matches(saved)) Native.ShowWindow((nint)saved.Handle, saved.WasVisible ? 8 : 0);
            if (state.WorkChanged && current.Monitor == saved.Monitor && current.Work == saved.ExpandedWork)
            {
                if (!TaskbarNative.SetWork(saved.Work)) return false;
            }
            // An external change to the work area wins over our old snapshot.
            if (TaskbarNative.Matches(saved) && Native.IsWindowVisible((nint)saved.Handle) != saved.WasVisible) return false;
            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    public static Process StartGuard(string data, EventWaitHandle stop, string eventName)
    {
        using var current = Process.GetCurrentProcess();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName + ".Ready");
        var info = SelfProcess.Create("--taskbar-guard", "--data-dir", data, "--parent-id", current.Id.ToString(),
            "--parent-start", current.StartTime.ToUniversalTime().Ticks.ToString(), "--guard-event", eventName);
        var process = Process.Start(info) ?? throw new InvalidOperationException("恢复守护进程无法启动。");
        if (!ready.WaitOne(5000)) { stop.Set(); process.Dispose(); throw new InvalidOperationException("恢复守护进程未就绪。"); }
        return process;
    }
    public static int RunGuard(string[] args, string data)
    {
        string? Value(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string? name = Value("--guard-event");
        if (name is null || !name.StartsWith("Local\\DesktopDock.TaskbarGuard.", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["Local\\DesktopDock.TaskbarGuard.".Length..], "N", out _)
            || !int.TryParse(Value("--parent-id"), out int id) || !long.TryParse(Value("--parent-start"), out long start)) return 2;
        try
        {
            using var stop = EventWaitHandle.OpenExisting(name);
            using var ready = EventWaitHandle.OpenExisting(name + ".Ready");
            if (!TaskbarNative.SameProcess((uint)id, start)) { Restore(data); return 0; }
            using var parent = Process.GetProcessById(id);
            using var ended = new EventWaitHandle(false, EventResetMode.ManualReset);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => ended.Set();
            if (parent.HasExited) ended.Set();
            ready.Set();
            if (WaitHandle.WaitAny([stop, ended]) == 1) Restore(data);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or WaitHandleCannotBeOpenedException)
        { Restore(data); }
        return 0;
    }
}

internal static class SelfProcess
{
    public static ProcessStartInfo Create(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string value in args) info.ArgumentList.Add(value);
        return info;
    }
}
