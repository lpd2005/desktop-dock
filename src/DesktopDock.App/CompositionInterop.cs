using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DesktopDock.App;

internal static unsafe class CompositionInterop
{
    private static nint Query(object instance, Guid iid)
    {
        nint unknown = ((IWinRTObject)instance).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint result));
        return result;
    }

    public static DesktopWindowTarget CreateTarget(Compositor compositor, nint hwnd)
    {
        nint interop = Query(compositor, new("29E691FA-4567-4DCA-B319-D0F207EB6807"));
        try
        {
            nint result = 0;
            var call = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(nint**)interop)[3];
            Marshal.ThrowExceptionForHR(call(interop, hwnd, 1, &result));
            try { return MarshalInterface<DesktopWindowTarget>.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally { Marshal.Release(interop); }
    }

    public static CompositionGraphicsDevice CreateGraphics(Compositor compositor, nint d2dDevice)
    {
        nint interop = Query(compositor, new("25297D5C-3AD4-4C9C-B5CF-E36A38512330"));
        try
        {
            nint result = 0;
            var call = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)interop)[5];
            Marshal.ThrowExceptionForHR(call(interop, d2dDevice, &result));
            try { return MarshalInterface<CompositionGraphicsDevice>.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally { Marshal.Release(interop); }
    }

    public static void Draw(CompositionDrawingSurface surface, Action<nint, Native.Point> draw)
    {
        nint interop = Query(surface, new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE"));
        try
        {
            Guid iid = new("E8F7FE7A-191C-466D-AD95-975678BDA998");
            nint context = 0;
            Native.Point offset;
            var begin = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, Native.Point*, int>)(*(nint**)interop)[3];
            Marshal.ThrowExceptionForHR(begin(interop, 0, &iid, &context, &offset));
            try { draw(context, offset); }
            finally
            {
                Marshal.Release(context);
                var end = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)interop)[4];
                Marshal.ThrowExceptionForHR(end(interop));
            }
        }
        finally { Marshal.Release(interop); }
    }
}
