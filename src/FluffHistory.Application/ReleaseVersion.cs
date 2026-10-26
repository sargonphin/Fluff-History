using System.Globalization;
using System.Reflection;

namespace FluffHistory.Application;

/// <summary>Closed public release grammar. Windows build revision is deliberately absent.</summary>
public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int? Beta { get; }
    public bool IsBeta => Beta.HasValue;
    private ReleaseVersion(int major, int minor, int patch, int? beta) => (Major, Minor, Patch, Beta) = (major, minor, patch, beta);
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (text is null || text.Length is < 5 or > 64) return false;
        ReadOnlySpan<char> value = text;
        if (value[0] == 'v') value = value[1..];
        int? beta = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            var suffix = value[dash..];
            if (!suffix.StartsWith("-beta.", StringComparison.Ordinal) || !Number(suffix[6..], out var sequence)) return false;
            beta = sequence; value = value[..dash];
        }
        var first = value.IndexOf('.');
        if (first < 1) return false;
        var tail = value[(first + 1)..]; var second = tail.IndexOf('.');
        if (second < 1 || !Number(value[..first], out var major) || !Number(tail[..second], out var minor) ||
            !Number(tail[(second + 1)..], out var patch)) return false;
        version = new(major, minor, patch, beta); return true;
    }
    private static bool Number(ReadOnlySpan<char> value, out int number)
    {
        number = 0;
        if (value.Length is < 1 or > 10 || value.Length > 1 && value[0] == '0') return false;
        foreach (var c in value) if (c is < '0' or > '9') return false;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
    public static ReleaseVersion Parse(string text) => TryParse(text, out var value) ? value : throw new FormatException("Invalid release version.");
    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); if (result != 0) return result;
        result = Patch.CompareTo(other.Patch); if (result != 0) return result;
        if (Beta is null) return other.Beta is null ? 0 : 1;
        return other.Beta is null ? -1 : Beta.Value.CompareTo(other.Beta.Value);
    }
    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}") +
        (Beta is { } beta ? "-beta." + beta.ToString(CultureInfo.InvariantCulture) : "");
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
}

/// <summary>Canonical UInt16 four-part version for PE/package/path ownership, never prerelease precedence.</summary>
public static class ProductBuildVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new(0, 0, 0, 0);
        if (text is null || text.Length > 23) return false;
        var parts = text.Split('.'); if (parts.Length != 4) return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is < 1 or > 5 || part.Length > 1 && part[0] == '0' || part.Any(c => c is < '0' or > '9') ||
                !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
            numbers[i] = number;
        }
        version = new(numbers[0], numbers[1], numbers[2], numbers[3]); return version != new Version(0, 0, 0, 0);
    }
    public static bool MatchesRelease(Version build, ReleaseVersion release) =>
        build.Major == release.Major && build.Minor == release.Minor && build.Build == release.Patch;
}

public static class InstalledReleaseIdentity
{
    public static ReleaseVersion? FromAssembly(Assembly assembly)
    {
        var entries = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Where(a => a.Key == "ReleaseVersion").ToArray();
        return entries.Length == 1 && ReleaseVersion.TryParse(entries[0].Value, out var version) ? version : null;
    }
}

/// <summary>Inno reserves trailing padding in signed VERSIONINFO strings. No leading/internal normalization.</summary>
public static class InstallerProductIdentity
{
    public static bool Matches(string? product, string? company, string? description, string? buildVersion, ReleaseVersion release) =>
        product?.TrimEnd(' ', '\0') == "Fluff-History" && company?.TrimEnd(' ', '\0') == "Fish & Dog Software" &&
        description?.TrimEnd(' ', '\0') == "Fluff-History Setup [" + release + "]" &&
        ProductBuildVersion.TryParse(buildVersion?.TrimEnd(' ', '\0'), out var build) && ProductBuildVersion.MatchesRelease(build, release);
}
