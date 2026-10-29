using System.Globalization;

namespace FluffHistory.Desktop.Presentation.Models;

public enum CountKind { Files, Folders, ExistingFolders, SavedVersions, NewVersions, FileVersions, FileProblems, Items, Seconds, Minutes, Hours, Bytes, Versions }
public enum CardinalCategory { Zero, One, Two, Few, Many, Other }

/// <summary>Integer cardinal subset of Unicode CLDR 48 for the closed shipped locale set.
/// Counts/durations are nonnegative integers; this is not a decimal/ordinal plural engine.</summary>
public static class LocalizedCounts
{
    public static string Count(this IAppStrings strings, CountKind kind, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var id = Enum.Parse<TextId>($"Count_{kind}_{Category(strings.Language, count)}", ignoreCase: false);
        return strings.Format(id, count);
    }
    public static CardinalCategory Category(string language, long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        var tag = CultureInfo.GetCultureInfo(language);
        var end = n % 10; var hundred = n % 100;
        return tag.TwoLetterISOLanguageName switch
        {
            "id" or "ja" or "ko" or "ms" or "th" or "vi" or "zh" => CardinalCategory.Other,
            "ar" => n switch { 0 => CardinalCategory.Zero, 1 => CardinalCategory.One, 2 => CardinalCategory.Two,
                _ when hundred is >= 3 and <= 10 => CardinalCategory.Few, _ when hundred >= 11 => CardinalCategory.Many, _ => CardinalCategory.Other },
            "ru" or "uk" => end == 1 && hundred != 11 ? CardinalCategory.One :
                end is >= 2 and <= 4 && hundred is not (>= 12 and <= 14) ? CardinalCategory.Few : CardinalCategory.Many,
            "pl" => n == 1 ? CardinalCategory.One : end is >= 2 and <= 4 && hundred is not (>= 12 and <= 14) ? CardinalCategory.Few : CardinalCategory.Many,
            "hr" or "sr" => end == 1 && hundred != 11 ? CardinalCategory.One :
                end is >= 2 and <= 4 && hundred is not (>= 12 and <= 14) ? CardinalCategory.Few : CardinalCategory.Other,
            "cs" or "sk" => n == 1 ? CardinalCategory.One : n is >= 2 and <= 4 ? CardinalCategory.Few : CardinalCategory.Other,
            "sl" => hundred switch { 1 => CardinalCategory.One, 2 => CardinalCategory.Two, 3 or 4 => CardinalCategory.Few, _ => CardinalCategory.Other },
            "ro" => n == 1 ? CardinalCategory.One : n == 0 || hundred is >= 1 and <= 19 ? CardinalCategory.Few : CardinalCategory.Other,
            "lt" => end == 1 && hundred is not (>= 11 and <= 19) ? CardinalCategory.One :
                end is >= 2 and <= 9 && hundred is not (>= 11 and <= 19) ? CardinalCategory.Few : CardinalCategory.Other,
            "lv" => end == 0 || hundred is >= 11 and <= 19 ? CardinalCategory.Zero : end == 1 ? CardinalCategory.One : CardinalCategory.Other,
            "he" => n switch { 1 => CardinalCategory.One, 2 => CardinalCategory.Two, _ => CardinalCategory.Other },
            "fil" => n is >= 1 and <= 3 || end is not (4 or 6 or 9) ? CardinalCategory.One : CardinalCategory.Other,
            "bn" or "fa" or "hi" => n <= 1 ? CardinalCategory.One : CardinalCategory.Other,
            "fr" => n <= 1 ? CardinalCategory.One : n % 1_000_000 == 0 ? CardinalCategory.Many : CardinalCategory.Other,
            "pt" when !tag.Name.Equals("pt-PT", StringComparison.OrdinalIgnoreCase) => n <= 1 ? CardinalCategory.One :
                n % 1_000_000 == 0 ? CardinalCategory.Many : CardinalCategory.Other,
            "ca" or "it" or "es" or "pt" => n == 1 ? CardinalCategory.One : n != 0 && n % 1_000_000 == 0 ? CardinalCategory.Many : CardinalCategory.Other,
            _ => n == 1 ? CardinalCategory.One : CardinalCategory.Other
        };
    }
    /// <summary>Isolate actual paths/IDs in RTL prose; this never changes an operation's real input.</summary>
    public static string Technical(this IAppStrings strings, string value) => strings.IsRightToLeft ? "\u2066" + value + "\u2069" : value;
}
