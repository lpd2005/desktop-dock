using Forms = System.Windows.Forms;

namespace DesktopDock.App;

// Own HWND with no WPF HwndTarget: WPF rewrites layered styles as its render
// settings change. This window only hosts the independent composition tree.
internal sealed class DockPresentationWindow : Forms.NativeWindow, IDisposable
{
    public int PositionChanges { get; private set; }
    public DockPresentationWindow()
    {
        CreateHandle(new Forms.CreateParams
        {
            Caption = "Desktop Dock Visual", Width = 900, Height = 250,
            Style = unchecked((int)(0x80000000 | 0x08000000)),
            ExStyle = 0x00200000 | 0x08000000 | 0x80000 | 0x20 | 0x80 | 8
        });
        if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, 2))
        { DestroyHandle(); throw new System.ComponentModel.Win32Exception(); }
    }
    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg is 0x0047 or 0x0005 or 0x0018) PositionChanges++;
        if (message.Msg == 0x0084) { message.Result = new(-1); return; }
        if (message.Msg == 0x0014) { message.Result = new(1); return; }
        base.WndProc(ref message);
    }
    public void Dispose() { if (Handle != 0) DestroyHandle(); }
}
