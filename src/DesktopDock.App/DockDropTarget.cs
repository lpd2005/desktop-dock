using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DesktopDock.App;

[Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
internal interface IOleDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, Native.Point point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, Native.Point point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, Native.Point point, ref uint effect);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class DockDropTarget : IOleDropTarget, IDisposable
{
    private readonly nint hwnd;
    private readonly Action<Native.Point, bool> preview;
    private readonly Func<IReadOnlyList<string>, Native.Point, int> dropped;
    private readonly Action leave;
    private bool accepted, initialized;
    public bool Registered { get; }
    public int RegistrationResult { get; }
    public DockDropTarget(nint hwnd, Action<Native.Point, bool> preview, Func<IReadOnlyList<string>, Native.Point, int> dropped, Action leave)
    {
        this.hwnd = hwnd; this.preview = preview; this.dropped = dropped; this.leave = leave;
        int result = OleInitialize(0);
        if (result < 0) { RegistrationResult = result; return; }
        initialized = true;
        RegistrationResult = RegisterDragDrop(hwnd, this);
        if (RegistrationResult == unchecked((int)0x80040101))
        {
            // HwndSource installs a WPF target even without a visual tree. This is
            // our own HWND; replace that target with the native Dock's handler.
            RevokeDragDrop(hwnd);
            RegistrationResult = RegisterDragDrop(hwnd, this);
        }
        Registered = RegistrationResult >= 0;
    }
    public int DragEnter(IDataObject data, uint keys, Native.Point point, ref uint effect)
    {
        accepted = ReadPaths(data).Any(IsSupported);
        effect = accepted ? LinkEffect(effect) : 0;
        preview(point, effect != 0); return 0;
    }
    public int DragOver(uint keys, Native.Point point, ref uint effect)
    {
        effect = accepted ? LinkEffect(effect) : 0;
        preview(point, effect != 0); return 0;
    }
    public int DragLeave() { accepted = false; leave(); return 0; }
    public int Drop(IDataObject data, uint keys, Native.Point point, ref uint effect)
    {
        uint link = LinkEffect(effect);
        try { effect = link != 0 && dropped(ReadPaths(data).Where(IsSupported).ToArray(), point) > 0 ? link : 0; }
        finally { accepted = false; leave(); }
        return 0;
    }
    private static uint LinkEffect(uint allowed) => (allowed & 4) != 0 ? 4u : allowed & 1; // Never report MOVE to Explorer.
    internal static bool IsSupported(string path) => Directory.Exists(path) || (File.Exists(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".url" or ".appref-ms");
    private static IReadOnlyList<string> ReadPaths(IDataObject data)
    {
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        STGMEDIUM medium = default;
        try
        {
            if (data.QueryGetData(ref format) < 0) return [];
            data.GetData(ref format, out medium);
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0) return [];
            uint count = Math.Min(40, DragQueryFile(medium.unionmember, uint.MaxValue, null, 0));
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(medium.unionmember, i, null, 0);
                if (length is 0 or > 32767) continue;
                var value = new StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, i, value, (uint)value.Capacity);
                paths.Add(Path.GetFullPath(value.ToString()));
            }
            return paths;
        }
        catch (Exception e) when (e is COMException or IOException or ArgumentException or UnauthorizedAccessException) { return []; }
        finally { if (medium.unionmember != 0) ReleaseStgMedium(ref medium); }
    }
    public void Dispose()
    {
        if (Registered) RevokeDragDrop(hwnd);
        if (initialized) { initialized = false; OleUninitialize(); }
    }
    internal int DropFilesForChecks(string[] paths, Native.Point point)
    {
        var files = new StringCollection(); files.AddRange(paths);
        var data = new System.Windows.Forms.DataObject(); data.SetFileDropList(files);
        uint effect = 7;
        DragEnter((IDataObject)data, 1, point, ref effect);
        DragOver(1, point, ref effect);
        Drop((IDataObject)data, 0, point, ref effect);
        return (int)effect;
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint hwnd, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? file, uint length);
}
