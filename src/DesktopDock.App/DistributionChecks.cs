using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopDock.Core;
using Microsoft.Win32;

namespace DesktopDock.App;

internal static class DistributionChecks
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    internal static async Task<Dictionary<string, bool>> Run(string data)
    {
        Directory.CreateDirectory(data);
        var checks = new Dictionary<string, bool>();
        void Require(bool condition, string name) { checks[name] = condition; if (!condition) throw new InvalidOperationException("Distribution: " + name); }
        async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 80 && !condition(); i++) await Task.Delay(30);
            if (!condition()) throw new InvalidOperationException("Distribution UI did not settle.");
        }
        string registryPath = @"Software\DesktopDockChecks\" + Guid.NewGuid().ToString("N");
        var startup = new StartupRegistration(Environment.ProcessPath!, registryPath);
        try
        {
            Require(!startup.Enabled, "StartupIsInitiallyDisabled");
            startup.Set(true); Require(startup.Enabled, "StartupCanBeEnabledForCurrentUser");
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                Require((string?)key?.GetValue("DesktopDock") == "\"" + Environment.ProcessPath + "\" --startup", "StartupCommandQuotesExecutableAndUsesStartupMode");
            startup.Set(false); Require(!startup.Enabled, "StartupCanBeRemoved");
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath)) key.SetValue("DesktopDock", "unrelated-command");
            startup.Set(false);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath)) Require((string?)key?.GetValue("DesktopDock") == "unrelated-command", "StartupDoesNotDeleteUnrelatedValue");
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true)) key!.DeleteValue("DesktopDock");

            byte[] payload = Encoding.UTF8.GetBytes("Synthetic update payload. Never execute this file.");
            string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string Feed(string digest, int size) => JsonSerializer.Serialize(new[] { new {
                tag_name = "v0.1.0-preview.23", draft = false, prerelease = true, body = "Example release notes",
                assets = new[] { new { name = "DesktopDock-0.1.0-preview.23-Setup-x64.exe", state = "uploaded", size,
                    digest = "sha256:" + digest, browser_download_url = "https://github.com/lpd2005/desktop-dock/releases/download/v0.1.0-preview.23/DesktopDock-0.1.0-preview.23-Setup-x64.exe" } }
            } });
            bool downloading = false, blockDownload = false; string digest = hash; int size = payload.Length; bool rateLimited = false;
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (rateLimited) return new(HttpStatusCode.Forbidden) { RequestMessage = request };
                bool api = request.RequestUri!.Host == "api.github.com";
                if (!api)
                {
                    downloading = true;
                    if (blockDownload) await Task.Delay(Timeout.Infinite, token);
                }
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = api ? new StringContent(Feed(digest, size)) : new ByteArrayContent(payload) };
            }));
            using var updates = new UpdateService(Path.Combine(data, "valid"), client, "0.1.0-preview.22");
            int notifications = 0; updates.AutomaticUpdateFound += _ => notifications++;
            await updates.CheckAsync(automatic: true);
            Require(updates.Stage == UpdateStage.Available && updates.Latest?.Version.ToString() == "0.1.0-preview.23", "ReleaseCheckFindsNewPreviewVersion");
            Require(!downloading && updates.Latest!.Notes == "Example release notes", "CheckingDoesNotDownloadOrInstallAutomatically");
            await updates.CheckAsync(automatic: true); Require(notifications == 1, "AutomaticNotificationIsDeduplicated");
            await updates.DownloadAsync(); Require(updates.Stage == UpdateStage.Ready && updates.Progress == 100, "InstallerDownloadVerifiesSizeAndSha256");
            Require(await updates.VerifyInstallerAsync(), "DownloadedInstallerCanBeReverified");
            string installer = Path.Combine(data, "valid", "updates", updates.Latest!.FileName);
            File.WriteAllBytes(installer, new byte[payload.Length]);
            Require(!await updates.VerifyInstallerAsync(), "TamperedInstallerIsRejectedBeforeExecution");
            Require(!await updates.InstallAsync() && updates.Stage == UpdateStage.Error, "InstallDoesNotExecuteTamperedPayload");
            await updates.DownloadAsync(); Require(await updates.VerifyInstallerAsync(), "RetryReplacesOnlyVerifiedInstaller");
            Require(!Directory.EnumerateFiles(Path.GetDirectoryName(installer)!, "*.download").Any(), "CompletedDownloadLeavesNoPartialFile");

            digest = new string('0', 64);
            using (var broken = new UpdateService(Path.Combine(data, "bad-hash"), client, "0.1.0-preview.22"))
            {
                await broken.CheckAsync(); await broken.DownloadAsync();
                Require(broken.Stage == UpdateStage.Error && !await broken.VerifyInstallerAsync(), "IncorrectHashCannotBecomeInstallable");
            }
            digest = hash; size = payload.Length + 1;
            using (var truncated = new UpdateService(Path.Combine(data, "bad-size"), client, "0.1.0-preview.22"))
            {
                await truncated.CheckAsync(); await truncated.DownloadAsync();
                Require(truncated.Stage == UpdateStage.Error, "IncorrectDownloadSizeIsRejected");
            }
            size = payload.Length; blockDownload = true; downloading = false;
            using (var canceled = new UpdateService(Path.Combine(data, "canceled"), client, "0.1.0-preview.22"))
            {
                await canceled.CheckAsync(); var pending = canceled.DownloadAsync(); await WaitFor(() => downloading);
                canceled.Cancel(); await pending;
                Require(canceled.Stage == UpdateStage.Available && !Directory.EnumerateFiles(Path.Combine(data, "canceled", "updates")).Any(), "CancelLeavesNoExecutableOrPartialDownload");
            }
            blockDownload = false; rateLimited = true;
            using (var limited = new UpdateService(Path.Combine(data, "limited"), client, "0.1.0-preview.22"))
            {
                await limited.CheckAsync(); Require(limited.Stage == UpdateStage.Error && limited.Status.Contains("限制请求"), "RateLimitHasActionableStatus");
            }
            rateLimited = false;
            var settings = new DockSettings(); int saved = 0;
            var page = new SystemSettingsPage(settings, updates, startup, () => saved++, () => throw new InvalidOperationException("A check must not quit the application."));
            using var taskbar = new TaskbarEnhancement(Path.Combine(data, "settings"));
            var window = new SettingsWindow(settings, () => { }, () => { }, () => { }, taskbar, page);
            try
            {
                window.Show(); window.SelectPageForChecks(4); window.UpdateLayout();
                await WaitFor(() => page.IsLoaded);
                Require(page.IsLoaded && Children<Button>(page).Any(x => x.Content as string == "检查更新"), "StartupAndUpdatesSettingsPageRenders");
                var startupBox = Children<CheckBox>(page).Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "开机自启");
                startupBox.IsChecked = true; startupBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(startup.Enabled, "StartupSwitchWritesRegistration");
                startupBox.IsChecked = false; startupBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(!startup.Enabled, "StartupSwitchRemovesRegistration");
                var automatic = Children<CheckBox>(page).Single(x => x.Content as string == "自动检查新版本（每天最多一次）");
                automatic.IsChecked = false; automatic.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(!settings.AutoCheckUpdates && saved == 1, "AutomaticCheckSwitchPersistsWithoutChangingDock");
                Require(Children<Button>(page).Single(x => x.Content as string == "安装并重启").Visibility == Visibility.Visible, "VerifiedDownloadShowsExplicitInstallAction");
                window.UpdateLayout();
                var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                picture.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
                using var capture = File.Create(Path.Combine(data, "startup-updates-settings.png")); encoder.Save(capture);
            }
            finally { window.Close(); }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); }
        return checks;
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T target) yield return target;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
