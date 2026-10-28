using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class DockWindow
{
    private readonly PreviewHover previewHover = new();
    private WindowPreview? windowPreview;
    private int previewIndex = -1;
    private double lastPreviewRefresh;
    private bool? previewPointerForChecks;

    private bool UpdateWindowPreview(double now, bool switching = false)
    {
        bool allowed = settings.WindowPreviews && motion.Target && !blocked && !autoHidden && !switching
            && pressedIndex < 0 && !dragging && !returning && !externalDrop && !shellDrag.Active
            && pendingDropGeneration == 0 && !menuOpen && !applicationsOpen() && Native.GetAsyncKeyState(0x1B) >= 0;
        if (!allowed) { CloseWindowPreview(); return false; }
        int index = IsPinnedIndex(hoveredIndex) && IsPointerOverDock() ? hoveredIndex : -1;
        int requested = previewHover.Update(index, previewPointerForChecks ?? (windowPreview?.ContainsPointer() == true), true, now);
        if (requested != previewIndex)
        {
            previewIndex = requested;
            if (requested < 0) windowPreview?.Hide();
            else
            {
                var item = entries[requested];
                var windows = launcher.WindowsFor(item);
                if (windows.Count == 0 || !iconCache.TryGetValue(item.Target, out var pixels)) windowPreview?.Hide(true);
                else
                {
                    windowPreview ??= new(target =>
                    {
                        if (previewIndex < 0 || previewIndex >= entries.Count) return;
                        launcher.ActivatePreview(entries[previewIndex], target);
                        NoteInteraction(); appStatesDirty = occlusionDirty = true;
                        previewHover.Reset(); previewIndex = -1;
                    });
                    var slot = geometry.Slots[requested];
                    var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                    windowPreview.Show(item.Name, windows, IconService.Bitmap(pixels), left + (slot.X + slot.Size / 2) * scale,
                        top + (geometry.Baseline(height / scale) - settings.IconSize * settings.Magnification - 12) * scale,
                        new() { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Bottom }, scale, settings.ReducedMotion);
                    lastPreviewRefresh = now;
                }
            }
        }
        if (windowPreview?.Open != true) return false;
        if (hoveredIndex == previewIndex) labelTimer.Stop();
        label.IsOpen = false;
        if (now - lastPreviewRefresh >= 1 && IsPinnedIndex(previewIndex))
        {
            windowPreview.Refresh(launcher.WindowsFor(entries[previewIndex])); lastPreviewRefresh = now;
        }
        return windowPreview.Open;
    }

    private void CloseWindowPreview(bool immediate = true)
    {
        previewHover.Reset(); previewIndex = -1;
        if (windowPreview?.Open == true || windowPreview?.Animating == true) windowPreview.Hide(immediate);
    }
}
