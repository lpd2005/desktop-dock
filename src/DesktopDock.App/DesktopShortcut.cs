using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopDock.App;

// Receives OS hotkey messages on the existing Dock HWND, even while that window is hidden.
// No keyboard hook, polling timer, input injection, or Windows-key reservation is required.
internal sealed class DesktopShortcut : IDisposable
{
    internal const int Message = 0x0312, Id = 0x4444;
    internal const uint Modifiers = 0x0001 | 0x0002 | 0x0004, Key = 0x44;
    internal static nint Payload => new((long)(Key << 16 | Modifiers));
    public const string Keys = "Ctrl + Alt + Shift + D";
    private readonly HwndSource source;
    private readonly Func<bool> minimize;
    private readonly bool runtimeAllowed;
    private bool enabled, suspended, disposed;
    private string status = "返回桌面快捷键已关闭";
    public bool Registered { get; private set; }
    public string Status => status;
    public event Action? StatusChanged;

    public DesktopShortcut(HwndSource source, Func<bool> minimize, bool runtimeAllowed)
    {
        this.source = source; this.minimize = minimize; this.runtimeAllowed = runtimeAllowed;
        source.AddHook(HandleMessage);
    }

    public void Apply(bool value)
    {
        if (disposed) return;
        enabled = value;
        if (!value || !runtimeAllowed)
        {
            if (Registered) Native.UnregisterHotKey(source.Handle, Id);
            Registered = false;
            SetStatus(value ? "安全预览不注册全局快捷键" : "返回桌面快捷键已关闭");
            return;
        }
        if (!Registered) Registered = Native.RegisterHotKey(source.Handle, Id, Modifiers | 0x4000, Key); // MOD_NOREPEAT
        SetStatus(!Registered ? "快捷键注册失败，可能已被其他程序占用；关闭再开启可重试"
            : suspended ? "当前会话暂停，返回桌面快捷键暂不可用" : Keys + " · 已启用");
    }

    public void SetSuspended(bool value) { suspended = value; Apply(enabled); }

    private nint HandleMessage(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message != Message || wparam != Id || lparam != Payload) return 0;
        handled = true;
        if (disposed || !Registered || !enabled || suspended) return 0;
        SetStatus(minimize() ? Keys + " · 已启用" : "暂时无法返回桌面，请确认 Windows 资源管理器正常后重试");
        return 0;
    }

    private void SetStatus(string value)
    { if (status != value) { status = value; StatusChanged?.Invoke(); } }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Registered) Native.UnregisterHotKey(source.Handle, Id);
        Registered = false; source.RemoveHook(HandleMessage);
    }
}

internal static class ShellDesktop
{
    // MinimizeAll is deliberately one-way: repeated presses must never restore the windows.
    internal static bool MinimizeAll()
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"));
            if (type is null || (shell = Activator.CreateInstance(type)) is null) return false;
            ((dynamic)shell).MinimizeAll();
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        { return false; }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }
}
