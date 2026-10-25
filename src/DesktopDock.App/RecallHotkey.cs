namespace DesktopDock.App;

internal sealed class RecallHotkey : IDisposable
{
    public const int Id = 0x51D;
    private readonly nint hwnd;
    public string Label { get; } = "未注册，可从托盘召回";
    public bool Registered { get; }
    public RecallHotkey(nint hwnd)
    {
        this.hwnd = hwnd;
        foreach (char key in new[] { 'D', 'J', 'K', 'U' })
        {
            if (!Native.RegisterHotKey(hwnd, Id, 0x4000 | 1 | 2 | 4, key)) continue;
            Registered = true; Label = "Ctrl+Alt+Shift+" + key; break;
        }
    }
    public void Dispose() { if (Registered) Native.UnregisterHotKey(hwnd, Id); }
}
