namespace FluffHistory.Application;

/// <summary>Closed UI preference choices, also used by resource-generation tooling.
/// Null follows Windows resource selection; this list does not implement language matching.</summary>
public static class ApplicationLanguage
{
    public static IReadOnlyList<string> SupportedTags { get; } = Array.AsReadOnly(new[]
    {
        "en-US", "de-DE", "fr-FR", "es-ES", "it-IT", "pt-BR", "pt-PT", "nl-NL", "pl-PL", "cs-CZ", "sk-SK",
        "hu-HU", "ro-RO", "bg-BG", "hr-HR", "sl-SI", "sr-Latn-RS", "sr-Cyrl-RS", "el-GR", "tr-TR", "ru-RU", "uk-UA",
        "sv-SE", "nb-NO", "da-DK", "fi-FI", "et-EE", "lv-LV", "lt-LT", "ca-ES", "ar-SA", "he-IL", "fa-IR", "hi-IN", "bn-BD",
        "zh-Hans", "zh-Hant", "ja-JP", "ko-KR", "th-TH", "vi-VN", "id-ID", "ms-MY", "fil-PH"
    });

    public static bool IsSupported(string? tag) => tag is null || SupportedTags.Contains(tag, StringComparer.Ordinal);
}
