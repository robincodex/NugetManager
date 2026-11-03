namespace NugetManager.Domain;

/// <summary>
/// Semantic NuGet version ordering (major.minor.patch[.revision][-prerelease][+metadata]).
/// Unparseable values sort before parseable ones and never compare as newer.
/// </summary>
internal sealed class NuGetVersionComparer : IComparer<string>
{
    public static NuGetVersionComparer Instance { get; } = new();

    public static bool TryParse(string? value, out ParsedVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var text = value.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }
        string? release = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            release = text[(dash + 1)..];
            text = text[..dash];
            if (release.Length == 0)
            {
                return false;
            }
        }
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }
        var numbers = new long[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }
        version = new ParsedVersion(numbers[0], numbers[1], numbers[2], numbers[3], release);
        return true;
    }

    public static bool IsPrerelease(string version) => TryParse(version, out var v) && v.Release is not null;

    public static bool IsNewer(string candidate, string current) => Instance.Compare(candidate, current) > 0
        && TryParse(candidate, out _) && TryParse(current, out _);

    public static bool IsMajorChange(string from, string to) =>
        TryParse(from, out var a) && TryParse(to, out var b) && a.Major != b.Major;

    public int Compare(string? x, string? y)
    {
        var okX = TryParse(x, out var a);
        var okY = TryParse(y, out var b);
        if (!okX || !okY)
        {
            return okX == okY ? string.Compare(x, y, StringComparison.OrdinalIgnoreCase) : okX ? 1 : -1;
        }
        var c = a.Major.CompareTo(b.Major);
        if (c == 0) c = a.Minor.CompareTo(b.Minor);
        if (c == 0) c = a.Patch.CompareTo(b.Patch);
        if (c == 0) c = a.Revision.CompareTo(b.Revision);
        if (c != 0)
        {
            return c;
        }
        if (a.Release is null || b.Release is null)
        {
            return a.Release is null ? (b.Release is null ? 0 : 1) : -1;
        }
        return CompareRelease(a.Release, b.Release);
    }

    private static int CompareRelease(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = long.TryParse(pa[i], out var ia);
            var nb = long.TryParse(pb[i], out var ib);
            int c = na && nb ? ia.CompareTo(ib)
                : na ? -1
                : nb ? 1
                : string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return c;
            }
        }
        return pa.Length.CompareTo(pb.Length);
    }

    internal readonly record struct ParsedVersion(long Major, long Minor, long Patch, long Revision, string? Release);
}
