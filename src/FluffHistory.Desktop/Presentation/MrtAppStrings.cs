using System.Globalization;
using FluffHistory.Desktop.Presentation.Models;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Windows selects each resource candidate; the PRI's ultimate language is en-US.</summary>
internal sealed class MrtAppStrings : IAppStrings
{
    private readonly ResourceManager _manager;
    private readonly ResourceMap _resources;
    private ResourceContext _context;
    internal static MrtAppStrings Instance { get; } = new();
    internal MrtAppStrings(string? pri = null, string? languageOverride = null)
    {
        // Explicit PRI path supports the unpackaged executable.
        _manager = new(pri ?? Path.Combine(AppContext.BaseDirectory, "FluffHistory.Desktop.pri"));
        _resources = _manager.MainResourceMap.GetSubtree("Resources");
        _context = _manager.CreateResourceContext();
        // A Properties page may provide its captured UI language; ordinary Desktop uses Windows defaults.
        if (languageOverride is not null) _context.QualifierValues["Language"] = languageOverride;
        Language = Required("UILanguage");
    }
    public string Language { get; private set; }
    internal (ResourceContext Context, string Language) PrepareLanguage(string? language)
    {
        var context = _manager.CreateResourceContext();
        if (language is not null) context.QualifierValues["Language"] = language;
        return (context, _resources.GetValue(nameof(TextId.UILanguage), context).ValueAsString);
    }
    internal void ApplyLanguage((ResourceContext Context, string Language) selection)
    { _context = selection.Context; Language = selection.Language; }
    internal void SelectLanguage(string? language) => ApplyLanguage(PrepareLanguage(language));
    internal string Resource(string key) => Required(key);
    public CultureInfo FormatCulture => CultureInfo.CurrentCulture;
    public string this[TextId id] => Required(id.ToString());
    private string Required(string key)
    {
        // An absent canonical identifier is a programming/build defect, never a blank label.
        var value = _resources.GetValue(key, _context).ValueAsString;
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException("Empty application resource: " + key) : value;
    }
}
