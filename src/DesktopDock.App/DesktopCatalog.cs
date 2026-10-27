using DesktopDock.Core;
using Microsoft.Win32;

namespace DesktopDock.App;

internal static class DesktopCatalog
{
    public static DockItem[] Read()
    {
        var items = DesktopLibrary.Read([Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)]).ToList();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\NewStartPanel");
            void Add(string id, bool visibleByDefault, string name, string target)
            {
                bool visible = key?.GetValue(id) is int hidden ? hidden == 0 : visibleByDefault;
                if (visible) items.Add(new(name, target));
            }
            Add("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false, "此电脑", "shell:MyComputerFolder");
            Add("{645FF040-5081-101B-9F08-00AA002F954E}", true, "回收站", "shell:RecycleBinFolder");
            Add("{59031a47-3f72-44a7-89c5-5595fe6b30ee}", false, "用户文件", "shell:UsersFilesFolder");
            Add("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", false, "控制面板", "shell:ControlPanelFolder");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        return items.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
