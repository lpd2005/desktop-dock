namespace DesktopDock.Core;

public static class LaunchArguments
{
    public static bool IsChromiumBrowser(string? processName) => processName?.ToLowerInvariant()
        is "chrome" or "chromium" or "msedge" or "brave" or "vivaldi" or "opera";

    public static bool RequiresWindowBinding(string? processName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        if (!IsChromiumBrowser(processName)) return true;
        const string source = "--source-shortcut-location=";
        foreach (string token in arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith(source, StringComparison.Ordinal)) return true;
            string value = token[source.Length..];
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return true;
        }
        return false;
    }
}
