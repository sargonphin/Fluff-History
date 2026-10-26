namespace FluffHistory.Localization;

public static class TranslationHygiene
{
    /// <summary>Approved equality is per locale/key, with a reviewable explanation. Never
    /// treat a whole locale or a broad prefix as permitted English fallback.</summary>
    public static IReadOnlyList<string> Check(IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> translated, IReadOnlyDictionary<string, string> approvedEquality)
    {
        var errors = new List<string>();
        foreach (var (key, reason) in approvedEquality)
        {
            if (string.IsNullOrWhiteSpace(reason) || !english.TryGetValue(key, out var original) ||
                !translated.TryGetValue(key, out var actual) || original != actual)
                errors.Add("Obsolete/invalid identical-translation exception: " + key);
        }
        foreach (var (key, value) in translated)
        {
            if (english.TryGetValue(key, out var original) && value == original && !approvedEquality.ContainsKey(key))
                errors.Add("Unreviewed English-identical translation: " + key);
            // Formatting is applied at the presentation boundary. Literal bidi overrides or
            // isolates in catalogs can conceal/reorder prose or leak into adjacent controls.
            // ZWJ/ZWNJ remain allowed for the scripts that need them.
            if (value.Any(c => c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
                errors.Add("Unexpected bidi formatting control: " + key);
        }
        return errors;
    }
}
