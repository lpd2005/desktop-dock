using System.Text.Json;

namespace DesktopDock.Core;

public enum SettingsLoadState { Missing, Loaded, BackupAvailable, RecoveryRequired }

public sealed record SettingsReadResult(DockSettings Settings, SettingsLoadState State)
{
    public bool NeedsRecovery => State is SettingsLoadState.BackupAvailable or SettingsLoadState.RecoveryRequired;
}

public sealed class SettingsRecoveryRequiredException() : IOException("原配置无法正常读取，请先在设置中确认恢复。");

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private sealed record Contents(DockSettings? Settings, byte[]? Bytes, bool Missing);

    public static DockSettings Load(string path) => Read(path).Settings;

    public static SettingsReadResult Read(string path)
    {
        var primary = ReadFile(path, tolerateAccessFailure: true);
        if (primary.Settings is not null) return new(primary.Settings, SettingsLoadState.Loaded);
        var backup = ReadFile(path + ".bak", tolerateAccessFailure: true);
        if (backup.Settings is not null) return new(backup.Settings, SettingsLoadState.BackupAvailable);
        return new(new(), primary.Missing && backup.Missing ? SettingsLoadState.Missing : SettingsLoadState.RecoveryRequired);
    }

    public static void Save(string path, DockSettings settings)
    {
        path = Path.GetFullPath(path);
        var current = ReadFile(path);
        if (current.Settings is null)
        {
            if (!current.Missing || !ReadFile(path + ".bak").Missing) throw new SettingsRecoveryRequiredException();
            var bytes = Serialize(settings);
            // Keep a valid initial backup even if creating the primary file fails.
            WriteAtomic(path + ".bak", bytes, replace: false);
            WriteAtomic(path, bytes, replace: false);
        }
        else WriteAtomic(path, Serialize(settings), replace: true, backup: path + ".bak");
    }

    // Explicit recovery preserves unreadable originals before replacing anything.
    public static void Recover(string path, DockSettings settings)
    {
        path = Path.GetFullPath(path);
        var current = ReadFile(path);
        if (current.Settings is not null) throw new IOException("原配置已发生变化，请重新启动软件后再检查。");
        var backup = ReadFile(path + ".bak");
        var bytes = Serialize(settings);
        if (current.Bytes is not null) Archive(path, current.Bytes);
        if (backup.Settings is null)
        {
            if (backup.Bytes is not null) Archive(path + ".bak", backup.Bytes);
            WriteAtomic(path + ".bak", bytes, replace: !backup.Missing);
        }
        // A valid older backup is retained; an invalid primary must never replace it.
        WriteAtomic(path, bytes, replace: !current.Missing);
    }

    private static byte[] Serialize(DockSettings settings)
    {
        settings.Normalize();
        return JsonSerializer.SerializeToUtf8Bytes(settings, Options);
    }

    private static Contents ReadFile(string path, bool tolerateAccessFailure = false)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(null, null, true); }
        catch (Exception e) when (tolerateAccessFailure && e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(null, null, false); }
        try
        {
            var settings = JsonSerializer.Deserialize<DockSettings>(bytes, Options);
            if (settings is null) return new(null, bytes, false);
            settings.Normalize(); return new(settings, bytes, false);
        }
        catch (JsonException) { return new(null, bytes, false); }
    }

    private static void Archive(string path, byte[] bytes) => WriteAtomic(
        path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"), bytes, replace: false);

    private static void WriteAtomic(string path, byte[] bytes, bool replace, string? backup = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
            if (replace) File.Replace(temporary, path, backup);
            else File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
