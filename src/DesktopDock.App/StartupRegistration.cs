using System.IO;
using Microsoft.Win32;

namespace DesktopDock.App;

internal sealed class StartupRegistration(string executable, string keyPath = StartupRegistration.RunKey, string valueName = "DesktopDock")
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly (string? Command, string? Error) registration = Prepare(executable);
    public string? UnavailableReason => registration.Error;
    private string? CommandValue => registration.Command;
    private static (string?, string?) Prepare(string path)
    {
        try { return (Command(path), null); }
        catch (InvalidOperationException e) { return (null, e.Message); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, "当前程序路径不可用于开机自启，请安装到较短的有效目录后重试。"); }
    }
    internal static string Command(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Contains('"') || !Path.GetFileName(path).Equals("DesktopDock.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从 DesktopDock.exe 启动后设置开机自启。");
        string value = "\"" + path + "\" --startup";
        if (value.Length > 260) throw new InvalidOperationException("当前程序路径过长，请安装到较短的目录后启用自启。");
        return value;
    }
    public bool Enabled
    {
        get { if (CommandValue is null) return false; using var key = Registry.CurrentUser.OpenSubKey(keyPath); return string.Equals(key?.GetValue(valueName) as string, CommandValue, StringComparison.OrdinalIgnoreCase); }
    }
    public bool DisabledByWindows
    {
        get
        {
            if (keyPath != RunKey) return false;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return key?.GetValue(valueName) is byte[] { Length: > 0 } bytes && bytes[0] is 3 or 7;
        }
    }
    public void Set(bool enabled)
    {
        if (CommandValue is null) throw new InvalidOperationException(UnavailableReason);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (enabled) key.SetValue(valueName, CommandValue, RegistryValueKind.String);
        else if (string.Equals(key.GetValue(valueName) as string, CommandValue, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
