using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace FluffHistory.Localization;

public static class CatalogValidation
{
    public static IReadOnlyList<string> ShippingLanguages => FluffHistory.Application.ApplicationLanguage.SupportedTags;
    public static IReadOnlyList<string> ReleaseBranding(IReadOnlyDictionary<string, string> catalog)
    {
        var errors = new List<string>();
        if (!catalog.TryGetValue("Diagnostics_TextBlock_ProductTitle.Text", out var title) || title != "Fluff-History")
            errors.Add("Normal About product title must be Fluff-History.");
        foreach (var (key, value) in catalog.Where(entry => entry.Key.StartsWith("Diagnostics_", StringComparison.Ordinal)))
        {
            if (key.Contains("Preview", StringComparison.OrdinalIgnoreCase))
                errors.Add("Obsolete preview-specific About identifier: " + key);
            if (Regex.IsMatch(value, @"\bdeveloper[\s-]+preview\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                errors.Add("Developer Preview branding in normal About resources: " + key);
        }
        return errors;
    }
    public static XDocument Load(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    public static IReadOnlyDictionary<string, string> Read(XDocument document)
    {
        if (document.Root?.Name != "root") throw new InvalidDataException("Missing RESW root.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Root.Elements("data"))
        {
            var key = (string?)entry.Attribute("name"); var nodes = entry.Elements("value").ToArray();
            if (string.IsNullOrWhiteSpace(key) || !unique.Add(key) || nodes.Length != 1) throw new InvalidDataException("Duplicate/malformed resource identifier: " + key);
            var value = nodes[0].Value;
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n') ||
                c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
                throw new InvalidDataException("Empty/control-character resource: " + key);
            _ = CompositeFormat.Parse(value);
            values.Add(key, value);
        }
        if (values.Count == 0) throw new InvalidDataException("Empty resource catalog.");
        return values;
    }
    public static IReadOnlyList<string> Compare(IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> translated, string language)
    {
        _ = CultureInfo.GetCultureInfo(language);
        var errors = new List<string>();
        foreach (var key in english.Keys.Except(translated.Keys, StringComparer.Ordinal)) errors.Add("Missing: " + key);
        foreach (var key in translated.Keys.Except(english.Keys, StringComparer.Ordinal)) errors.Add("Obsolete/unknown: " + key);
        foreach (var (key, value) in translated.Where(p => english.ContainsKey(p.Key)))
        {
            try { _ = CompositeFormat.Parse(value); }
            catch (FormatException) { errors.Add("Malformed format: " + key); continue; }
            if (!Placeholders(english[key]).SetEquals(Placeholders(value))) errors.Add("Placeholder mismatch: " + key);
            if (Leading(value) != Leading(english[key]) || Trailing(value) != Trailing(english[key])) errors.Add("Whitespace mismatch: " + key);
        }
        if (translated.TryGetValue("UILanguage", out var selected) && selected != language) errors.Add("UILanguage must equal catalog tag.");
        if (translated.TryGetValue("Shell_ResourceLanguage", out var native) && native != language) errors.Add("Shell_ResourceLanguage must equal catalog tag.");
        return errors;
    }
    // CompositeFormat validates syntax. This inventory additionally protects indices AND their
    // format/alignment contracts, while permitting arbitrary translator ordering/repetition.
    private static HashSet<string> Placeholders(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '{') continue;
            if (index + 1 < value.Length && value[index + 1] == '{') { index++; continue; }
            var end = value.IndexOf('}', index + 1); // CompositeFormat has already validated the syntax.
            if (end < 0) throw new FormatException("Unclosed composite placeholder.");
            result.Add(value[(index + 1)..end]); index = end;
        }
        return result;
    }
    private static string Leading(string value) => new(value.TakeWhile(char.IsWhiteSpace).ToArray());
    private static string Trailing(string value) => new(value.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
}
