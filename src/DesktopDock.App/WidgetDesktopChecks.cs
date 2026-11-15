using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopDock.Core;

namespace DesktopDock.App;

internal static class WidgetDesktopChecks
{
    internal static async Task<Dictionary<string, bool>> Run(string data, bool inspect)
    {
        Directory.CreateDirectory(data);
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name) { checks[name] = value; if (!value) throw new InvalidOperationException("Widget desktop: " + name); }
        var settings = new DockSettings(); int saves = 0;
        using var widgets = new DesktopWidgets(settings, () => saves++, data, network: false);
        widgets.Apply();
        var windows = widgets.WindowsForChecks.ToArray();
        var start = new TaskCompletionSource();
        var button = FeatureUi.Button("开始显示桌面检查", () => start.TrySetResult());
        button.HorizontalAlignment = HorizontalAlignment.Center; button.VerticalAlignment = VerticalAlignment.Center;
        var application = new Window { Title = "Desktop Dock · 桌面切换检查", Width = 900, Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = button, WindowState = WindowState.Maximized };
        application.Closed += (_, _) => start.TrySetCanceled();
        object? shell = null; bool showingDesktop = false;
        try
        {
            application.Show();
            nint handle = new WindowInteropHelper(application).Handle;
            if (inspect) await start.Task.WaitAsync(TimeSpan.FromMinutes(3));
            else { Native.ShowWindow(handle, 3); Native.SetForegroundWindow(handle); }
            widgets.RestackForChecks(); await Task.Delay(400);
            Require(windows.Length == 2, "ClockAndWeatherAreEnabled");
            Require(Exposed(handle), "FixtureApplicationIsExposedBeforeToggle");
            Require(windows.All(x => !Exposed(x.Handle)), "MaximizedApplicationCoversWidgets");
            Require(windows.All(x => Native.FindWindowEx(Native.GetWindow(x.Handle, 4), 0, "SHELLDLL_DefView", null) != 0), "WidgetsBelongToDesktopWindowGroup");
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"))!);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                // UndoMinimizeALL may leave Explorer foreground. A new user click starts the
                // next interactive cycle without bypassing Windows foreground activation rules.
                if (cycle != 0 && inspect)
                {
                    start = new(); button.Content = "继续第二轮检查";
                    await start.Task.WaitAsync(TimeSpan.FromMinutes(3));
                }
                else Native.SetForegroundWindow(handle);
                await Task.Delay(200);
                Require(Native.GetForegroundWindow() == handle, $"FixtureIsForegroundBeforeDesktopOperation{cycle}");
                // Exercise the real Shell desktop operation without injecting or registering Win+D.
                ((dynamic)shell!).ToggleDesktop(); showingDesktop = true;
                await Task.Delay(1000);
                Require(!Exposed(handle), $"DesktopOperationCoversApplication{cycle}");
                Require(windows.All(x => Exposed(x.Handle)), $"WidgetsStayVisibleOnDesktop{cycle}");
                Require(windows.All(x => (Native.GetWindowLongPtr(x.Handle, -20).ToInt64() & 8) == 0), $"WidgetsAreNotTopmost{cycle}");
                // This explicitly uses Shell's undo API; it is not a physical Win+D key test.
                ((dynamic)shell).UndoMinimizeALL(); showingDesktop = false;
                await Task.Delay(1000);
                Require(Exposed(handle), $"SystemUndoRestoresApplication{cycle}");
                Require(windows.All(x => !Exposed(x.Handle)), $"RestoredApplicationCoversWidgets{cycle}");
            }
            Require(widgets.WindowsForChecks.SequenceEqual(windows) && saves == 0, "DesktopSwitchReusesWindowsWithoutSavingSettings");
            await Task.Delay(600);
            Require(!widgets.RestackPendingForChecks, "RestackingSettlesWithoutPolling");
            widgets.SetSuspended(true);
            Require(!widgets.RestackPendingForChecks && windows.All(x => !x.IsVisible), "SuspensionStopsRestackingAndHidesWidgets");
        }
        finally
        {
            if (showingDesktop && shell is not null) { ((dynamic)shell).UndoMinimizeALL(); await Task.Delay(600); }
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            application.Close();
        }
        return checks;
    }

    private static bool Exposed(nint window)
    {
        if (!Native.IsWindowVisible(window) || Native.IsIconic(window)) return false;
        if (Native.DwmGetIntAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        Native.GetWindowRect(window, out var bounds);
        var point = new Native.Point { X = (bounds.Left + bounds.Right) / 2, Y = bounds.Top + (bounds.Bottom - bounds.Top) / 3 };
        return Native.GetAncestor(Native.WindowFromPoint(point), 2) == window;
    }
}
