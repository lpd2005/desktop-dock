using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopDock.Core;

namespace DesktopDock.App;

internal sealed partial class Controller
{
    internal static async Task<Dictionary<string, bool>> CheckSettingsRecovery(string data)
    {
        Directory.CreateDirectory(data);
        string path = Path.Combine(data, "settings.json");
        SettingsStore.Save(path, new() { IconSize = 40, Items = [new("Example", "ms-settings:")] });
        SettingsStore.Save(path, new() { IconSize = 56 });
        string backup = File.ReadAllText(path + ".bak");
        File.Delete(path);
        var checks = new Dictionary<string, bool>();
        void Require(bool condition, string name) { checks[name] = condition; if (!condition) throw new InvalidOperationException("Settings recovery regression: " + name); }
        IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var descendant in Children<T>(child)) yield return descendant;
            }
        }
        using var desktop = new DesktopIcons(data);
        using var controller = new Controller(System.Windows.Application.Current, data, desktop, preview: false, stress: false, showWidgets: false);
        try
        {
            Require(controller.recoveryRequired && controller.settings.Items.Count == 1 && controller.settings.IconSize == 40, "MissingPrimaryLoadsBackupWithoutFirstRunSeeding");
            controller.OpenSettings();
            var window = controller.settingsWindow!; window.UpdateLayout(); await Task.Delay(40);
            Require(Children<Button>(window).Any(x => x.Content as string == "确认恢复并保存"), "RecoveryPromptAppearsBeforeAnySave");
            var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            picture.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using (var capture = File.Create(Path.Combine(data, "settings-recovery.png"))) encoder.Save(capture);
            File.WriteAllText(path, "{controller-fixture-damaged");
            Children<Slider>(window).First().Value = 52; window.UpdateLayout();
            Require(File.ReadAllText(path) == "{controller-fixture-damaged" && File.ReadAllText(path + ".bak") == backup, "UnconfirmedChangesPreserveOriginalAndBackup");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Children<Button>(window).Single(x => x.Content as string == "确认恢复并保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Require(window.IsVisible && controller.recoveryRequired, "FailedRecoveryKeepsSettingsWindowUsable");
            }
            Children<Button>(window).Single(x => x.Content as string == "确认恢复并保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Require(!controller.recoveryRequired && SettingsStore.Load(path).IconSize == 52 && File.ReadAllText(path + ".bak") == backup, "RecoveryButtonPreservesBackupAndSavesPendingChanges");
            Require(Directory.GetFiles(data, "settings.json.corrupt-*").Any(x => File.ReadAllText(x) == "{controller-fixture-damaged"), "RecoveryButtonArchivesDamagedOriginal");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Children<Slider>(window).First().Value = 60; window.UpdateLayout();
                Require(window.IsVisible && Children<Button>(window).Any(x => x.Content as string == "重试保存")
                    && Children<TextBlock>(window).Any(x => x.Text == "更改尚未保存"), "FailedSaveDoesNotExitOrClaimSaved");
            }
            Require(SettingsStore.Load(path).IconSize == 52 && File.ReadAllText(path + ".bak") == backup, "FailedSaveLeavesDiskConfigurationsUntouched");
            Children<Button>(window).Single(x => x.Content as string == "重试保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Require(SettingsStore.Load(path).IconSize == 60 && SettingsStore.Load(path + ".bak").IconSize == 52
                && !Children<Button>(window).Any(x => x.Content as string == "重试保存"), "SaveRetryClearsNoticeAndRetainsPreviousVersion");
        }
        finally { controller.settingsWindow?.Close(); }
        return checks;
    }
}
