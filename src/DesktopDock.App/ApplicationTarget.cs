using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;

namespace DesktopDock.App;

internal sealed record ApplicationTarget(string? ProcessName, string? AppId, bool SpecificWindow)
{
    public static ApplicationTarget Resolve(string input, string arguments)
    {
        string target = Environment.ExpandEnvironmentVariables(input), args = arguments;
        string? appId = null;
        bool specific = !string.IsNullOrWhiteSpace(args);
        if (Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, shortcut = null, explorer = null, folder = null, item = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                shortcut = ((dynamic)shell!).CreateShortcut(target);
                string resolved = ((dynamic)shortcut).TargetPath;
                args = ((dynamic)shortcut).Arguments;
                explorer = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                folder = ((dynamic)explorer!).NameSpace(Path.GetDirectoryName(target));
                item = ((dynamic)folder!).ParseName(Path.GetFileName(target));
                appId = ((dynamic)item!).ExtendedProperty("System.AppUserModel.ID") as string;
                if (!string.IsNullOrWhiteSpace(resolved)) target = Environment.ExpandEnvironmentVariables(resolved);
                specific |= !string.IsNullOrWhiteSpace(args);
            }
            catch (Exception e) when (e is COMException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally { Release(item); Release(folder); Release(explorer); Release(shortcut); Release(shell); }
        }
        if (Path.GetExtension(target).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            try { target = File.ReadLines(target).FirstOrDefault(x => x.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..] ?? target; specific = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        const string appsFolder = "shell:AppsFolder\\";
        int appIndex = (target + " " + args).IndexOf(appsFolder, StringComparison.OrdinalIgnoreCase);
        bool shellApplication = appIndex >= 0;
        if (shellApplication) appId = (target + " " + args)[(appIndex + appsFolder.Length)..].Trim().Trim('"').Split(' ')[0];
        string? process = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(target)
            : target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "SystemSettings"
            : target.StartsWith("microsoft-edge:", StringComparison.OrdinalIgnoreCase) ? "msedge"
            : Directory.Exists(target) || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "explorer" : UriProcess(target);
        specific |= Directory.Exists(target) || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        // An AppsFolder argument identifies an application, not an argument-specific document window.
        if (shellApplication || !string.IsNullOrWhiteSpace(appId) && process is null) specific = false;
        return new(process, string.IsNullOrWhiteSpace(appId) ? null : appId, specific);
    }

    private static string? UriProcess(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.IsFile) return null;
        try
        {
            string association = uri.Scheme;
            using var choice = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\" + uri.Scheme + "\\UserChoice");
            association = choice?.GetValue("ProgId") as string ?? association;
            using var command = Registry.ClassesRoot.OpenSubKey(association + "\\shell\\open\\command");
            string text = Environment.ExpandEnvironmentVariables(command?.GetValue(null) as string ?? "").Trim();
            string executable = text.StartsWith('"') ? text.Split('"').ElementAtOrDefault(1) ?? "" : text.Split(' ')[0];
            return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(executable) : null;
        }
        catch (System.Security.SecurityException) { return null; }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

internal static class WindowAppId
{
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Value; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out Variant value);
    }
    public static string? Read(nint hwnd)
    {
        string? explicitId = ReadWindow(hwnd);
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        string? processId = ReadProcess(pid);
        // Packaged desktop apps can expose only process identity. Their optional
        // explicit window ID may also differ from the Store activation identity.
        return processId ?? explicitId;
    }

    internal static string? ReadProcess(uint pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            uint length = 0;
            if (GetApplicationUserModelId(process, ref length, null) != 122 || length is 0 or > 512) return null;
            var value = new StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, value) == 0 ? value.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    private static string? ReadWindow(nint hwnd)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, in iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            if (store.GetValue(in key, out var value) != 0) return null;
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? value);
}
