using FluffHistory.Application;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Apply the app-local choice before XAML; the Shell broker has its own context.</summary>
internal static class UiLanguageStartup
{
    internal static PreferencesLoadResult Preferences { get; private set; } = null!;

    internal static void Apply(PreferencesLoadResult preferences)
    {
        preferences.Preferences.Validate();
        MrtAppStrings.Instance.SelectLanguage(preferences.Preferences.Language);
        Preferences = preferences;
    }

}
