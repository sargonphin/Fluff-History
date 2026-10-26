using System.Globalization;
using System.Text;
using FluffHistory.Core;

namespace FluffHistory.Core;

/// <summary>V3 root/path identity. Casing is frozen by the format, never by the host's locale/Unicode version.</summary>
public sealed record HistoryPath : IComparable<HistoryPath>
{
    public const int MaximumPathLength = 2048;
    private static readonly IReadOnlyDictionary<int, int> Casing = LoadCasing();
    private static readonly UnicodeEncoding StrictUtf16 = new(true, false, true);
    public Guid SourceRootId { get; }
    public string CanonicalPath { get; }

    public HistoryPath(Guid sourceRootId, string relativePath)
    {
        ModelValidation.RequireIdentity(sourceRootId, nameof(sourceRootId));
        ModelValidation.RequireRelativePath(relativePath, nameof(relativePath));
        if (relativePath.Length > MaximumPathLength)
            throw new ArgumentException("V3 logical paths are limited to 2048 UTF-16 code units.", nameof(relativePath));
        // A strict encoding rejects unpaired surrogates; EnumerateRunes alone would replace them.
        _ = StrictUtf16.GetByteCount(relativePath);
        SourceRootId = sourceRootId;
        CanonicalPath = Fold(relativePath);
    }

    internal static string Fold(string value)
    {
        var result = new StringBuilder(value.Length);
        Span<char> encoded = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            var folded = Casing.TryGetValue(rune.Value, out var upper) ? new Rune(upper) : rune;
            // Append(Rune) binds to Append(object): boxing plus a string for every scalar.
            result.Append(encoded[..folded.EncodeToUtf16(encoded)]);
        }
        return result.ToString();
    }

    public int CompareTo(HistoryPath? other)
    {
        if (other is null) return 1;
        var root = SourceRootId.CompareTo(other.SourceRootId);
        return root != 0 ? root : string.CompareOrdinal(CanonicalPath, other.CanonicalPath);
    }

    private static IReadOnlyDictionary<int, int> LoadCasing()
    {
        using var resource = typeof(HistoryPath).Assembly.GetManifestResourceStream("FluffHistory.Core.OrdinalCaseMap.txt")
            ?? throw new InvalidOperationException("Missing frozen v3 case table.");
        using var reader = new StreamReader(resource);
        var result = new Dictionary<int, int>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(';');
            result.Add(int.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return result;
    }
}
