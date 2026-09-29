using System.Globalization;

namespace Hearthsheet.Infrastructure.Updates;

/// <summary>
/// Semantic version (major.minor.patch[-prerelease][+build]) with SemVer 2.0 precedence.
/// System.Version cannot represent pre-release tags, which release channels need.
/// </summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string PreRelease = "") : IComparable<SemVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var pre = dash >= 0 ? s[(dash + 1)..] : "";
        var core = (dash >= 0 ? s[..dash] : s).Split('.');
        if (core.Length is < 1 or > 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        if (dash >= 0 && (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0))) return false;
        version = new SemVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a valid version.");

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release outranks any of its pre-releases.
        if (!IsPreRelease || !other.IsPreRelease) return other.IsPreRelease.CompareTo(IsPreRelease);

        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bNum = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = (aNum, bNum) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
