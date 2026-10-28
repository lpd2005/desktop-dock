using System.Runtime.InteropServices;
using DesktopDock.Core;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

internal readonly record struct PreviewTarget(nint Window, uint Process)
{
    public static PreviewTarget Capture(nint window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        return new(window, pid);
    }
    public bool Valid
    {
        get
        {
            if (!WindowClose.CanClose(Window) || !Native.IsWindowVisible(Window)) return false;
            Native.GetWindowThreadProcessId(Window, out uint current);
            return current == Process && (Native.DwmGetIntAttribute(Window, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0);
        }
    }
}

// DWM draws into a small, non-layered top-level HWND. Keeping it separate from
// the alpha WPF card avoids layered-window thumbnail rendering restrictions.
internal sealed class DwmPreviewSurface : Forms.NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Properties
    {
        public uint Flags;
        public Native.Rect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref Properties properties);
    private readonly Action activate;
    private readonly Action<int> wheel;
    private nint thumbnail;
    private int width, height;
    private int pressX, pressY;
    private double dpi = 1;
    private bool pressed;
    public PreviewTarget Target { get; }
    public bool Available => thumbnail != 0;

    public DwmPreviewSurface(nint owner, PreviewTarget target, Action activate, Action<int> wheel)
    {
        Target = target; this.activate = activate; this.wheel = wheel;
        CreateHandle(new Forms.CreateParams
        {
            Caption = "Desktop Dock · Window thumbnail", Width = 1, Height = 1,
            Style = unchecked((int)0x80000000), ExStyle = 0x08000000 | 0x80 | 8, Parent = owner
        });
        if (target.Valid && DwmRegisterThumbnail(Handle, target.Window, out var registered) == 0) thumbnail = registered;
    }
    public void Place(int left, int top, int width, int height, double scale)
    {
        this.width = width; this.height = height;
        dpi = scale;
        Native.SetWindowPos(Handle, new(-1), left, top, width, height, 0x0010);
        nint region = Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(12 * scale), (int)(12 * scale));
        if (region != 0 && Native.SetWindowRgn(Handle, region, false) == 0) Native.DeleteObject(region);
    }
    public void Present(byte opacity)
    {
        if (!Available || width <= 0 || height <= 0) return;
        if (DwmQueryThumbnailSourceSize(thumbnail, out var size) != 0) return;
        var fit = WindowPreviewLayout.Fit(size.Width, size.Height, width, height);
        var properties = new Properties
        {
            Flags = 1 | 4 | 8 | 16, Opacity = opacity, Visible = true, ClientOnly = false,
            Destination = new() { Left = (int)Math.Round(fit.X), Top = (int)Math.Round(fit.Y),
                Right = (int)Math.Round(fit.X + fit.Width), Bottom = (int)Math.Round(fit.Y + fit.Height) }
        };
        if (DwmUpdateThumbnailProperties(thumbnail, ref properties) != 0) return;
        if (!Native.IsWindowVisible(Handle)) Native.ShowWindow(Handle, 8);
    }
    protected override void WndProc(ref Forms.Message message)
    {
        switch (message.Msg)
        {
            case 0x0021: message.Result = new(3); return;
            case 0x0014: message.Result = new(1); return;
            case 0x0020: Native.SetCursor(Native.LoadCursor(0, new(32649))); message.Result = new(1); return;
            case 0x0201:
                pressX = unchecked((short)message.LParam.ToInt64()); pressY = unchecked((short)(message.LParam.ToInt64() >> 16));
                pressed = true; Native.SetCapture(Handle); return;
            case 0x0202:
                int x = unchecked((short)message.LParam.ToInt64()), y = unchecked((short)(message.LParam.ToInt64() >> 16));
                bool select = pressed && x >= 0 && x < width && y >= 0 && y < height && !DockReorder.Moved((x - pressX) / dpi, (y - pressY) / dpi);
                pressed = false;
                if (Native.GetCapture() == Handle) Native.ReleaseCapture();
                if (select) activate();
                return;
            case 0x0215:
            case 0x001F: pressed = false; break;
            case 0x020A: wheel(unchecked((short)(message.WParam.ToInt64() >> 16))); return;
        }
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (Handle != 0 && Native.GetCapture() == Handle) Native.ReleaseCapture();
        if (thumbnail != 0) { DwmUnregisterThumbnail(thumbnail); thumbnail = 0; }
        if (Handle != 0) DestroyHandle();
    }
}
