using DesktopDock.Core;

internal static class SettingsStoreChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopDock-settings-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "settings.json"), backup = path + ".bak";
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rejected = true; }
            check(rejected, name);
        }
        try
        {
            check(SettingsStore.Read(path).State == SettingsLoadState.Missing, "Missing settings are a genuine first run");
            SettingsStore.Save(path, new() { IconSize = 40, Items = [new("Example", "example.exe")] });
            check(SettingsStore.Load(path).IconSize == 40 && SettingsStore.Load(backup).Items.Count == 1, "First save creates a valid primary and backup");
            SettingsStore.Save(path, new() { IconSize = 56 });
            check(SettingsStore.Load(path).IconSize == 56 && SettingsStore.Load(backup).IconSize == 40, "Atomic replacement retains previous valid settings");
            string previousBackup = File.ReadAllText(backup);
            File.WriteAllText(path, "{broken-primary");
            var recovered = SettingsStore.Read(path);
            check(recovered.State == SettingsLoadState.BackupAvailable && recovered.NeedsRecovery && recovered.Settings.IconSize == 40, "Damaged settings load backup with explicit recovery state");
            check(File.ReadAllText(path) == "{broken-primary" && File.ReadAllText(backup) == previousBackup, "Loading does not rewrite damaged files or backups");
            Reject(() => SettingsStore.Save(path, new()), "Ordinary save requires recovery acknowledgement");
            check(File.ReadAllText(path) == "{broken-primary" && File.ReadAllText(backup) == previousBackup, "Rejected save preserves both files");
            SettingsStore.Recover(path, recovered.Settings);
            check(SettingsStore.Read(path).State == SettingsLoadState.Loaded && SettingsStore.Load(path).IconSize == 40, "Explicit recovery restores backup settings");
            check(Directory.GetFiles(root, "settings.json.corrupt-*").Any(x => File.ReadAllText(x) == "{broken-primary"), "Recovery archives the original damaged bytes");
            check(File.ReadAllText(backup) == previousBackup, "Recovering an invalid primary never replaces the valid backup with it");
            Reject(() => SettingsStore.Recover(path, new()), "Recovery refuses a primary that has become valid");

            File.Delete(path);
            check(SettingsStore.Read(path).State == SettingsLoadState.BackupAvailable, "Missing primary with valid backup is not a first run");
            Reject(() => SettingsStore.Save(path, new()), "Missing primary cannot silently overwrite its only backup");
            SettingsStore.Recover(path, recovered.Settings);
            check(SettingsStore.Load(path).IconSize == 40 && File.ReadAllText(backup) == previousBackup, "Missing primary can be explicitly restored without changing backup");

            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                check(SettingsStore.Read(path).NeedsRecovery, "Unavailable primary is distinguished from a first run");
                Reject(() => SettingsStore.Save(path, new()), "Locked primary rejects a save");
                Reject(() => SettingsStore.Recover(path, new()), "Locked primary rejects recovery before writing");
                check(File.ReadAllText(backup) == previousBackup, "Locked primary does not damage its backup");
            }
            string primary = File.ReadAllText(path);
            using (var held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => SettingsStore.Save(path, new() { IconSize = 64 }), "Locked backup rejects replacement");
            check(File.ReadAllText(path) == primary && File.ReadAllText(backup) == previousBackup, "Failed replacement leaves the last valid configurations intact");
            check(!Directory.GetFiles(root, "*.tmp").Any(), "Failed replacement cleans its temporary file");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                Reject(() => SettingsStore.Save(path, new() { IconSize = 64 }), "Read-only primary rejects a save");
                check(File.ReadAllText(path) == primary && File.ReadAllText(backup) == previousBackup, "Read-only failure preserves primary and backup");
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            SettingsStore.Save(path, new() { IconSize = 64 });
            check(SettingsStore.Load(path).IconSize == 64 && SettingsStore.Load(backup).IconSize == 40, "Save retries successfully after a lock is released");

            File.WriteAllText(path, "{bad-primary-again"); File.WriteAllText(backup, "{bad-backup");
            check(SettingsStore.Read(path).State == SettingsLoadState.RecoveryRequired, "Two damaged files require explicit default recovery");
            SettingsStore.Recover(path, new() { IconSize = 60 });
            check(SettingsStore.Load(path).IconSize == 60 && SettingsStore.Load(backup).IconSize == 60, "Default recovery creates two valid configurations");
            check(Directory.GetFiles(root, "settings.json.corrupt-*").Any(x => File.ReadAllText(x) == "{bad-primary-again")
                && Directory.GetFiles(root, "settings.json.bak.corrupt-*").Any(x => File.ReadAllText(x) == "{bad-backup"), "Default recovery preserves both damaged originals");

            foreach (string invalid in new[] { "", "null", "[]", "{\"IconSize\":\"bad\"}" })
            {
                File.WriteAllText(path, invalid);
                check(SettingsStore.Read(path).State == SettingsLoadState.BackupAvailable, "Invalid JSON shape uses backup: " + invalid.Length);
                Reject(() => SettingsStore.Save(path, new()), "Invalid JSON shape is not silently overwritten: " + invalid.Length);
                SettingsStore.Recover(path, SettingsStore.Read(path).Settings);
                check(Directory.GetFiles(root, "settings.json.corrupt-*").Any(x => File.ReadAllText(x) == invalid), "Invalid original remains recoverable: " + invalid.Length);
            }
            File.WriteAllText(path, "{\"Items\":null,\"Widgets\":null,\"DesktopGroups\":null}");
            check(SettingsStore.Read(path).State == SettingsLoadState.Loaded && SettingsStore.Load(path).Items.Count == 0, "Existing partial settings still normalize without a schema migration");
        }
        finally
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe check cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
