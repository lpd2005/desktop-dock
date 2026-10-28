using System.IO;
using System.Text.Json;

namespace DesktopDock.App;

internal sealed class DesktopIcons(string dataDirectory) : IDisposable
{
    private readonly string journal = Path.Combine(dataDirectory, "desktop-state.json");
    private bool? originalVisible;
    private bool hiding;
    internal static bool ShellReady => FindView() != 0;

    private static nint FindView()
    {
        nint result = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            nint shell = Native.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null);
            if (shell == 0) return true;
            result = Native.FindWindowEx(shell, 0, "SysListView32", "FolderView");
            return result == 0;
        }, 0);
        return result;
    }

    public void Recover()
    {
        if (!File.Exists(journal)) return;
        var state = JsonSerializer.Deserialize<RecoveryState>(File.ReadAllText(journal));
        nint view = FindView();
        if (state is null || view == 0) return; // Keep the journal if Explorer is not ready.
        Native.ShowWindow(view, state.WasVisible ? 5 : 0);
        if (Native.IsWindowVisible(view) == state.WasVisible) File.Delete(journal);
    }

    public void Apply(bool hide)
    {
        if (!hide) { Restore(); return; }
        nint view = FindView();
        if (view == 0) return;
        if (!originalVisible.HasValue)
        {
            originalVisible = Native.IsWindowVisible(view);
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(journal, JsonSerializer.Serialize(new RecoveryState(originalVisible.Value)));
        }
        Native.ShowWindow(view, 0);
        hiding = true;
    }

    public void Restore()
    {
        if (!hiding) return;
        nint view = FindView();
        if (view == 0) return;
        Native.ShowWindow(view, originalVisible == true ? 5 : 0);
        if (Native.IsWindowVisible(view) != originalVisible) return;
        if (File.Exists(journal)) File.Delete(journal);
        originalVisible = null;
        hiding = false;
    }

    public void Dispose() => Restore();

    public object VerifyRoundTrip()
    {
        nint view = FindView();
        if (view == 0) throw new InvalidOperationException("Desktop view is unavailable.");
        bool before = Native.IsWindowVisible(view);
        bool hidden;
        try { Apply(true); hidden = !Native.IsWindowVisible(view); }
        finally { Restore(); }
        return new { ViewAvailable = true, Hidden = hidden, OriginalStateRestored = Native.IsWindowVisible(view) == before };
    }
    private sealed record RecoveryState(bool WasVisible);
}
