using System.Diagnostics;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopDock.Core;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

internal sealed class DockWindow : IDisposable
{
    private readonly HwndSource source;
    private readonly CompositionRenderer renderer;
    private readonly WindowRegion region;
    private readonly DockSettings settings;
    private readonly Action openSettings, changed, recover, quit;
    private readonly Action toggleSettings;
    private readonly LaunchCoordinator launcher;
    private readonly WindowOcclusion occlusion;
    private readonly RecallHotkey hotkey;
    private readonly DockVisibility visibility = new();
    private readonly DispatcherTimer visibilityTimer;
    private readonly DispatcherTimer labelTimer;
    private readonly AnimationClock animationClock;
    private readonly ToolTip label;
    private readonly Native.WinEvent windowEvent;
    private readonly List<nint> hooks = [];
    private readonly SpringMotion pointer = new();
    private readonly SpringMotion hover = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<double> frameIntervals = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private DockGeometry geometry = new([], 0, 48);
    private RoundedBounds[] footprint = [];
    private List<DockItem> entries = [];
    private float scale = 1;
    private int width, height, top, left, separatorBefore = -1;
    private bool tracking, autoHidden, disposed, rendering, eventPending;
    private bool blocked = true, occlusionDirty = true, cachedOccluded;
    private double lastOcclusionScan;
    private bool? switchingForChecks;
    private Native.Point lastMouse;
    private bool syntheticHover;
    private double pointerTarget, lastFrame, lastPresence, edgeSince = -1;
    private int pressedIndex = -1, hoveredIndex = -1, loadGeneration;
    private Native.Point pressedPoint;
    private ContextMenu? context;
    public nint Handle => source.Handle;
    public bool HardwareRendering => renderer.HardwareRendering;
    public bool HostBackdrop => renderer.HostBackdrop;
    public int IconCount => renderer.IconCount;
    public bool AvoidanceHidden => blocked;
    public Action? RestoreTaskbarRequested { get; set; }
    public bool HighResolutionClock => animationClock.HighResolution;
    public IReadOnlyList<double> FrameIntervals => frameIntervals;
    public bool LaunchWatcherActive => launcher.IsWatching;
    public string RecallShortcut => hotkey.Label;
    public bool HotkeyRegistered => hotkey.Registered;
    public bool IsDockVisible => !blocked && !autoHidden && Native.IsWindowVisible(Handle);
    internal AppClickAction LastAppAction => launcher.LastAction;
    internal nint LastAppWindow => launcher.LastWindow;

