using System.IO;
using DesktopDock.Core;

namespace DesktopDock.App;

internal static class BrowserWindowChecks
{
    public static async Task<Dictionary<string, bool>> Run(string data)
    {
        var checks = new Dictionary<string, bool>();
        void Require(bool value, string name)
        {
            checks[name] = value;
            if (!value) throw new InvalidOperationException("Pre-existing browser regression: " + name);
        }
        async Task WaitFor(Func<bool> condition, string name)
        {
            for (int i = 0; i < 60 && !condition(); i++) await Task.Delay(50);
            Require(condition(), name);
        }
        // A renamed copy of our own probe emulates the executable identity only;
        // it never starts, closes or reads a user's real browser.
        string application = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        string fixture = Path.Combine(data, "browser-fixture");
        Directory.CreateDirectory(fixture);
        foreach (string file in Directory.EnumerateFiles(application))
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        string browser = Path.Combine(fixture, "chrome.exe");
        File.Copy(Path.Combine(application, "DesktopDock.exe"), browser, true);
        string probeData = Path.Combine(data, "already-open");
        using var process = RegressionProbe.Start("normal", probeData, browser);
        try
        {
            await RegressionProbe.WaitReady(process, probeData);
            nint window = RegressionProbe.Handle(probeData);
            const string metadata = "--source-shortcut-location=desktop";
            string link = Path.Combine(data, "Example browser.lnk");
            RegressionProbe.CreateShortcut(link, browser, metadata);
            foreach (bool shortcut in new[] { false, true })
            {
                string prefix = shortcut ? "Shortcut" : "Executable";
                var item = shortcut ? new DockItem("Example browser", link) : new DockItem("Example browser", browser, metadata);
                var target = ApplicationTarget.Resolve(item.Target, item.Arguments);
                Require(!target.SpecificWindow && target.ExecutablePath == browser, prefix + "MetadataKeepsApplicationIdentity");
                using var launcher = new LaunchCoordinator(); // Created after the window; no launch/binding history.
                Require(launcher.GetStates([item], window)[0] == new DockAppState(true, true), prefix + "PreExistingWindowHasActiveRunningState");
                launcher.Launch(item, foregroundAtPress: 0, allowLaunch: false);
                Require(launcher.LastWindow == window && launcher.LastAction == AppClickAction.Activate && !launcher.IsWatching,
                    prefix + "SingleClickActivatesPreExistingWindow");
                launcher.Launch(item, foregroundAtPress: window, allowLaunch: false);
                await WaitFor(() => Native.IsIconic(window), prefix + "RepeatClickMinimizesSameWindow");
                Require(launcher.GetStates([item], window)[0] == new DockAppState(true, false), prefix + "MinimizedWindowKeepsRunningState");
                using var restartedDock = new LaunchCoordinator();
                restartedDock.Launch(item, allowLaunch: true);
                Require(restartedDock.LastWindow == window && restartedDock.LastAction == AppClickAction.Restore && !restartedDock.IsWatching,
                    prefix + "FreshCoordinatorRestoresSameWindow");
                await WaitFor(() => !Native.IsIconic(window), prefix + "RestoredWindowIsVisible");
                Require(restartedDock.GetStates([item], 0)[0] == new DockAppState(true, false), prefix + "BackgroundWindowKeepsRunningState");
                restartedDock.Launch(item, toggle: false, allowLaunch: true);
                Require(restartedDock.LastWindow == window && restartedDock.LastAction == AppClickAction.Activate && !restartedDock.IsWatching,
                    prefix + "OpenExistingNeverStartsAnotherProcess");
                Require(!launcher.Matches(window, 0, target with { ExecutablePath = Path.Combine(data, "other-install", "chrome.exe") }),
                    prefix + "OtherInstallationIsNotMatched");
                Require(!launcher.GetStates([new("Other installation", Path.Combine(data, "other-install", "chrome.exe"))], window)[0].Running,
                    prefix + "OtherInstallationHasNoRunningMarker");
            }
            foreach (var (name, specific) in new[] { ("Url", "https://example.invalid/"), ("Profile", "--profile-directory=\"Example Profile\""), ("WebApp", "--app-id=example") })
            {
                using var launcher = new LaunchCoordinator();
                launcher.Launch(new("Example specific entry", browser, metadata + " " + specific), allowLaunch: false);
                Require(launcher.LastAction == AppClickAction.Ignore && !launcher.IsWatching, name + "EntryDoesNotSelectUnrelatedWindow");
                Require(!launcher.GetStates([new("Example specific entry", browser, metadata + " " + specific)], window)[0].Running,
                    name + "UnboundEntryHasNoRunningMarker");
            }
            Require(!process.HasExited && Native.IsWindow(window), "OriginalBrowserFixtureRemainsRunning");
            using var status = new LaunchCoordinator();
            var plain = new DockItem("Example browser", browser);
            Native.ShowWindowAsync(window, 0);
            await WaitFor(() => !Native.IsWindowVisible(window), "FixtureCanHideItsMainWindow");
            Require(status.GetStates([plain], window)[0] == default, "HiddenMainWindowHasNoRunningMarker");
            Native.PostMessage(window, 0x0010, 0, 0);
            await WaitFor(() => !Native.IsWindow(window), "FixtureClosesForStatusCheck");
            Require(status.GetStates([plain], 0)[0] == default, "ClosedWindowRemovesRunningMarker");
        }
        finally { RegressionProbe.Close(process); }
        string shellHost = Path.Combine(fixture, "explorer.exe");
        File.Copy(Path.Combine(application, "DesktopDock.exe"), shellHost, true);
        string shellData = Path.Combine(data, "shell-helper");
        using var helper = RegressionProbe.Start("normal", shellData, shellHost);
        try
        {
            await RegressionProbe.WaitReady(helper, shellData);
            using var state = new LaunchCoordinator();
            // A user's real Explorer window can remain open during the run.
            // Only our helper is evidence for this auxiliary-window check.
            Require(!state.GetStates([new("File Explorer", shellHost)], windowsForChecks: [RegressionProbe.Handle(shellData)])[0].Running, "ExplorerHelperIsNotAnOpenFileBrowser");
        }
        finally { RegressionProbe.Close(helper); }
        return checks;
    }
}
