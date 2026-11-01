using System.Runtime.InteropServices;

namespace DesktopDock.App;

internal static class DesktopEnvironmentChecks
{
    internal static void DisplayState(nint window, int value, Guid? setting = null, int length = sizeof(int))
    {
        nint packet = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy((setting ?? DesktopEnvironment.DisplaySetting).ToByteArray(), 0, packet, 16);
            Marshal.WriteInt32(packet, 16, length); Marshal.WriteInt32(packet, 20, value);
            Native.SendMessage(window, 0x0218, 0x8013, packet);
        }
        finally { Marshal.FreeHGlobal(packet); }
    }
}
