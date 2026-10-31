using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace DesktopDock.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDock");
        int dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < args.Length) data = Path.GetFullPath(args[dataIndex + 1]);
        bool automated = args.Any(x => x is "--smoke-test" or "--drag-test" or "--click-hover-test" or "--browser-window-test" or "--feature-test" or "--window-preview-test" or "--distribution-test" or "--environment-test");
        bool preview = args.Contains("--preview") || automated;
        Directory.CreateDirectory(data);
        if (args.Contains("--taskbar-guard")) return TaskbarRecovery.RunGuard(args, data);
        if (args.Contains("--taskbar-test")) return TaskbarChecks.Run(data);
        if (args.Contains("--taskbar-exit-probe")) return TaskbarChecks.RunExitProbe(data);
        if (args.Contains("--inspect-taskbar"))
        {
            File.WriteAllText(Path.Combine(data, "taskbar-inspection.json"), JsonSerializer.Serialize(TaskbarNative.Capture(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        int probeIndex = Array.IndexOf(args, "--probe-window");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            int delayIndex = Array.IndexOf(args, "--probe-delay-ms");
            if (delayIndex >= 0 && delayIndex + 1 < args.Length && int.TryParse(args[delayIndex + 1], out int delay))
                Thread.Sleep(Math.Clamp(delay, 0, 2000));
            int grantIndex = Array.IndexOf(args, "--allow-foreground");
            uint parent = grantIndex >= 0 && grantIndex + 1 < args.Length && uint.TryParse(args[grantIndex + 1], out uint parsed) ? parsed : 0;
            return RegressionProbe.Run(args[probeIndex + 1], data, parent);
        }
        using var desktop = new DesktopIcons(data);
        if (args.Contains("--quit"))
        {
            nint live = Native.FindWindow(null, "Desktop Dock");
            if (live != 0) Native.PostMessage(live, 0x0010, 0, 0);
            return 0;
        }
        if (args.Contains("--verify-recovery"))
        {
            desktop.Recover();
            var result = desktop.VerifyRoundTrip();
            File.WriteAllText(Path.Combine(data, "recovery-report.json"), JsonSerializer.Serialize(result));
            return 0;
        }
        if (args.Contains("--recover"))
        {
            nint live = Native.FindWindow(null, "Desktop Dock");
            if (live != 0) { Native.PostMessage(live, Native.RecoverMessage, 0, 0); Native.PostMessage(live, Native.RestoreTaskbarMessage, 0, 0); }
            else { desktop.Recover(); TaskbarRecovery.Restore(data); }
            return 0;
        }
        if (args.Contains("--recover-taskbar"))
        {
            nint live = Native.FindWindow(null, "Desktop Dock");
            if (live != 0) Native.PostMessage(live, Native.RestoreTaskbarMessage, 0, 0);
            else TaskbarRecovery.Restore(data);
            return 0;
        }
        using var mutex = new Mutex(true, "Local\\DesktopDock.SingleInstance", out bool created);
        if (!created) return 0;
        nint queue = 0;
        Controller? controller = null;
        try
        {
            var options = new Native.QueueOptions { Size = Marshal.SizeOf<Native.QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(Native.CreateDispatcherQueueController(options, out queue));
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.DispatcherUnhandledException += (_, e) =>
            {
                desktop.Restore();
                File.WriteAllText(Path.Combine(data, "last-error.txt"), e.Exception.ToString());
                application.Shutdown(1);
                e.Handled = true;
            };
            desktop.Recover();
            TaskbarRecovery.Restore(data);
            controller = new(application, data, desktop, preview, args.Contains("--stress"), showWidgets: !automated);
            application.Startup += async (_, _) =>
            {
                if (args.Contains("--startup"))
                {
                    for (int i = 0; i < 40 && !DesktopIcons.ShellReady; i++) await Task.Delay(500);
                    desktop.Recover(); controller.RepositionAfterStartup();
                }
                if (args.Contains("--about")) controller.OpenAbout();
                else if (args.Contains("--updates")) controller.OpenUpdates();
                else if (args.Contains("--settings")) controller.OpenSettings();
                await controller.Start();
                if (args.Contains("--applications")) controller.ToggleApplications();
                if (args.Contains("--smoke-test")) await controller.RunSmokeTest();
                if (args.Contains("--drag-test")) await controller.RunDragTest(args.Contains("--hold-drag"));
                if (args.Contains("--click-hover-test")) await controller.RunClickHoverTest();
                if (args.Contains("--browser-window-test")) await controller.RunBrowserWindowTest();
                if (args.Contains("--feature-test")) await controller.RunFeatureTest(args.Contains("--inspect-features"));
                if (args.Contains("--window-preview-test")) await controller.RunWindowPreviewTest(args.Contains("--inspect-previews"));
                if (args.Contains("--distribution-test")) await controller.RunDistributionTest();
                if (args.Contains("--environment-test")) await controller.RunEnvironmentTest();
            };
            return application.Run();
        }
        catch (Exception e)
        {
            desktop.Restore();
            File.WriteAllText(Path.Combine(data, "last-error.txt"), e.ToString());
            if (!automated) System.Windows.MessageBox.Show("Dock 无法启动。桌面已尝试恢复。诊断信息保存在本机应用数据目录。", "Desktop Dock");
            return 1;
        }
        finally
        {
            controller?.Dispose();
            desktop.Restore();
            if (queue != 0) Marshal.Release(queue);
        }
    }
}
