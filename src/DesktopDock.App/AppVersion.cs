using System.Reflection;

namespace DesktopDock.App;

internal static class AppVersion
{
    public static string Current { get; } = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string Short { get; } = FormatShort();

    private static string FormatShort()
    {
        string[] parts = Current.Split('-', 2);
        var version = Version.Parse(parts[0]);
        return $"{version.Major}.{version.Minor}" + (parts.Length == 2 ? " " + parts[1] : "");
    }
}
