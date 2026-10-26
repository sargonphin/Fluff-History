using System.Globalization;
using System.Xml.Linq;
using FluffHistory.Desktop.Presentation.Models;

namespace FluffHistory.Tests;

/// <summary>Deterministic catalog provider for portable presentation tests, independent of MRT/WinUI.</summary>
internal sealed class TestAppStrings : IAppStrings
{
    private readonly IReadOnlyDictionary<string, string> _selected, _english;
    private readonly CultureInfo? _culture;
    internal static IAppStrings English { get; } = new TestAppStrings();
    internal TestAppStrings(string language = "en-US", CultureInfo? culture = null,
        IReadOnlyDictionary<string, string>? selected = null)
    {
        Language = language; _culture = culture;
        _english = Read("en-US"); _selected = selected ?? Read(language);
    }
    internal static IReadOnlyDictionary<string, string> Read(string language) => XDocument.Load(Path.Combine(
        AppContext.BaseDirectory, "Localization", language, "Resources.resw")).Root!.Elements("data")
        .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value, StringComparer.Ordinal);
    public string Language { get; }
    public CultureInfo FormatCulture => _culture ?? CultureInfo.CurrentCulture;
    public string this[TextId id] => Resolve(id.ToString());
    internal string Resolve(string key) => _selected.TryGetValue(key, out var text) ? text :
        _english.TryGetValue(key, out var fallback) ? fallback : throw new KeyNotFoundException("Missing canonical resource: " + key);
}
