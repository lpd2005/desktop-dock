using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopDock.Core;
using Forms = System.Windows.Forms;

namespace DesktopDock.App;

internal sealed partial class DockWindow : IDisposable
{
    private readonly HwndSource source;
    private readonly DockPresentationWindow visualSource;
    private readonly DockMotion motion;
    private readonly CompositionRenderer renderer;
    private readonly WindowRegion region;
    private readonly DockSettings settings;
    private readonly Action openSettings, changed, recover, quit;
    private readonly Action<nint?> toggleSettings;
    private readonly Func<nint> settingsHandle;
    private readonly Action toggleApplications;
    private readonly Func<nint> applicationsHandle;
    private readonly Func<bool> applicationsOpen;
    private readonly LaunchCoordinator launcher;
    private readonly WindowOcclusion occlusion;
    private readonly DockDropTarget dropTarget;
    private readonly ShellDragMonitor shellDrag = new();
    private readonly DockVisibility visibility = new();
    private readonly DispatcherTimer visibilityTimer;
    private readonly DispatcherTimer labelTimer;
    private readonly AnimationClock animationClock;
    private readonly DesktopEnvironment environment;
    private readonly ToolTip label;
    private readonly Native.WinEvent windowEvent;
    private readonly List<nint> hooks = [];
    private readonly SpringMotion pointer = new();
    private readonly SpringMotion hover = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<double> frameIntervals = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private DockGeometry geometry = new([], 0, 48);
    private DockGeometry? restGeometry;
    private (int Count, double IconSize, double Magnification, double Width, int Separator) restGeometryKey;
    private RoundedBounds[] footprint = [];
    private List<DockItem> entries = [];
    private float scale = 1;
    private int width, height, top, left, separatorBefore = -1;
    private bool tracking, autoHidden, disposed, rendering, eventPending;
    private bool suspended;
    private bool blocked = true, occlusionDirty = true, cachedOccluded;
    private double lastOcclusionScan;
    private bool appStatesDirty = true;
    private double lastAppStateScan = double.NegativeInfinity;
    private int appStateScans;
    private bool? stateRefreshForChecks;
    private bool? switchingForChecks;
    private Native.Point lastMouse;
    private bool syntheticHover;
    private double pointerTarget, lastFrame;
    private double? pointerTargetForChecks;
    private int pressedIndex = -1, hoveredIndex = -1, loadGeneration;
    private int interactionVersion;
    private bool startingDrag;
    private int pendingDropGeneration;
    private int hideTransitions;
    private bool? pointerOverForChecks;
    private bool? bottomEdgeForChecks;
    private bool? visibilityForChecks;
    private double dropVisibleUntil = double.NegativeInfinity;
    internal int IconLoadDelayForChecks { get; set; }
    private Native.Point pressedPoint;
    private double pressedAt;
    private readonly DockClickSequence clicks = new();
    private nint? pressedForeground;
    private bool dragging, returning, sortingSettles, externalDrop, suppressHover, systemDragging;
    private bool checkingInteraction;
    private bool isolatingInputForChecks;
    private bool dispatchingCheckMouse;
    private Action? dragStartInterruptionForChecks;
    private int dragFrom = -1, dragTo = -1, ghostIndex = -1, insertion = -1;
    private double gripX = .5, gripY = .5, dragX, dragY, ghostX, ghostY, returnX, returnY;
    private int ghostMargin;
    private BitmapOverlay? ghost;
    private byte[]? ghostNormalPixels, ghostRemovePixels;
    private bool ghostRemoving;
    private readonly TimedTransition returnMotion = new();
    private bool menuOpen;
    private nint menuForChecks;
    public nint Handle => source.Handle;
    public bool HardwareRendering => renderer.HardwareRendering;
    public bool HostBackdrop => renderer.HostBackdrop;
    public int IconCount => renderer.IconCount;
    public bool AvoidanceHidden => blocked;
    public Action? RestoreTaskbarRequested { get; set; }
    public Action<DesktopChange>? EnvironmentChanged { get; set; }
    public Action<bool>? SuspensionChanged { get; set; }
    public bool HighResolutionClock => animationClock.HighResolution;
    public IReadOnlyList<double> FrameIntervals => frameIntervals;
    public bool LaunchWatcherActive => launcher.IsWatching;
    public bool IsDockVisible => !blocked && !autoHidden && Native.IsWindowVisible(Handle) && Native.IsWindowVisible(visualSource.Handle);
    internal AppClickAction LastAppAction => launcher.LastAction;
    internal nint LastAppWindow => launcher.LastWindow;
    internal string VisibilityStateForChecks => System.Text.Json.JsonSerializer.Serialize(new
    {
        blocked, autoHidden, cachedOccluded, CoveredNow = occlusion.Covered(RestDockArea),
        motion.Target, motion.Active, motion.Progress, pressedIndex, dragging, returning, externalDrop,
        ShellDragging = shellDrag.Active, systemDragging, pendingDropGeneration, menuOpen,
        DropProtected = clock.Elapsed.TotalSeconds < dropVisibleUntil, visibility.IsTemporarilyShown,
        PointerOver = IsPointerOverDock()
    });

