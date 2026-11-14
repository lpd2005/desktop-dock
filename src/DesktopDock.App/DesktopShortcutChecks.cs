using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Text.Json;

namespace DesktopDock.App;

internal static class DesktopShortcutChecks
{
    internal static async Task<Dictionary<string, bool>> Run(string data, bool inspect)
    {
        Directory.CreateDirectory(data);
        var checks = new Dictionary<string, bool>();
        void Require(bool condition, string name) { checks[name] = condition; if (!condition) throw new InvalidOperationException("Desktop shortcut: " + name); }
        using var first = new HwndSource(new HwndSourceParameters("Desktop shortcut check A") { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
        using var second = new HwndSource(new HwndSourceParameters("Desktop shortcut check B") { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
        int calls = 0; bool success = true;
        using var shortcut = new DesktopShortcut(first, () => { calls++; return success; }, runtimeAllowed: true);
        void Press(int message = DesktopShortcut.Message, int id = DesktopShortcut.Id, nint? payload = null)
            => Native.SendMessage(first.Handle, message, id, payload ?? DesktopShortcut.Payload);
        shortcut.Apply(true);
        Require(shortcut.Registered, "GlobalChordRegistersOnExistingWindow");
        Require(!Native.IsWindowVisible(first.Handle), "HotkeyOwnerCanStayHidden");
        Press(); Press();
        Require(calls == 2, "HiddenOwnerDispatchesEachPressWithoutToggleLogic");
        Press(id: DesktopShortcut.Id + 1); Press(payload: new nint(0)); Press(message: 0x0400);
        Require(calls == 2, "UnrelatedMessagesAndPayloadsAreIgnored");
        shortcut.SetSuspended(true); Press();
        Require(calls == 2 && shortcut.Registered, "SuspendedSessionIgnoresQueuedShortcut");
        shortcut.SetSuspended(false); Press(); Require(calls == 3, "SessionResumeReenablesShortcut");
        success = false; Press();
        Require(calls == 4 && shortcut.Status.Contains("暂时无法"), "ShellFailureIsReportedWithoutUnregistering");
        success = true; Press(); Require(calls == 5 && shortcut.Status.Contains("已启用"), "NextPressCanRecoverAfterShellFailure");
        using var competitor = new DesktopShortcut(second, () => true, runtimeAllowed: true);
        competitor.Apply(true);
        Require(!competitor.Registered && competitor.Status.Contains("占用"), "ConflictingRegistrationShowsExplanation");
        shortcut.Apply(false); Press();
        Require(!shortcut.Registered && calls == 5, "DisablingUnregistersAndRejectsStaleMessages");
        competitor.Apply(true); Require(competitor.Registered, "DisabledShortcutReleasesChordForOtherOwner");
        competitor.Dispose(); shortcut.Apply(true);
        Require(shortcut.Registered, "DisposalReleasesChordForReregistration");
        shortcut.Apply(true); Press(); Require(calls == 6, "RepeatedApplyDoesNotDuplicateDispatch");
        shortcut.Dispose(); Press(); Require(calls == 6 && !shortcut.Registered, "DisposedOwnerRemovesMessageHook");
        using var preview = new DesktopShortcut(first, () => { calls++; return true; }, runtimeAllowed: false);
        preview.Apply(true); Press();
        Require(!preview.Registered && calls == 6 && preview.Status.Contains("安全预览"), "SafePreviewCannotRegisterOrRunGlobalAction");
        if (inspect)
        {
            int keyboardCalls = 0;
            var label = FeatureUi.Text("请按 Ctrl + Alt + Shift + D\n隔离验证：仅计数，不最小化其他窗口。", 19);
            label.Margin = new(28); label.TextWrapping = TextWrapping.Wrap;
            var window = new Window { Title = "Desktop Dock · 返回桌面快捷键验证", Width = 650, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = label };
            window.Show();
            using var physical = new DesktopShortcut(HwndSource.FromHwnd(new WindowInteropHelper(window).Handle), () =>
            {
                keyboardCalls++; label.Text = "快捷键已触发 " + keyboardCalls + " 次\n隔离验证：其他窗口保持原状。";
                File.WriteAllText(Path.Combine(data, "keyboard-count.json"), JsonSerializer.Serialize(new { Count = keyboardCalls }));
                return true;
            }, runtimeAllowed: true);
            try
            {
                physical.Apply(true); Require(physical.Registered, "VisibleKeyboardFixtureRegistersChord");
                string marker = Path.Combine(data, "visual-inspection.pause"); File.WriteAllText(marker, "Local keyboard inspection.");
                for (int i = 0; i < 600 && File.Exists(marker); i++) await Task.Delay(500);
            }
            finally { window.Close(); }
        }
        return checks;
    }
}
