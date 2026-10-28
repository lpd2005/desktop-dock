using System.Text.Json;

namespace DesktopDock.Core;

public sealed record UpdateRelease(ReleaseVersion Version, string Url, string Sha256, long Size, string Notes)
{
    public string FileName => $"DesktopDock-{Version}-Setup-x64.exe";
}

public static class ReleaseFeed
{
    public const string Repository = "lpd2005/desktop-dock";
    public const string Page = "https://github.com/" + Repository + "/releases";
    public const string Api = "https://api.github.com/repos/" + Repository + "/releases?per_page=100";
    public const long MaximumBytes = 512L * 1024 * 1024;
    public static UpdateRelease? Latest(string json, ReleaseVersion current)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid release feed.");
        UpdateRelease? latest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft")) continue;
            string tag = Text(release, "tag_name");
            if (!ReleaseVersion.TryParse(tag, out var version) || tag != "v" + version || version.CompareTo(current) <= 0) continue;
            if (!current.IsPrerelease && (version.IsPrerelease || Flag(release, "prerelease"))) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            string expected = $"DesktopDock-{version}-Setup-x64.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != expected || Text(asset, "state") != "uploaded") continue;
                string digest = Text(asset, "digest"), url = Text(asset, "browser_download_url");
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) continue;
                if (url != $"https://github.com/{Repository}/releases/download/{tag}/{expected}") continue;
                if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes is <= 0 or > MaximumBytes) continue;
                if (latest is not null && version.CompareTo(latest.Version) <= 0) continue;
                string notes = Text(release, "body");
                latest = new(version, url, digest[7..].ToLowerInvariant(), bytes, notes[..Math.Min(notes.Length, 8000)]);
            }
        }
        return latest;
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
