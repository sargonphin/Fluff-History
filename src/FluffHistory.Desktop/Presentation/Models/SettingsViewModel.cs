using FluffHistory.Application;
using System.Globalization;
namespace FluffHistory.Desktop.Presentation.Models;

public sealed record AppearanceChoice(string Label, AppearancePreference Value);
public sealed record LanguageChoice(string Label, string? Value);
public sealed record UpdateChannelChoice(string Label, UpdateChannel Value);
public sealed record UpdateFrequencyChoice(string Label, UpdateFrequency Value);
public sealed class SettingsViewModel : ObservableObject
{
    private IAppStrings S => _owner.Strings;
    private readonly IDesktopContext _owner;
    private readonly IDesktopDialogs? _dialogs;
    public SettingsViewModel(IDesktopContext owner, IDesktopDialogs? dialogs = null)
    {
        _owner = owner; _dialogs = dialogs; RefreshChoices(); owner.Changed += () => { RefreshChoices(); Changed(""); };
    }
    private string? _choicesLanguage;
    private void RefreshChoices()
    {
        if (_choicesLanguage == S.Language) return;
        _choicesLanguage = S.Language;
        Appearances = [new(S[TextId.Appearance_System], AppearancePreference.System), new(S[TextId.Appearance_Light], AppearancePreference.Light), new(S[TextId.Appearance_Dark], AppearancePreference.Dark)];
        Languages = [new(S[TextId.Language_UseWindows], null), .. ApplicationLanguage.SupportedTags.Select(tag =>
            new LanguageChoice(CultureInfo.GetCultureInfo(tag).NativeName, tag))];
        UpdateChannels = [new(S[TextId.Update_Stable], UpdateChannel.Stable), new(S[TextId.Update_Beta], UpdateChannel.Beta)];
        UpdateFrequencies = [new(S[TextId.Update_Never], UpdateFrequency.Never), new(S[TextId.Update_Daily], UpdateFrequency.Daily),
            new(S[TextId.Update_Weekly], UpdateFrequency.Weekly), new(S[TextId.Update_Monthly], UpdateFrequency.Monthly)];
    }
    public IReadOnlyList<AppearanceChoice> Appearances { get; private set; } = [];
    public AppearanceChoice SelectedAppearance
    {
        get => Appearances.Single(a => a.Value == _owner.Preferences.Appearance);
        set { if (value is not null && value.Value != _owner.Preferences.Appearance) _ = SetAppearanceAsync(value.Value); }
    }
    public bool NeedsAttention => _owner.PreferencesNeedAttention;
    public IReadOnlyList<LanguageChoice> Languages { get; private set; } = [];
    public LanguageChoice SelectedLanguage
    {
        get => Languages.Single(choice => choice.Value == _owner.Preferences.Language);
        set { if (value is not null && value.Value != _owner.Preferences.Language) _ = SetLanguageAsync(value.Value); }
    }
    public Task SetLanguageAsync(string? language) => _owner.RunAsync(token =>
        _owner.SavePreferencesAsync(_owner.Preferences with { Language = language }, token));
    public Task SetAppearanceAsync(AppearancePreference appearance) => _owner.RunAsync(token => _owner.SavePreferencesAsync(_owner.Preferences with { Appearance = appearance }, token));
    private UpdatePreferences UpdatePolicy => _owner.Updates?.Preferences ?? UpdatePreferences.Default;
    public string UpdateTitle => S[TextId.Update_Title];
    public string UpdateChannelLabel => S[TextId.Update_Channel];
    public string UpdateFrequencyLabel => S[TextId.Update_Frequency];
    public string UpdateCheckLabel => S[TextId.Update_CheckNow];
    public string UpdateInstallLabel => S[TextId.Update_Install];
    public string UpdateNotesLabel => S[TextId.Update_Notes];
    public string UpdateDisabledLabel => S[TextId.Update_Disabled];
    public string UpdatePrivacyLabel => S[TextId.Update_Privacy];
    public IReadOnlyList<UpdateChannelChoice> UpdateChannels { get; private set; } = [];
    public IReadOnlyList<UpdateFrequencyChoice> UpdateFrequencies { get; private set; } = [];
    public UpdateChannelChoice SelectedUpdateChannel
    {
        get => UpdateChannels.Single(c => c.Value == UpdatePolicy.Channel);
        set { if (value is not null && value.Value != UpdatePolicy.Channel) _ = _owner.RunAsync(t => _owner.SaveUpdatePreferencesAsync(UpdatePolicy with { Channel = value.Value }, t)); }
    }
    public UpdateFrequencyChoice SelectedUpdateFrequency
    {
        get => UpdateFrequencies.Single(c => c.Value == UpdatePolicy.Frequency);
        set { if (value is not null && value.Value != UpdatePolicy.Frequency) _ = _owner.RunAsync(t => _owner.SaveUpdatePreferencesAsync(UpdatePolicy with { Frequency = value.Value }, t)); }
    }
    public bool CanCheckUpdates => _owner.AgentAvailable && _owner.Updates?.Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.HandingOff);
    public bool HasUpdate => _owner.Updates?.Available is not null;
    public bool CanInstallUpdate => CanCheckUpdates && _owner.Updates is { CurrentReleaseVersion: not null, Available.CanDownload: true };
    public bool AutomaticChecksDisabled => UpdatePolicy.Frequency == UpdateFrequency.Never;
    public string CurrentUpdateVersion => S.Format(TextId.Update_Current, _owner.Updates?.CurrentReleaseVersion is { } version ?
        version + (ReleaseVersion.Parse(version).IsBeta ? " · " + S[TextId.Update_Beta] : "") : S[TextId.Update_Development]);
    public string LastUpdateCheck => _owner.Updates?.LastCheckedUtc is { } utc ? S.Format(TextId.Update_LastChecked, utc.ToLocalTime().ToString("g", S.FormatCulture)) : S[TextId.Update_NotChecked];
    public string AvailableUpdate => _owner.Updates?.Available is { } release ?
        S.Format(TextId.Update_Available, release.Version.ToString(), S[release.Prerelease ? TextId.Update_Beta : TextId.Update_Stable], release.Name) : "";
    public string UpdateStatus
    {
        get
        {
            var state = _owner.Updates;
            if (state is null) return "";
            if (state.Phase is UpdatePhase.Checking) return S[TextId.Update_Checking];
            if (state.Phase is UpdatePhase.Downloading) return S.Format(TextId.Update_Downloading,
                state.Available is { Size: > 0 } r ? (state.DownloadedBytes * 100 / r.Size).ToString(S.FormatCulture) : "0");
            if (state.Phase is UpdatePhase.Verifying) return S[TextId.Update_Verifying];
            if (state.Phase is UpdatePhase.HandingOff) return S[TextId.Update_Installing];
            if (state.Phase is UpdatePhase.WaitingForIdle) return S[TextId.Update_Waiting];
            if (state.Failure != UpdateFailure.None) return S[state.Failure switch
            {
                UpdateFailure.Offline or UpdateFailure.Unavailable => TextId.Update_Network,
                UpdateFailure.RateLimited => TextId.Update_RateLimited,
                UpdateFailure.InvalidResponse or UpdateFailure.NoValidRelease => TextId.Update_Invalid,
                UpdateFailure.Integrity or UpdateFailure.Signature or UpdateFailure.WrongProduct => TextId.Update_VerificationFailed,
                UpdateFailure.SignerNotApproved => TextId.Update_SignerGate,
                UpdateFailure.Busy or UpdateFailure.BackupStillRunning => TextId.Update_Waiting,
                UpdateFailure.DevelopmentBuild => TextId.Update_DevelopmentInstall,
                UpdateFailure.Relaunch => TextId.Update_RelaunchFailed,
                _ => TextId.Update_InstallFailed
            }];
            if (state.Phase is UpdatePhase.Ready) return S[TextId.Update_Ready];
            return state.LastCheckedUtc is null || state.Available is not null ? "" : S[TextId.Update_Idle];
        }
    }
    public Task CheckUpdatesAsync() => _owner.RunAsync(_owner.CheckUpdatesAsync);
    public Task InstallUpdateAsync() => _owner.RunAsync(async token =>
    {
        var active = _owner.Status?.IsAttemptActive == true;
        if (active && (_dialogs is null || !await _dialogs.ConfirmAsync(S[TextId.Update_Title], S[TextId.Update_Confirm], S[TextId.Update_StopInstall]))) return;
        await _owner.InstallUpdateAsync(active, token);
    });
    public string? ReleaseNotesUrl => _owner.Updates?.Available?.PageUrl;
}
