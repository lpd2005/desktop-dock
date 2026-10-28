namespace DesktopDock.Core;

public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = new(0, 0, 0, "");
        if (string.IsNullOrEmpty(text) || text.Length > 100) return false;
        if (text.StartsWith('v')) text = text[1..];
        string[] build = text.Split('+');
        if (build.Length > 2 || build.Length == 2 && !Identifiers(build[1], false)) return false;
        string[] parts = build[0].Split('-', 2), numbers = parts[0].Split('.');
        if (numbers.Length != 3 || numbers.Any(x => !Numeric(x) || x.Length > 1 && x[0] == '0')) return false;
        if (!int.TryParse(numbers[0], out int major) || !int.TryParse(numbers[1], out int minor) || !int.TryParse(numbers[2], out int patch)) return false;
        string pre = parts.Length == 2 ? parts[1] : "";
        if (parts.Length == 2 && !Identifiers(pre, true)) return false;
        version = new(major, minor, patch, pre); return true;
    }
    private static bool Numeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    private static bool Identifiers(string text, bool strictNumbers) => text.Length > 0 && text.Split('.').All(x =>
        x.Length > 0 && x.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && (!strictNumbers || !Numeric(x) || x.Length == 1 || x[0] != '0'));
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        foreach (int order in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) }) if (order != 0) return order;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        string[] first = Prerelease.Split('.'), second = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(first.Length, second.Length); i++)
        {
            bool a = Numeric(first[i]), b = Numeric(second[i]);
            int order = a && b ? first[i].Length.CompareTo(second[i].Length) : a == b ? 0 : a ? -1 : 1;
            if (order == 0) order = string.CompareOrdinal(first[i], second[i]);
            if (order != 0) return order;
        }
        return first.Length.CompareTo(second.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + Prerelease : "");
}
