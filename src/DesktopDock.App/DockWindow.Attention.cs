using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class DockWindow
{
    private sealed record AttentionRequest(uint Process, HashSet<string> Keys);
    private readonly Dictionary<nint, AttentionRequest> attentionRequests = [];
    private readonly Dictionary<nint, double> attentionDismissed = [];
    private readonly ShellAttention attentionHook;
    private readonly bool attentionAllowed;
    private bool revealAttention;
    internal bool AttentionAvailable => attentionHook.Registered;
    private static string ItemKey(DockItem item) => item.Target + "\n" + item.Arguments;

    private void AttentionChanged(int kind, nint window)
    {
        if (disposed || !attentionAllowed || !settings.AppAttention) return;
        if (kind == 2)
        {
            bool removed = attentionRequests.Remove(window); attentionDismissed.Remove(window); if (removed) RefreshAttention(); return;
        }
        if (kind is 4 or 0x8004)
        {
            if (window != 0)
            {
                attentionDismissed.Remove(window);
                if (attentionRequests.Count == 0) return;
                var pendingKeys = attentionRequests.Values.SelectMany(x => x.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in settings.Items.Where(x => pendingKeys.Contains(ItemKey(x)) && launcher.IsAssociated(x, window))) AcknowledgeAttention(item);
            }
            return;
        }
        if (window == 0 || !WindowClose.CanClose(window) || attentionRequests.ContainsKey(window)
            || attentionDismissed.TryGetValue(window, out double until) && clock.Elapsed.TotalSeconds < until
            || Native.GetAncestor(Native.GetForegroundWindow(), 3) == window && !Native.IsIconic(window)) return;
        var keys = settings.Items.Where(x => launcher.IsAssociated(x, window)).Select(ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0 || attentionRequests.Count >= 64) return;
        Native.GetWindowThreadProcessId(window, out uint pid);
        attentionRequests[window] = new(pid, keys); revealAttention = true;
        RefreshAttention(); CheckVisibility();
    }
    private void RefreshAttention()
    {
        if (!settings.AppAttention) { attentionRequests.Clear(); attentionDismissed.Clear(); revealAttention = false; visibility.ClearNotification(); }
        var pinned = settings.Items.Select(ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in attentionRequests.ToArray())
        {
            pair.Value.Keys.RemoveWhere(key => !pinned.Contains(key));
            Native.GetWindowThreadProcessId(pair.Key, out uint pid);
            if (pair.Value.Keys.Count == 0 || !Native.IsWindow(pair.Key) || pid != pair.Value.Process) attentionRequests.Remove(pair.Key);
        }
        if (attentionRequests.Count == 0) { revealAttention = false; visibility.ClearNotification(); }
        foreach (var window in attentionDismissed.Where(x => x.Value <= clock.Elapsed.TotalSeconds).Select(x => x.Key).ToArray()) attentionDismissed.Remove(window);
        if (renderer.SetAttention(entries.Select(x => attentionRequests.Values.Any(request => request.Keys.Contains(ItemKey(x)))).ToArray())) RenderLayout();
    }
    private void AcknowledgeAttention(DockItem item)
    {
        string key = ItemKey(item);
        foreach (var pair in attentionRequests.Where(x => x.Value.Keys.Contains(key)).ToArray())
        { attentionRequests.Remove(pair.Key); attentionDismissed[pair.Key] = clock.Elapsed.TotalSeconds + 2; }
        if (attentionRequests.Count == 0) { revealAttention = false; visibility.ClearNotification(); }
        RefreshAttention();
    }
}