    public DockWindow(DockSettings settings, Action openSettings, Action changed, Action recover, Action quit, Action? toggleSettings = null)
    {
        this.settings = settings; this.openSettings = openSettings; this.changed = changed;
        this.recover = recover; this.quit = quit;
        this.toggleSettings = toggleSettings ?? openSettings;
        launcher = new();
        source = new HwndSource(new HwndSourceParameters("Desktop Dock")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x00200000 | 0x08000000 | 0x00000080 | 0x00000008,
            Width = 900, Height = 190
        });
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(Message);
        occlusion = new(Handle);
        hotkey = new(Handle);
        region = new(Handle);
        region.Update([], 1); // Never expose an unshaped screen-wide input window, including during startup.
        int enable = 1;
        Native.DwmSetWindowAttribute(Handle, 17, ref enable, sizeof(int));
        renderer = new(Handle);
        renderer.SetTheme(settings);
        animationClock = new(source.Dispatcher, () => RenderFrame(null, EventArgs.Empty));
        label = new ToolTip { Placement = PlacementMode.AbsolutePoint, StaysOpen = true,
            Background = new SolidColorBrush(Color.FromArgb(242, 34, 36, 43)), Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new(1),
            Padding = new(11, 6, 11, 6), FontSize = 12, HasDropShadow = true };
        labelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        labelTimer.Tick += (_, _) => ShowLabel();
        visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        visibilityTimer.Tick += (_, _) => CheckVisibility();
        windowEvent = (_, _, hwnd, objectId, _, _, _) =>
        {
            if (disposed || hwnd == Handle || objectId != 0 || eventPending) return;
            occlusionDirty = true;
            eventPending = true;
            source.Dispatcher.BeginInvoke(() => { eventPending = false; if (!disposed) CheckVisibility(); }, DispatcherPriority.Background);
        };
        hooks.Add(Native.SetWinEventHook(3, 3, 0, windowEvent, 0, 0, 0)); // Foreground changes.
        hooks.Add(Native.SetWinEventHook(0x800B, 0x800B, 0, windowEvent, 0, 0, 0));
        hooks.Add(Native.SetWinEventHook(0x8000, 0x8003, 0, windowEvent, 0, 0, 0));
        hooks.Add(Native.SetWinEventHook(0x0016, 0x0017, 0, windowEvent, 0, 0, 0)); // Minimize start/end.
        Reposition();
        visibilityTimer.Start();
    }

    public async Task LoadIcons()
    {
        int generation = ++loadGeneration;
        var localEntries = settings.Items.Append(new DockItem("Dock 设置", "dock:settings")).ToArray();
        if (iconCache.Count > 80) iconCache.Clear();
        byte[][] pixels = await Task.Run(() => localEntries.Select(item => iconCache.GetOrAdd(item.Target, _ => IconService.Load(item))).ToArray());
        if (disposed || generation != loadGeneration) return;
        entries = localEntries.ToList();
        separatorBefore = entries.FindIndex(x => System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(x.Target)) || x.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
        renderer.ClearIcons();
        foreach (var bytes in pixels) renderer.AddIcon(bytes);
        hover.Reset(0);
        occlusionDirty = true;
        RenderLayout();
        CheckVisibility();
    }

    public void ApplySettings()
    {
        renderer.SetTheme(settings);
        Reposition();
        CheckVisibility();
    }

    public void Reposition()
    {
        var screen = Forms.Screen.PrimaryScreen!;
        scale = Math.Max(.75f, Native.GetDpiForWindow(Handle) / 96f);
        width = screen.WorkingArea.Width;
        height = (int)(190 * scale);
        left = screen.WorkingArea.Left;
        top = screen.WorkingArea.Bottom - height - (int)(settings.BottomGap * scale);
        autoHidden = false;
        occlusionDirty = true;
        hover.Reset(0);
        Native.SetWindowPos(Handle, new(-1), left, top, width, height, 0x0010);
        RenderLayout();
        CheckVisibility();
    }

    private void RenderLayout()
    {
        if (entries.Count == 0) return;
        geometry = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification,
            hover.Value > 0 ? pointer.Value : null, width / scale, hover.Value, separatorBefore);
        renderer.Layout(geometry, width, height, scale);
        footprint = DockFootprint.Create(geometry, width / scale, height / scale);
        if (!autoHidden && !blocked) region.Update(footprint, scale);
    }

    private void StartRendering()
    {
        if (rendering || blocked || disposed) return;
        rendering = true;
        lastFrame = clock.Elapsed.TotalSeconds;
        animationClock.Start();
    }

    private void StopRendering()
    {
        if (!rendering) return;
        animationClock.Stop();
        rendering = false;
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        if (disposed || blocked || autoHidden) { StopRendering(); return; }
        double now = clock.Elapsed.TotalSeconds, dt = now - lastFrame;
        if (dt < .001) return;
        lastFrame = now;
        bool present = syntheticHover || IsPointerOverDock();
        bool active = pointer.Step(pointerTarget, dt, 100);
        active |= hover.Step(present ? 1 : 0, dt, present ? 36 : 29);
        if (settings.ReducedMotion) { pointer.Reset(pointerTarget); hover.Reset(present ? 1 : 0); active = false; }
        RenderLayout();
        if (frameIntervals.Count < 5000) frameIntervals.Add(dt * 1000);
        if (!active) StopRendering();
    }

    private bool IsPointerOverDock()
    {
        Native.GetCursorPos(out var point);
        Native.ScreenToClient(Handle, ref point);
        return footprint.Any(shape => shape.Contains(point.X / scale, point.Y / scale));
    }

    private int HitIndex(int x, int y) => geometry.HitIcon(x / scale, y / scale, height / scale);

    private nint Message(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        int x = unchecked((short)(long)lparam), y = unchecked((short)((long)lparam >> 16));
        switch (msg)
        {
            case 0x0021: handled = true; return 3;
            case 0x0084:
                // The OS region performs cross-process hit exclusion. No reliance on HTTRANSPARENT.
                handled = true; return 1;
            case 0x0312:
                if (wparam.ToInt32() == RecallHotkey.Id) { Summon(); handled = true; }
                break;
            case 0x020A:
            case 0x020E: NoteInteraction(); break;
            case 0x0200:
                if (blocked) break;
                if (x != lastMouse.X || y != lastMouse.Y) { NoteInteraction(); lastMouse = new() { X = x, Y = y }; }
                if (!tracking)
                {
                    var track = new Native.MouseTrack { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MouseTrack>(), Flags = 2, Hwnd = hwnd };
                    Native.TrackMouseEvent(ref track); tracking = true;
                    if (hover.Value < .02) pointer.Reset(x / scale);
                }
                lastPresence = clock.Elapsed.TotalSeconds;
                pointerTarget = x / scale;
                if (!autoHidden) StartRendering();
                int current = HitIndex(x, y);
                if (current != hoveredIndex)
                {
                    hoveredIndex = current; label.IsOpen = false; labelTimer.Stop();
                    if (current >= 0) labelTimer.Start();
                }
                break;
            case 0x02A3:
                tracking = false; hoveredIndex = -1;
                labelTimer.Stop(); label.IsOpen = false;
                if (pressedIndex < 0) StartRendering();
                break;
            case 0x0201:
                NoteInteraction();
                label.IsOpen = false;
                pressedIndex = HitIndex(x, y); pressedPoint = new() { X = x, Y = y };
                if (pressedIndex >= 0) Native.SetCapture(hwnd);
                break;
            case 0x0202:
                NoteInteraction();
                int released = HitIndex(x, y), pressed = pressedIndex;
                pressedIndex = -1;
                Native.ReleaseCapture();
                bool moved = Math.Abs(x - pressedPoint.X) > 12 * scale || Math.Abs(y - pressedPoint.Y) > 12 * scale;
                if (pressed >= 0 && released >= 0 && pressed < settings.Items.Count && released < settings.Items.Count && moved)
                {
                    var item = settings.Items[pressed]; settings.Items.RemoveAt(pressed); settings.Items.Insert(released, item);
                    changed(); _ = LoadIcons();
                }
                else if (pressed >= 0 && pressed == released && !moved) Launch(entries[released]);
                break;
            case 0x0215: pressedIndex = -1; break;
            case 0x0205: NoteInteraction(); ShowMenu(HitIndex(x, y)); break;
            case 0x007E:
            case 0x001A:
            case 0x02E0: source.Dispatcher.BeginInvoke(Reposition); break;
            case Native.RecoverMessage: recover(); break;
            case Native.RestoreTaskbarMessage: RestoreTaskbarRequested?.Invoke(); break;
            case 0x0010: quit(); handled = true; break;
        }
        return 0;
    }

    private void ShowLabel()
    {
        labelTimer.Stop();
        if (hoveredIndex < 0 || hoveredIndex >= entries.Count || blocked || autoHidden || !IsPointerOverDock()) return;
        var slot = geometry.Slots[hoveredIndex];
        label.Content = entries[hoveredIndex].Name;
        label.HorizontalOffset = (left + (slot.X + slot.Size / 2) * scale) / scale - 38;
        label.VerticalOffset = (top + (geometry.Baseline(height / scale) - slot.Size - 40) * scale) / scale;
        label.IsOpen = true;
    }

    private void Launch(DockItem item, bool toggle = true)
    {
        NoteInteraction();
        if (item.Target == "dock:settings") { if (toggle) toggleSettings(); else openSettings(); return; }
        try
        {
            launcher.Launch(item, toggle);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { System.Windows.MessageBox.Show("无法打开这个入口，请检查目标是否仍然存在。", "Desktop Dock"); }
    }

    private void ShowMenu(int index)
    {
        label.IsOpen = false;
        context = new ContextMenu();
        context.PreviewMouseMove += (_, _) => NoteInteraction();
        context.PreviewKeyDown += (_, _) => NoteInteraction();
        if (index >= 0 && index < settings.Items.Count)
        {
            AddMenu("打开 " + settings.Items[index].Name, () => Launch(settings.Items[index], false));
            AddMenu("从 Dock 移除", () => { settings.Items.RemoveAt(index); changed(); _ = LoadIcons(); });
            context.Items.Add(new Separator());
        }
        AddMenu("添加应用或文件夹…", openSettings);
        AddMenu("Dock 设置…", openSettings);
        AddMenu("恢复桌面图标", recover);
        context.Items.Add(new Separator());
        AddMenu("退出 Desktop Dock", quit);
        context.Placement = PlacementMode.MousePoint;
        context.IsOpen = true;
    }

    private void AddMenu(string title, Action action)
    {
        var menu = new MenuItem { Header = title };
        menu.Click += (_, _) => { NoteInteraction(); action(); }; context!.Items.Add(menu);
    }

    public void CheckVisibility()
    {
        if (disposed) return;
        double now = clock.Elapsed.TotalSeconds;
        if (occlusionDirty || now - lastOcclusionScan >= 1)
        {
            cachedOccluded = occlusion.Covered(RestDockArea);
            occlusionDirty = false; lastOcclusionScan = now;
        }
        bool switching = switchingForChecks ?? ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0
            && ((Native.GetAsyncKeyState(0x11) & 0x8000) == 0 || (Native.GetAsyncKeyState(0x09) & 0x8000) != 0));
        bool show = entries.Count > 0 && visibility.ShouldShow(cachedOccluded, switching, now);
        if (!show)
        {
            if (!blocked || Native.IsWindowVisible(Handle))
            {
                blocked = true; autoHidden = false; StopRendering(); hover.Reset(0); label.IsOpen = false;
                if (context is not null) context.IsOpen = false;
                Native.ShowWindow(Handle, 0);
            }
            return;
        }
        if (blocked)
        {
            blocked = false; autoHidden = false;
            Native.SetWindowPos(Handle, new(-1), left, top, width, height, 0x0010);
            RenderLayout();
            Native.ShowWindow(Handle, 8);
            lastPresence = now;
        }
        if (visibility.IsTemporarilyShown) { SetAutoHidden(false); return; }
        if (!settings.AutoHide)
        {
            SetAutoHidden(false);
            if (!Native.IsWindowVisible(Handle)) Native.ShowWindow(Handle, 8);
            return;
        }
        if (context?.IsOpen == true || pressedIndex >= 0) return;
        Native.GetCursorPos(out var cursor);
        var rest = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification, null, width / scale, separatorBefore: separatorBefore);
        double centerLeft = left + rest.BodyLeft(width / scale) * scale;
        bool atEdge = cursor.X >= centerLeft && cursor.X <= centerLeft + rest.Width * scale
            && cursor.Y >= Forms.Screen.PrimaryScreen!.WorkingArea.Bottom - 3;
        if (autoHidden)
        {
            if (!atEdge) edgeSince = -1;
            else if (edgeSince < 0) edgeSince = now;
            else if (now - edgeSince > .12) { SetAutoHidden(false); lastPresence = now; }
        }
        else
        {
            if (IsPointerOverDock()) lastPresence = now;
            if (now - lastPresence > .5) SetAutoHidden(true);
        }
    }

    public void Summon()
    {
        visibility.Summon(clock.Elapsed.TotalSeconds);
        occlusionDirty = true;
        CheckVisibility();
    }
    private void NoteInteraction() => visibility.Interact(clock.Elapsed.TotalSeconds);
    public void RefreshOcclusion() { occlusionDirty = true; CheckVisibility(); }

    private WindowBounds RestDockArea
    {
        get
        {
            var rest = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification, null, width / scale, separatorBefore: separatorBefore);
            int x = left + (int)(rest.BodyLeft(width / scale) * scale);
            return new(x, top + (int)(rest.BodyTop(height / scale) * scale), x + (int)(rest.Width * scale),
                top + height - (int)(DockGeometry.Foot * scale));
        }
    }

    private void SetAutoHidden(bool value)
    {
        if (value == autoHidden || blocked) return;
        autoHidden = value;
        if (value)
        {
            StopRendering(); hover.Reset(0); label.IsOpen = false;
            var rest = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification, null, width / scale, separatorBefore: separatorBefore);
            region.Update([new(rest.BodyLeft(width / scale), 0, rest.Width, 3 / scale, 0)], scale);
            Native.SetWindowPos(Handle, new(-1), left, Forms.Screen.PrimaryScreen!.WorkingArea.Bottom - 3, width, height, 0x0010);
        }
        else
        {
            Native.SetWindowPos(Handle, new(-1), left, top, width, height, 0x0010 | 0x0040);
            RenderLayout();
        }
    }

    public void PreviewWave(double fraction = .5)
    {
        NoteInteraction();
        syntheticHover = true;
        var rest = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification, null, width / scale, separatorBefore: separatorBefore);
        pointerTarget = rest.BodyLeft(width / scale) + rest.Width * fraction;
        if (hover.Value < .01) pointer.Reset(pointerTarget);
        StartRendering();
    }
    public void ResetWave() { syntheticHover = false; StartRendering(); }
    public void ClearFrameSamples() => frameIntervals.Clear();

    internal void InvokeSettingsEntry() => Launch(new("Dock 设置", "dock:settings"));
    internal void LaunchForCheck(DockItem item) => Launch(item);
    internal void UseOcclusionWindowsForChecks(Func<IEnumerable<nint>> source) { occlusion.WindowsForChecks = source; RefreshOcclusion(); }
    internal void SetSwitchingForChecks(bool? value) { switchingForChecks = value; CheckVisibility(); }
    internal void InteractForChecks() => NoteInteraction();
    internal void InvokeHotkeyForChecks() => Native.PostMessage(Handle, 0x0312, RecallHotkey.Id, 0);
    internal bool ExcludedFromAltTab => (Native.GetWindowLongPtr(Handle, -20).ToInt64() & (0x80 | 0x40000)) == 0x80;

    public InputRegionReport VerifyInputRegion()
    {
        nint nativeRegion = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (Native.GetWindowRgn(Handle, nativeRegion) == 0) throw new InvalidOperationException("Missing OS input region.");
            int samples = 0, blockedOutside = 0;
            for (int x = 0; x < width; x += Math.Max(4, (int)(8 * scale)))
            for (int y = 0; y < height; y += Math.Max(4, (int)(8 * scale)))
            {
                bool near = footprint.Any(shape => shape.Contains(x / scale, y / scale)
                    || shape.Contains((x - 2) / scale, (y - 2) / scale) || shape.Contains((x + 2) / scale, (y + 2) / scale));
                if (near) continue;
                samples++;
                if (Native.PtInRegion(nativeRegion, x, y) || Native.WindowFromPoint(new() { X = left + x, Y = top + y }) == Handle) blockedOutside++;
            }
            return new(samples, blockedOutside);
        }
        finally { Native.DeleteObject(nativeRegion); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; StopRendering(); visibilityTimer.Stop(); labelTimer.Stop(); label.IsOpen = false;
        foreach (nint hook in hooks) if (hook != 0) Native.UnhookWinEvent(hook);
        context?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        hotkey.Dispose(); launcher.Dispose(); animationClock.Dispose(); renderer.Dispose(); source.Dispose();
    }
}

internal sealed record InputRegionReport(int OutsideSamples, int BlockedOutside)
{
    public bool Passed => OutsideSamples > 0 && BlockedOutside == 0;
}