    public DockWindow(DockSettings settings, Action openSettings, Action changed, Action recover, Action quit,
        Action<nint?>? toggleSettings = null, Func<nint>? settingsHandle = null, Action? toggleApplications = null,
        Func<nint>? applicationsHandle = null, Func<bool>? applicationsOpen = null)
    {
        this.settings = settings; this.openSettings = openSettings; this.changed = changed;
        this.recover = recover; this.quit = quit;
        this.toggleSettings = toggleSettings ?? (_ => openSettings());
        this.settingsHandle = settingsHandle ?? (() => 0);
        this.toggleApplications = toggleApplications ?? (() => { });
        this.applicationsHandle = applicationsHandle ?? (() => 0);
        this.applicationsOpen = applicationsOpen ?? (() => false);
        launcher = new();
        source = new HwndSource(new HwndSourceParameters("Desktop Dock")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x00200000 | 0x08000000 | 0x00000080 | 0x00000008,
            Width = 900, Height = 250
        });
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(Message);
        occlusion = new(Handle);
        dropTarget = new(Handle, PreviewDrop, AddDroppedFiles, LeaveDrop);
        region = new(Handle);
        region.Update([], 1); // Never expose an unshaped screen-wide input window, including during startup.
        visualSource = new();
        renderer = new(visualSource.Handle);
        motion = new(Handle, visualSource.Handle, source.Dispatcher);
        renderer.SetTheme(settings);
        animationClock = new(source.Dispatcher, () => RenderFrame(null, EventArgs.Empty));
        label = new ToolTip { Placement = PlacementMode.AbsolutePoint, StaysOpen = true,
            Background = new SolidColorBrush(Color.FromArgb(242, 34, 36, 43)), Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new(1),
            Padding = new(11, 6, 11, 6), FontSize = 12, HasDropShadow = true };
        labelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        labelTimer.Tick += (_, _) => ShowLabel();
        visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        visibilityTimer.Tick += (_, _) => CheckVisibility();
        windowEvent = (_, eventType, hwnd, objectId, _, _, _) =>
        {
            if (eventType == 0x0008)
            {
                shellDrag.CaptureStarted(hwnd);
                source.Dispatcher.BeginInvoke(CheckVisibility);
                return;
            }
            if (eventType is 0x000E or 0x000F)
            {
                systemDragging = eventType == 0x000E;
                source.Dispatcher.BeginInvoke(CheckVisibility);
                return;
            }
            if (disposed || objectId != 0) return;
            if (eventType != 0x800B) appStatesDirty = true;
            if (hwnd == Handle || eventPending) return;
            Native.GetWindowThreadProcessId(hwnd, out uint eventProcess);
            if (eventProcess == Environment.ProcessId && (Native.GetWindowLongPtr(hwnd, -16).ToInt64() & 0x00C00000) == 0) return;
            occlusionDirty = true;
            eventPending = true;
            source.Dispatcher.BeginInvoke(() => { eventPending = false; if (!disposed) CheckVisibility(); }, DispatcherPriority.Background);
        };
        hooks.Add(Native.SetWinEventHook(3, 3, 0, windowEvent, 0, 0, 0)); // Foreground changes.
        hooks.Add(Native.SetWinEventHook(0x0008, 0x0008, 0, windowEvent, 0, 0, 0)); // Mouse capture starts even without a drag WinEvent.
        hooks.Add(Native.SetWinEventHook(0x800B, 0x800B, 0, windowEvent, 0, 0, 0));
        hooks.Add(Native.SetWinEventHook(0x8000, 0x8003, 0, windowEvent, 0, 0, 0));
        hooks.Add(Native.SetWinEventHook(0x0016, 0x0017, 0, windowEvent, 0, 0, 0)); // Minimize start/end.
        hooks.Add(Native.SetWinEventHook(0x000E, 0x000F, 0, windowEvent, 0, 0, 0)); // Shell drag start/end, where supplied.
        environment = new(source, SetSuspended, change => { Reposition(); EnvironmentChanged?.Invoke(change); });
        Reposition();
        visibilityTimer.Start();
    }

    public async Task LoadIcons(bool fromDrop = false)
    {
        int generation = ++loadGeneration;
        var localEntries = new[] { new DockItem("桌面应用", "dock:apps") }.Concat(settings.Items).Append(new("Dock 设置", "dock:settings")).ToArray();
        if (fromDrop || pendingDropGeneration != 0)
        {
            pendingDropGeneration = generation;
            CheckVisibility();
        }
        try
        {
            if (iconCache.Count > 80) iconCache.Clear();
            if (IconLoadDelayForChecks > 0) await Task.Delay(IconLoadDelayForChecks);
            byte[][] pixels = await Task.Run(() => localEntries.Select(item => iconCache.GetOrAdd(item.Target, _ => IconService.Load(item))).ToArray());
            if (disposed || generation != loadGeneration) return;
            CancelInteraction(); hover.Reset(0);
            int nextSeparator = Array.FindIndex(localEntries, x => System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(x.Target)) || x.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
            var nextGeometry = DockLayout.Calculate(localEntries.Length, settings.IconSize, settings.Magnification, null, width / scale, separatorBefore: nextSeparator);
            renderer.SynchronizeIcons(entries, localEntries, pixels, nextGeometry, width, height, scale);
            entries = localEntries.ToList(); separatorBefore = nextSeparator;
            occlusionDirty = appStatesDirty = true;
            RenderLayout();
            CheckVisibility();
        }
        finally
        {
            if (pendingDropGeneration == generation)
            {
                pendingDropGeneration = 0;
                dropVisibleUntil = clock.Elapsed.TotalSeconds + .35;
                CheckVisibility();
            }
        }
    }

    public void ApplySettings()
    {
        CloseWindowPreview();
        renderer.SetTheme(settings);
        Reposition();
        CheckVisibility();
    }

    public void Reposition()
    {
        CancelInteraction();
        var screen = Forms.Screen.PrimaryScreen!;
        scale = Math.Max(.75f, Native.GetDpiForWindow(Handle) / 96f);
        width = screen.WorkingArea.Width;
        height = (int)(250 * scale);
        left = screen.WorkingArea.Left;
        top = screen.WorkingArea.Bottom - height - (int)(settings.BottomGap * scale);
        autoHidden = false;
        occlusionDirty = true;
        hover.Reset(0); renderer.ResetPressFeedback();
        motion.SetBounds(left, top, width, height, scale);
        RenderLayout();
        CheckVisibility();
    }

    private void RenderLayout(double dt = 0)
    {
        if (entries.Count == 0) return;
        geometry = DockLayout.Calculate(entries.Count, settings.IconSize, settings.Magnification,
            hover.Value > 0 ? pointer.Value : null, width / scale, hover.Value, separatorBefore);
        int[]? order = dragging ? DockReorder.Order(entries.Count, dragFrom, dragTo, settings.Items.Count, start: 1) : null;
        renderer.Layout(geometry, width, height, scale, order, !settings.ReducedMotion && (dragging || returning || sortingSettles), dt, ghostIndex, insertion);
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
        TrackPressedPointer();
        bool present = (syntheticHover || IsPointerOverDock()) && !dragging && !returning && !externalDrop && !shellDrag.Active && !suppressHover;
        bool active = pointer.Step(pointerTargetForChecks ?? pointerTarget, dt, 100);
        active |= hover.Step(present ? 1 : 0, dt, present ? 36 : 29);
        if (settings.ReducedMotion) { pointer.Reset(pointerTarget); hover.Reset(present ? 1 : 0); active = false; }
        RenderLayout(dt);
        active |= renderer.OrderingActive || renderer.PressFeedbackActive || renderer.StateAnimationActive;
        if (sortingSettles && !renderer.OrderingActive) sortingSettles = false;
        active |= UpdateGhost(now);
        active |= pressedIndex >= 0 || dragging;
        if (frameIntervals.Count < 5000) frameIntervals.Add(dt * 1000);
        if (!active) StopRendering();
    }

    private bool IsPointerOverDock()
    {
        if (pointerOverForChecks is bool value) return value;
        Native.GetCursorPos(out var point);
        Native.ScreenToClient(Handle, ref point);
        return footprint.Any(shape => shape.Contains(point.X / scale, point.Y / scale));
    }

    private int HitIndex(int x, int y) => geometry.HitIcon(x / scale, y / scale, height / scale);

    private void TrackPressedPointer()
    {
        if (checkingInteraction || isolatingInputForChecks || pressedIndex < 0) return;
        if (Native.GetAsyncKeyState(0x1B) < 0) { CancelInteraction(); return; }
        Native.GetCursorPos(out var cursor); Native.ScreenToClient(Handle, ref cursor);
        int button = Native.GetSystemMetrics(23) != 0 ? 0x02 : 0x01;
        bool down = Native.GetAsyncKeyState(button) < 0;
        if (down && !dragging && DockReorder.Moved((cursor.X - pressedPoint.X) / scale, (cursor.Y - pressedPoint.Y) / scale)) BeginIconDrag();
        if (dragging)
        {
            dragX = cursor.X / scale; dragY = cursor.Y / scale;
            dragTo = DockReorder.Target(RestGeometry, settings.Items.Count, dragFrom, dragX, start: 1);
            if (!down)
            {
                interactionVersion++;
                pressedIndex = -1;
                pressedForeground = null;
                EndIconDrag(CanDropSort(dragX, dragY), DockReorder.RemoveOnRelease(RestGeometry, width / scale, height / scale, dragX, dragY));
                Native.ReleaseCapture();
            }
        }
        else if (!down && !IsPointerOverDock())
        { renderer.SetPressed(pressedIndex, false); interactionVersion++; pressedIndex = -1; pressedForeground = null; Native.ReleaseCapture(); }
    }

    private nint Message(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        if (suspended && msg is >= 0x0200 and <= 0x020E) { handled = true; return 0; }
        if ((checkingInteraction || isolatingInputForChecks) && !dispatchingCheckMouse && msg is >= 0x0200 and <= 0x020E)
        { handled = true; return 0; }
        int x = unchecked((short)(long)lparam), y = unchecked((short)((long)lparam >> 16));
        switch (msg)
        {
            case 0x0021: handled = true; return 3;
            case 0x0084:
                // The OS region performs cross-process hit exclusion. No reliance on HTTRANSPARENT.
                handled = true; return 1;
            case 0x0020:
                if (dragging) { Native.SetCursor(Native.LoadCursor(0, new(32646))); handled = true; return 1; }
                break;
            case 0x020A:
            case 0x020E: NoteInteraction(); break;
            case 0x0200:
                if (blocked) break;
                if (x != lastMouse.X || y != lastMouse.Y)
                {
                    NoteInteraction(); lastMouse = new() { X = x, Y = y };
                    if (pressedIndex < 0 && !returning) suppressHover = false;
                }
                if (IsPinnedIndex(pressedIndex))
                {
                    if (!dragging && DockReorder.Moved((x - pressedPoint.X) / scale, (y - pressedPoint.Y) / scale)) BeginIconDrag();
                    if (dragging)
                    {
                        dragX = x / scale; dragY = y / scale;
                        dragTo = DockReorder.Target(RestGeometry, settings.Items.Count, dragFrom, dragX, start: 1);
                        RenderLayout(); StartRendering(); break;
                    }
                }
                if (!tracking)
                {
                    var track = new Native.MouseTrack { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MouseTrack>(), Flags = 2, Hwnd = hwnd };
                    Native.TrackMouseEvent(ref track); tracking = true;
                    if (hover.Value < .02) pointer.Reset(x / scale);
                }
                pointerTarget = x / scale;
                if (!autoHidden) StartRendering();
                int current = HitIndex(x, y);
                if (current != hoveredIndex)
                {
                    hoveredIndex = current; label.IsOpen = false; labelTimer.Stop();
                    if (current >= 0 && pressedIndex < 0 && !externalDrop && !returning) labelTimer.Start();
                }
                UpdateWindowPreview(clock.Elapsed.TotalSeconds);
                break;
            case 0x02A3:
                tracking = false; hoveredIndex = -1;
                labelTimer.Stop(); label.IsOpen = false;
                if (pressedIndex < 0) StartRendering();
                break;
            case 0x0201:
            case 0x0203: // Some input classes replace the second button-down with WM_LBUTTONDBLCLK.
                CloseWindowPreview();
                NoteInteraction();
                interactionVersion++;
                pressedForeground = Native.GetAncestor(Native.GetForegroundWindow(), 3);
                pressedAt = clock.Elapsed.TotalSeconds;
                if (returning) FinishGhost();
                label.IsOpen = false; labelTimer.Stop();
                pressedIndex = HitIndex(x, y); pressedPoint = new() { X = x, Y = y };
                if (pressedIndex >= 0)
                {
                    renderer.SetPressed(pressedIndex, true);
                    var slot = geometry.Slots[pressedIndex];
                    gripX = Math.Clamp((x / scale - slot.X) / slot.Size, 0, 1);
                    gripY = Math.Clamp((y / scale - (geometry.Baseline(height / scale) - slot.Size)) / slot.Size, 0, 1);
                }
                if (pressedIndex >= 0 && !checkingInteraction) Native.SetCapture(hwnd);
                StartRendering();
                break;
            case 0x0202:
                NoteInteraction();
                interactionVersion++;
                int pressed = pressedIndex;
                renderer.SetPressed(pressed, false);
                nint? clickForeground = pressedForeground;
                pressedForeground = null;
                bool wasDragging = dragging;
                pressedIndex = -1;
                if (wasDragging) EndIconDrag(CanDropSort(x / scale, y / scale),
                    DockReorder.RemoveOnRelease(RestGeometry, width / scale, height / scale, x / scale, y / scale));
                Native.ReleaseCapture();
                bool moved = DockReorder.Moved((x - pressedPoint.X) / scale, (y - pressedPoint.Y) / scale);
                suppressHover = wasDragging || moved;
                if (!wasDragging && pressed >= 0 && pressed < entries.Count && !moved)
                {
                    var clicked = entries[pressed];
                    bool doubleClick = clicks.Complete(clicked.Target + "\n" + clicked.Arguments, pressedAt,
                        left + pressedPoint.X, top + pressedPoint.Y, Native.GetDoubleClickTime() / 1000d,
                        Native.GetSystemMetrics(36), Native.GetSystemMetrics(37));
                    Launch(clicked, foregroundAtPress: clickForeground, allowLaunch: doubleClick);
                }
                else clicks.Reset();
                StartRendering();
                break;
            case 0x0215:
            case 0x001F:
                if (pressedIndex >= 0 || dragging || startingDrag) CancelInteraction();
                break;
            case 0x0205: clicks.Reset(); NoteInteraction(); ShowMenu(HitIndex(x, y)); break;
            case Native.RecoverMessage: recover(); break;
            case Native.RestoreTaskbarMessage: RestoreTaskbarRequested?.Invoke(); break;
            case 0x0010: quit(); handled = true; break;
        }
        return 0;
    }

    private void ShowLabel()
    {
        labelTimer.Stop();
        if (hoveredIndex < 0 || hoveredIndex >= entries.Count || blocked || autoHidden || pressedIndex >= 0 || dragging || returning || externalDrop || !IsPointerOverDock()) return;
        if (UpdateWindowPreview(clock.Elapsed.TotalSeconds)) return;
        var slot = geometry.Slots[hoveredIndex];
        label.Content = entries[hoveredIndex].Name;
        label.HorizontalOffset = (left + (slot.X + slot.Size / 2) * scale) / scale - 38;
        label.VerticalOffset = (top + (geometry.Baseline(height / scale) - slot.Size - 40) * scale) / scale;
        label.IsOpen = true;
    }

    private void Launch(DockItem item, bool toggle = true, nint? foregroundAtPress = null, bool allowLaunch = true)
    {
        CloseWindowPreview();
        NoteInteraction();
        if (item.Target == "dock:settings") { if (toggle) toggleSettings(foregroundAtPress); else openSettings(); return; }
        if (item.Target == "dock:apps") { toggleApplications(); return; }
        try
        {
            launcher.Launch(item, toggle, foregroundAtPress, allowLaunch);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { System.Windows.MessageBox.Show("无法打开这个入口，请检查目标是否仍然存在。", "Desktop Dock"); }
    }

    private void ShowMenu(int index)
    {
        if (menuOpen || disposed) return;
        CloseWindowPreview();
        label.IsOpen = false; labelTimer.Stop(); hover.Reset(0); renderer.ResetPressFeedback(); StopRendering(); RenderLayout();
        nint menu = Native.CreatePopupMenu();
        if (menu == 0) return;
        var actions = new Dictionary<uint, Action>();
        void Add(string title, Action action, bool enabled = true)
        {
            uint id = (uint)actions.Count + 1; actions[id] = action;
            Native.AppendMenu(menu, enabled ? 0u : 3u, id, title);
        }
        if (IsPinnedIndex(index))
        {
            foreach (var itemAction in ItemMenuActions(entries[index])) Add(itemAction.Title, itemAction.Invoke, itemAction.Enabled);
            Native.AppendMenu(menu, 0x800, 0, null);
        }
        Add("添加应用或文件夹…", openSettings);
        Add("Dock 设置…", openSettings);
        Add("恢复桌面图标", recover);
        Native.AppendMenu(menu, 0x800, 0, null);
        Add("退出 Desktop Dock", quit);
        uint selected;
        menuOpen = true; menuForChecks = menu; CheckVisibility();
        try
        {
            Native.GetCursorPos(out var cursor);
            Native.SetForegroundWindow(Handle);
            selected = Native.TrackPopupMenuEx(menu, 0x100 | 2, cursor.X, cursor.Y, Handle, 0);
        }
        finally
        {
            menuOpen = false; menuForChecks = 0; Native.DestroyMenu(menu);
            Native.PostMessage(Handle, 0, 0, 0); NoteInteraction();
        }
        if (actions.TryGetValue(selected, out var action)) action();
        CheckVisibility();
    }

    private (string Title, Action Invoke, bool Enabled)[] ItemMenuActions(DockItem item)
    {
        var associated = launcher.WindowsFor(item);
        nint foreground = Native.GetAncestor(Native.GetForegroundWindow(), 3);
        nint closeWindow = associated.Contains(foreground) ? foreground : associated.FirstOrDefault();
        Native.GetWindowThreadProcessId(closeWindow, out uint closeProcess);
        return [
            ("打开 " + item.Name, () => Launch(item, false), true),
            ("关闭窗口", () => WindowClose.Request(closeWindow, closeProcess), closeWindow != 0),
            ("从 Dock 移除", () => RemoveEntry(item), true)
        ];
    }

    internal bool CloseFromMenuForChecks(DockItem item)
    {
        var actions = ItemMenuActions(item);
        var close = actions.Single(x => x.Title == "关闭窗口");
        bool enabled = close.Enabled && actions.All(x => x.Title != "退出应用…");
        if (enabled) close.Invoke();
        return enabled;
    }

    public void CheckVisibility()
    {
        if (disposed || suspended) return;
        double now = clock.Elapsed.TotalSeconds;
        bool switching = switchingForChecks ?? ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0
            && ((Native.GetAsyncKeyState(0x11) & 0x8000) == 0 || (Native.GetAsyncKeyState(0x09) & 0x8000) != 0));
        bool wasShellDragging = shellDrag.Active;
        bool shellDragging = shellDrag.Update(systemDragging, switching, scale, RestDockArea);
        if (!shellDragging) systemDragging = false;
        if (shellDragging)
        {
            labelTimer.Stop(); label.IsOpen = false; suppressHover = true;
            if (!wasShellDragging) { hover.Reset(0); renderer.ResetPressFeedback(); RenderLayout(); }
        }
        if (occlusionDirty || now - lastOcclusionScan >= 1)
        {
            cachedOccluded = occlusion.Covered(RestDockArea);
            occlusionDirty = false; lastOcclusionScan = now;
        }
        if ((pressedIndex >= 0 || dragging) && (switching || Native.GetAsyncKeyState(0x1B) < 0)) CancelInteraction();
        if (switching && menuOpen) Native.EndMenu();
        bool finishingDrop = pendingDropGeneration != 0 || now < dropVisibleUntil;
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        Native.GetCursorPos(out var cursor);
        bool atEdge = bottomEdgeForChecks ?? (cursor.X >= screen.Left && cursor.X < screen.Right && cursor.Y >= screen.Bottom - 2 && cursor.Y < screen.Bottom);
        bool interacting = pressedIndex >= 0 || dragging || returning || externalDrop || shellDragging || finishingDrop || menuOpen || applicationsOpen();
        bool previewOpen = UpdateWindowPreview(now, switching);
        interacting |= previewOpen;
        bool overPresented = motion.Progress > 0 && IsPointerOverDock();
        if (motion.Active && !motion.Target && overPresented && !switching) visibility.Summon(now);
        bool show = visibility.ShouldShow(cachedOccluded, switching, now, settings.AutoHide, atEdge,
            overPresented, interacting) && entries.Count > 0;
        show = (visibilityForChecks ?? show) && !switching && entries.Count > 0;
        bool wasTarget = motion.Target;
        if (!show)
        {
            blocked = cachedOccluded || !settings.AutoHide;
            autoHidden = !blocked;
            if (wasTarget)
            {
                CloseWindowPreview();
                StopRendering(); hover.Reset(0); renderer.ResetPressFeedback(); label.IsOpen = false;
                hideTransitions++;
            }
            motion.Request(false, switching || settings.ReducedMotion);
            return;
        }
        blocked = autoHidden = false;
        RefreshAppStates(now, force: !wasTarget);
        if (!wasTarget) RenderLayout();
        motion.Request(true, settings.ReducedMotion);
    }

    private DockAppState[] ReadAppStates()
    {
        nint foreground = Native.GetAncestor(Native.GetForegroundWindow(), 3);
        var states = launcher.GetStates(entries, foreground);
        nint settingsWindow = settingsHandle();
        int index = entries.FindIndex(x => x.Target == "dock:settings");
        if (index >= 0 && settingsWindow != 0 && Native.IsWindow(settingsWindow))
            states[index] = new(true, settingsWindow == foreground && !Native.IsIconic(settingsWindow));
        nint panel = applicationsHandle();
        int panelIndex = entries.FindIndex(x => x.Target == "dock:apps");
        if (panelIndex >= 0 && applicationsOpen() && panel != 0 && Native.IsWindowVisible(panel))
            states[panelIndex] = new(true, panel == foreground);
        return states;
    }

    private void RefreshAppStates(double now, bool force = false)
    {
        if (stateRefreshForChecks == false || !force && now - lastAppStateScan < (appStatesDirty ? .15 : 2)) return;
        appStatesDirty = false; lastAppStateScan = now; appStateScans++;
        if (!renderer.SetAppStates(ReadAppStates())) return;
        RenderLayout();
        if (!settings.ReducedMotion) StartRendering();
    }

    public void Summon()
    {
        visibility.Summon(clock.Elapsed.TotalSeconds);
        occlusionDirty = true;
        CheckVisibility();
    }
    private void NoteInteraction() => visibility.Interact(clock.Elapsed.TotalSeconds);
    public void RefreshOcclusion() { occlusionDirty = appStatesDirty = true; CheckVisibility(); }

    private void SetSuspended(bool value)
    {
        if (disposed || suspended == value) return;
        suspended = value;
        if (value)
        {
            blocked = true; visibilityTimer.Stop(); visibility.Reset();
            CancelInteraction(); StopRendering(); motion.Request(false, true);
            hover.Reset(0); renderer.ResetPressFeedback(); hoveredIndex = -1; tracking = false;
            labelTimer.Stop(); label.IsOpen = false; externalDrop = systemDragging = syntheticHover = false; insertion = -1;
            shellDrag.Reset();
            if (menuOpen) Native.EndMenu();
        }
        else { occlusionDirty = appStatesDirty = true; visibilityTimer.Start(); CheckVisibility(); }
        SuspensionChanged?.Invoke(value);
    }

    private WindowBounds RestDockArea
    {
        get
        {
            var rest = RestGeometry;
            int x = left + (int)(rest.BodyLeft(width / scale) * scale);
            return new(x, top + (int)(rest.BodyTop(height / scale) * scale), x + (int)(rest.Width * scale),
                top + height - (int)(DockGeometry.Foot * scale));
        }
    }

    private DockGeometry RestGeometry
    {
        get
        {
            var key = (Count: entries.Count, IconSize: settings.IconSize, Magnification: settings.Magnification,
                Width: (double)(width / scale), Separator: separatorBefore);
            if (restGeometry is null || restGeometryKey != key)
            {
                restGeometry = DockLayout.Calculate(key.Count, key.IconSize, key.Magnification, null, key.Width, separatorBefore: key.Separator);
                restGeometryKey = key;
            }
            return restGeometry;
        }
    }
    private bool IsPinnedIndex(int index) => index >= 1 && index <= settings.Items.Count && index < entries.Count - 1;
    private bool CanDropSort(double x, double y)
    {
        var rest = RestGeometry;
        return x >= rest.BodyLeft(width / scale) - 30 && x <= rest.BodyLeft(width / scale) + rest.Width + 30
            && y >= rest.BodyTop(height / scale) - 55 && y <= rest.Baseline(height / scale) + 35;
    }
    private void BeginIconDrag()
    {
        int index = pressedIndex, version = interactionVersion, generation = loadGeneration;
        if (startingDrag || dragging || disposed || pendingDropGeneration != 0 || index < 0
            || !IsPinnedIndex(index)) return;
        var item = entries[index];
        if (settings.Items[index - 1] != item || !iconCache.TryGetValue(item.Target, out var pixels)) return;
        bool Current() => !disposed && interactionVersion == version && loadGeneration == generation
            && pressedIndex == index && pendingDropGeneration == 0 && index < entries.Count - 1
            && IsPinnedIndex(index) && entries[index] == item && settings.Items[index - 1] == item;

        startingDrag = true;
        CloseWindowPreview();
        BitmapOverlay? prepared = null;
        try
        {
            // The gesture's frame loop already follows the pointer outside the
            // Dock. Activating or re-capturing here can synchronously cancel the
            // press; never use mutable pressedIndex after such a notification.
            var image = BitmapOverlay.Icon(pixels, IconService.Resolution, (int)Math.Round(RestGeometry.IconSize * scale));
            var removal = BitmapOverlay.Icon(pixels, IconService.Resolution, (int)Math.Round(RestGeometry.IconSize * scale), removing: true);
            prepared = new(image.Pixels, image.Side, image.Side);
            labelTimer.Stop(); label.IsOpen = false;
            dragStartInterruptionForChecks?.Invoke();
            if (!Current()) return;
            ghost = prepared; prepared = null; ghostMargin = image.Margin;
            ghostNormalPixels = image.Pixels; ghostRemovePixels = removal.Pixels; ghostRemoving = false;
            dragging = true; dragFrom = dragTo = ghostIndex = index;
            hover.Reset(0); renderer.ResetPressFeedback(); suppressHover = true;
            StartRendering();
        }
        finally { prepared?.Dispose(); startingDrag = false; }
    }
    private bool UpdateGhost(double now)
    {
        if (ghost is null) return false;
        double x, y;
        var rest = RestGeometry;
        if (dragging)
        {
            x = left + (dragX - gripX * rest.IconSize) * scale - ghostMargin;
            y = top + (dragY - gripY * rest.IconSize - 6) * scale - ghostMargin;
        }
        else if (returning)
        {
            if (ghostIndex < 0 || ghostIndex >= rest.Slots.Length) { FinishGhost(); return false; }
            double t = returnMotion.Sample(now);
            x = returnX + (left + rest.Slots[ghostIndex].X * scale - ghostMargin - returnX) * t;
            y = returnY + (top + (rest.Baseline(height / scale) - rest.Slots[ghostIndex].Size) * scale - ghostMargin + 3 - returnY) * t;
            if (!returnMotion.Active) { FinishGhost(); return false; }
        }
        else return false;
        bool moved = Math.Abs(x - ghostX) > .1 || Math.Abs(y - ghostY) > .1;
        bool removing = dragging && DockReorder.RemoveOnRelease(rest, width / scale, height / scale, dragX, dragY);
        bool badgeChanged = removing != ghostRemoving;
        if (badgeChanged)
        {
            ghostRemoving = removing;
            if ((removing ? ghostRemovePixels : ghostNormalPixels) is { } pixels) ghost.SetPixels(pixels);
        }
        ghostX = x; ghostY = y;
        if (moved || returning || badgeChanged) ghost.Place((int)Math.Round(x), (int)Math.Round(y), removing ? (byte)235 : (byte)255);
        return returning || moved;
    }
    private void EndIconDrag(bool commit, bool remove = false)
    {
        if (!dragging) return;
        if (!IsPinnedIndex(dragFrom) || entries[dragFrom] != settings.Items[dragFrom - 1])
        { dragging = false; FinishGhost(); return; }
        if (remove)
        {
            var item = entries[dragFrom];
            dragging = false; FinishGhost(false); RemoveEntry(item); suppressHover = true;
            return;
        }
        if (commit && dragFrom != dragTo && IsPinnedIndex(dragTo))
        {
            var item = settings.Items[dragFrom - 1]; settings.Items.RemoveAt(dragFrom - 1); settings.Items.Insert(dragTo - 1, item);
            var entry = entries[dragFrom]; entries.RemoveAt(dragFrom); entries.Insert(dragTo, entry);
            renderer.MoveIcon(dragFrom, dragTo); ghostIndex = dragTo;
            separatorBefore = entries.FindIndex(x => System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(x.Target)) || x.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
            changed();
        }
        dragging = false; returning = ghost is not null; sortingSettles = true; suppressHover = true;
        returnX = ghostX; returnY = ghostY;
        returnMotion.Reset(0); returnMotion.Begin(1, clock.Elapsed.TotalSeconds, settings.ReducedMotion ? .001 : .14);
        RenderLayout(); StartRendering();
    }
    private void RemoveEntry(DockItem item)
    {
        if (!settings.Items.Remove(item)) return;
        int index = entries.IndexOf(item);
        if (index >= 0) { entries.RemoveAt(index); renderer.RemoveIcon(index); }
        separatorBefore = entries.FindIndex(x => System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(x.Target)) || x.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
        changed(); RenderLayout();
        if (pendingDropGeneration != 0) _ = LoadIcons();
    }
    private void FinishGhost(bool render = true)
    {
        ghost?.Dispose(); ghost = null; ghostIndex = -1; returning = false;
        ghostNormalPixels = ghostRemovePixels = null; ghostRemoving = false;
        if (!disposed && render) RenderLayout();
    }
    private void CancelInteraction()
    {
        CloseWindowPreview();
        renderer.ReleasePressFeedback();
        clicks.Reset();
        interactionVersion++;
        pressedIndex = -1;
        pressedForeground = null;
        if (dragging) EndIconDrag(false);
        FinishGhost(); dragging = false; sortingSettles = false;
        if (Native.GetCapture() == Handle) Native.ReleaseCapture();
    }
    private void PreviewDrop(Native.Point point, bool allowed)
    {
        if (disposed || suspended) return;
        CloseWindowPreview();
        NoteInteraction();
        Native.ScreenToClient(Handle, ref point);
        externalDrop = allowed; insertion = allowed ? DockReorder.Insertion(RestGeometry, settings.Items.Count, point.X / scale, start: 1) + 1 : -1;
        hover.Reset(0); renderer.ResetPressFeedback(); suppressHover = true; labelTimer.Stop(); label.IsOpen = false;
        RenderLayout(); CheckVisibility();
    }
    private int AddDroppedFiles(IReadOnlyList<string> paths, Native.Point point)
    {
        if (disposed || suspended) return 0;
        Native.ScreenToClient(Handle, ref point);
        int index = DockReorder.Insertion(RestGeometry, settings.Items.Count, point.X / scale, start: 1), added = 0;
        foreach (string path in paths)
        {
            if (settings.Items.Count >= 40) break;
            if (settings.Items.Any(item => item.Target.Equals(path, StringComparison.OrdinalIgnoreCase) && item.Arguments.Length == 0)) continue;
            string name = System.IO.Directory.Exists(path) ? new System.IO.DirectoryInfo(path).Name : System.IO.Path.GetFileNameWithoutExtension(path);
            settings.Items.Insert(index++, new(name, path)); added++;
        }
        if (added > 0) { changed(); _ = LoadIcons(fromDrop: true); }
        return added;
    }
    private void LeaveDrop()
    {
        if (disposed) return;
        externalDrop = false; insertion = -1; suppressHover = true;
        RenderLayout(); CheckVisibility();
    }

    public void PreviewWave(double fraction = .5)
    {
        NoteInteraction();
        suppressHover = false;
        syntheticHover = true;
        var rest = RestGeometry;
        pointerTarget = rest.BodyLeft(width / scale) + rest.Width * fraction;
        if (hover.Value < .01) pointer.Reset(pointerTarget);
        StartRendering();
    }
    public void ResetWave() { syntheticHover = false; StartRendering(); }
    public void ClearFrameSamples() => frameIntervals.Clear();

    internal void OpenFromPanel(DockItem item) => Launch(item, toggle: false, allowLaunch: true);

    public void Dispose()
    {
        if (disposed) return;
        environment.Dispose();
        CancelInteraction(); disposed = true; StopRendering(); visibilityTimer.Stop(); labelTimer.Stop(); label.IsOpen = false;
        foreach (nint hook in hooks) if (hook != 0) Native.UnhookWinEvent(hook);
        if (menuOpen) Native.EndMenu();
        windowPreview?.Dispose();
        dropTarget.Dispose(); launcher.Dispose(); animationClock.Dispose(); motion.Dispose(); renderer.Dispose(); visualSource.Dispose(); source.Dispose();
    }
}
