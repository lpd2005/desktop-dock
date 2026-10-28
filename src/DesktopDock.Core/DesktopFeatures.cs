using System.Globalization;
using System.Text.Json;

namespace DesktopDock.Core;

public sealed class DesktopGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分组";
    public List<string> Targets { get; set; } = [];
}

public sealed record WidgetPosition(double X, double Y);
public sealed class WidgetOptions
{
    public bool Clock { get; set; } = true;
    public bool Weather { get; set; } = true;
    public bool Light { get; set; } = true;
    public string City { get; set; } = "洛阳";
    public Dictionary<string, WidgetPosition> Positions { get; set; } = [];
    public void Normalize()
    {
        City = string.IsNullOrWhiteSpace(City) ? "洛阳" : City.Trim()[..Math.Min(City.Trim().Length, 80)];
        Positions = (Positions ?? []).Where(x => x.Key is "clock" or "weather" && x.Value is not null
            && double.IsFinite(x.Value.X) && double.IsFinite(x.Value.Y))
            .ToDictionary(x => x.Key, x => new WidgetPosition(Math.Clamp(x.Value.X, 0, 20000), Math.Clamp(x.Value.Y, 0, 20000)));
    }
}

public static class DesktopLibrary
{
    public static readonly (string Id, string Name, string Glyph)[] Presets =
    [ ("apps", "应用", "▦"), ("folders", "文件夹", "▱"), ("documents", "文档", "≡"), ("media", "媒体", "▷"), ("other", "其他", "⋯") ];

    public static string Category(DockItem item, bool directory = false)
    {
        if (directory) return "folders";
        if (item.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || item.Target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return "apps";
        return Path.GetExtension(item.Target).ToLowerInvariant() switch
        {
            ".lnk" or ".url" or ".exe" or ".appref-ms" => "apps",
            ".txt" or ".md" or ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".csv" or ".rtf" or ".odt" => "documents",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg" or ".bmp" or ".ico" or ".heic" or ".mp4" or ".mov" or ".mkv" or ".avi" or ".mp3" or ".wav" or ".flac" or ".m4a" => "media",
            _ => "other"
        };
    }
    public static List<DesktopGroup> NormalizeGroups(IEnumerable<DesktopGroup>? groups)
    {
        var result = new List<DesktopGroup>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in (groups ?? []).Where(x => x is not null).Take(40))
        {
            if (!Guid.TryParseExact(group.Id, "N", out _) || !ids.Add(group.Id)) continue;
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "新分组" : group.Name.Trim()[..Math.Min(group.Name.Trim().Length, 24)];
            group.Targets = (group.Targets ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
            result.Add(group);
        }
        return result;
    }

    public static DockItem[] Read(IEnumerable<string> directories)
    {
        var items = new List<DockItem>();
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        bool folder = Directory.Exists(path);
                        string extension = Path.GetExtension(path);
                        string name = !folder && (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                            ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
                        items.Add(new(name, path));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return items.DistinctBy(x => x.Target, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static DockItem[] Filter(IEnumerable<DockItem> items, IEnumerable<DesktopGroup> groups, string groupId, string query)
    {
        var allGroups = groups.ToArray();
        var targets = groupId == "all" ? null : (groupId == "ungrouped"
            ? allGroups.SelectMany(x => x.Targets) : allGroups.FirstOrDefault(x => x.Id == groupId)?.Targets ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items.Where(x => (targets is null || (groupId == "ungrouped" ? !targets.Contains(x.Target) : targets.Contains(x.Target)))
            && x.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    public static void Assign(List<DesktopGroup> groups, string target, string? groupId)
    {
        foreach (var group in groups) group.Targets.RemoveAll(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
        var chosen = groups.FirstOrDefault(x => x.Id == groupId);
        chosen?.Targets.Add(target);
    }
}

public sealed record WeatherSnapshot(string City, double Temperature, double FeelsLike, int Humidity,
    double Wind, int Code, bool IsDay, double High, double Low, DateTimeOffset FetchedAt)
{
    public string? Location { get; init; }
    public static WeatherSnapshot Parse(string city, string json, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var current = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        var result = new WeatherSnapshot(city, current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(), current.GetProperty("relative_humidity_2m").GetInt32(),
            current.GetProperty("wind_speed_10m").GetDouble(), current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1, daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(), fetchedAt);
        if (new[] { result.Temperature, result.FeelsLike, result.Wind, result.High, result.Low }.Any(x => !double.IsFinite(x))
            || result.Humidity is < 0 or > 100 || result.Wind < 0 || result.Low > result.High) throw new JsonException("Invalid weather values.");
        return result;
    }
    public string Description => Code switch
    {
        0 => IsDay ? "晴" : "晴朗夜空", 1 or 2 => "晴间多云", 3 => "多云", 45 or 48 => "雾",
        >= 51 and <= 57 => "毛毛雨", >= 61 and <= 67 => "雨", >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "阵雨", 85 or 86 => "阵雪", >= 95 and <= 99 => "雷雨", _ => "天气变化"
    };
    public string Glyph => Code switch { 0 => IsDay ? "☀" : "☾", 1 or 2 => "⛅", >= 51 and <= 67 or >= 80 and <= 82 => "☂", >= 71 and <= 77 or 85 or 86 => "❄", >= 95 and <= 99 => "ϟ", _ => "☁" };
}
