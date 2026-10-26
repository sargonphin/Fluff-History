namespace FluffHistory.Core;

internal static class ModelValidation
{
    internal static void RequireRelativePath(string value, string parameterName, bool allowRoot = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (allowRoot && value.Length == 0)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (InvalidPath(value))
        {
            throw new ArgumentException("A normalized, Windows-safe root-relative path is required.", parameterName);
        }
    }

    private static bool InvalidPath(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (c == '\\' || char.IsControl(c)) return true;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i != value.Length && value[i] != '/') continue;
            var segment = value[start..i]; start = i + 1;
            if (segment.IsEmpty || segment is "." or ".." || segment[^1] is ' ' or '.' ||
                segment.IndexOfAny("<>:\"|?*") >= 0 || IsDeviceName(segment)) return true;
        }
        return false;
    }

    private static bool IsDeviceName(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (stem.Length is not (3 or 4 or 6 or 7)) return false;
        // Preserve invariant uppercase device-name semantics, including non-ASCII input,
        // without Split/Trim/ToUpper strings for every path component.
        Span<char> upper = stackalloc char[7]; var length = stem.ToUpperInvariant(upper);
        if (length < 0) return false;
        ReadOnlySpan<char> name = upper[..length];
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
                name.StartsWith("LPT", StringComparison.Ordinal)) &&
                (name[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    internal static void RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identity must not be empty.", parameterName);
        }
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC timestamp is required.", parameterName);
        }
    }
}
