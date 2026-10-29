using FluffHistory.Application;
using FluffHistory.Core;

namespace FluffHistory.Desktop.Presentation.Models;

/// <summary>Window-owned composition/interaction boundary. No repository or native window handles in models.</summary>
public interface IDesktopContext
{
    IAppStrings Strings { get; }
    AppSettings Settings { get; }
    RepositoryInspection Inspection { get; }
    BackupStatus? Status { get; }
    LastBackupStatus? SavedBackupStatus => null;
    HistoryBrowserSession? Browser { get; }
    string LastOutcome { get; set; }
    string BrowserActivity { get; set; }
    string CurrentPage { get; set; }
    ApplicationFailure? LatestFailure { get; }
    bool HasError { get; }
    bool AgentAvailable => true;
    bool ConfigurationNeedsReset { get; }
    long HistoryRevision { get; }
    ISourceFolderPolicy FolderPolicy => SourceFolderPolicy.Empty;
    string? DefaultSourceFolder => null;
    ApplicationPreferences Preferences => ApplicationPreferences.Default;
    bool PreferencesNeedAttention => false;
    UpdateSnapshot? Updates => null;
    string? InstalledProductVersion => null;
    Task SaveUpdatePreferencesAsync(UpdatePreferences preferences, CancellationToken token) => Task.CompletedTask;
    Task CheckUpdatesAsync(CancellationToken token) => Task.CompletedTask;
    Task InstallUpdateAsync(bool cancelBackup, CancellationToken token) => Task.CompletedTask;
    Task<CapacityObservation?> ObserveDriveSpaceAsync(string path, CancellationToken token) => Task.FromResult<CapacityObservation?>(null);
    Task<long?> ObserveBackupSizeAsync(string path, CancellationToken token) => Task.FromResult<long?>(null);
    Task SavePreferencesAsync(ApplicationPreferences preferences, CancellationToken token) => Task.CompletedTask;
    TimelineMaintenanceService? Maintenance => null;
    ExactVersionRemovalService? VersionRemoval => null;
    string ConfigurationProblem => "";
    Task<string?> ValidateWorkspaceAsync(AppSettings settings, CancellationToken token) => Task.FromResult<string?>(null);
    void TimelineChanged() => Notify();
    event Action? Changed;
    Task RunAsync(Func<CancellationToken, Task> command);
    Task SaveAsync(AppSettings settings, CancellationToken token, bool reset = false);
    Task InspectAndComposeAsync(CancellationToken token);
    Task WithBrowserAsync(Func<HistoryBrowserSession, CancellationToken, Task> action, CancellationToken token);
    Task RecoverAsync(char[] password, CancellationToken token);
    Task<ExperimentalRecoveryState> InspectSecurityAsync(CancellationToken token) => Task.FromResult(ExperimentalRecoveryState.Off);
    Task<SecurityOperationResult> ExecuteSecurityAsync(RepositorySecurityOperation operation, char[]? password, CancellationToken token)
        => throw new NotSupportedException();
    bool EncryptionBusy => false;
    EncryptionChangeProgress? EncryptionProgress => null;
    Task<EncryptionChangeResult> ChangeEncryptionAsync(bool encrypted, char[]? password, CancellationToken token) => throw new NotSupportedException();
    void PauseEncryption() { }
    Task<RecoveryResult> RecoverLocationAsync(string path, char[] password, CancellationToken token)
        => new RepositorySetupService().RecoverAsync(path, password, token);
    Task PreviewAsync(BrowserVersion version, CancellationToken token);
    Task RestoreSelectionAsync(BrowserSelection selection, string? destination, Guid root, bool original, CancellationToken token);
    Task RestoreExactVersionAsync(BrowserVersion version, string? destination, bool original, CancellationToken token)
        => throw new NotSupportedException();
    void BackUpNow();
    Task BackUpNowAsync(CancellationToken token) { BackUpNow(); return Task.CompletedTask; }
    void StartScheduling();
    void StopScheduling();
    Task CancelBackupAsync();
    void CancelHistory();
    void ReleasePreview();
    void Notify();
}
public interface IDesktopDialogs
{
    IAppStrings Strings { get; }
    Task<string?> FolderAsync();
    async Task<IReadOnlyList<string>> FoldersAsync() { var folder = await FolderAsync(); return folder is null ? [] : [folder]; }
    Task<bool?> ProtectBackupAsync(string path) => Task.FromResult<bool?>(false);
    Task<char[]?> PasswordAsync(bool creation);
    Task<char[]?> NewRecoveryPasswordAsync() => PasswordAsync(true);
    Task<char[]?> MigrationRecoveryPasswordAsync() => PasswordAsync(false);
    Task<bool> ConfirmAsync(string title, string message, string primary);
    Task NoticeAsync(string title, string message);
    Task<bool> ConfirmLocationAsync(BackupLocationConfirmation confirmation);
    Task<bool> ConfirmDeleteAsync(string title, string message) => ConfirmAsync(title, message, Strings[TextId.Action_Delete]);
    void Navigate(string page);
    string DiagnosticsHeader { get; }
    string SettingsPath { get; }
    void CopyText(string text);
}

public sealed record BackupLocationConfirmation(string Path, string Status, string Explanation, string? Primary = null);

public static class PresentationText
{
    public static string Outcome(IAppStrings S, BackupAttemptResult result) => IsNoChange(result) ? S[TextId.Outcome_NoChanges] : Outcome(S, result.Outcome);
    public static bool IsNoChange(BackupAttemptResult result) => result.Outcome == BackupOutcome.Completed && !result.TimelineChanged &&
        result.Scan is { VersionsCreated: 0, BatchesCommitted: 0, Reactivations: 0, Removals: 0, CapacityRemovalBatches: 0 } && result.Maintenance?.ChangesMayExist != true;
    public static string Duration(IAppStrings S, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks);
        if (duration < TimeSpan.FromSeconds(1)) return S[TextId.Duration_LessThanSecond];
        if (duration < TimeSpan.FromMinutes(1)) return S.Count(CountKind.Seconds, duration.Ticks / TimeSpan.TicksPerSecond);
        var minutes = duration.Ticks / TimeSpan.TicksPerMinute;
        if (minutes < 60) return S.Count(CountKind.Minutes, minutes);
        var hours = S.Count(CountKind.Hours, minutes / 60);
        return minutes % 60 == 0 ? hours : S.Format(TextId.Duration_Joined, hours, S.Count(CountKind.Minutes, minutes % 60));
    }
    public static string Size(IAppStrings S, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes < 1000) return S.Count(CountKind.Bytes, bytes);
        string[] units = ["bytes", "kB", "MB", "GB", "TB"]; decimal size = bytes; var unit = 0;
        while (size >= 1000 && unit < units.Length - 1) { size /= 1000; unit++; }
        if (size >= 999.95m && unit < units.Length - 1) { size /= 1000; unit++; }
        return size.ToString("0.#", S.FormatCulture) + " " + units[unit];
    }
    public static string Failure(IAppStrings S, ApplicationFailure failure) => failure switch
    {
        ApplicationFailure.RecoveryRequired => S[TextId.Failure_RecoveryRequired],
        ApplicationFailure.EncryptionMigrationRequired => S[TextId.Failure_MigrationRequired],
        ApplicationFailure.RefreshRequired => S[TextId.Failure_RefreshRequired],
        ApplicationFailure.InvalidRepository => S[TextId.Failure_InvalidRepository],
        ApplicationFailure.Busy => S[TextId.Failure_Busy],
        ApplicationFailure.InvalidSelection => S[TextId.Failure_InvalidSelection],
        ApplicationFailure.RestoreStructureConflict => S[TextId.Failure_RestoreConflict],
        ApplicationFailure.Cancelled => S[TextId.Failure_Cancelled],
        _ => S[TextId.Failure_Unavailable]
    };
    public static string Readiness(IAppStrings S, RepositoryReadiness state) => state switch
    {
        RepositoryReadiness.Ready => S[TextId.Status_Ready],
        RepositoryReadiness.RecoveryRequired => S[TextId.Status_RecoveryRequired],
        RepositoryReadiness.EncryptionMigrationRequired => S[TextId.Encryption_Paused],
        RepositoryReadiness.Missing => S[TextId.Status_BackupUnavailable],
        RepositoryReadiness.Invalid => S[TextId.Status_InvalidLocation],
        _ => S[TextId.Status_BackupUnavailable]
    };
    public static string Outcome(IAppStrings S, BackupOutcome? outcome) => outcome switch
    {
        BackupOutcome.Completed => S[TextId.Outcome_Completed],
        BackupOutcome.CompletedWithSourceProblems => S[TextId.Outcome_SourceProblems],
        BackupOutcome.Cancelled => S[TextId.Outcome_Cancelled],
        BackupOutcome.CapacityBlocked => S[TextId.Outcome_CapacityBlocked],
        BackupOutcome.RecoveryRequired => S[TextId.Status_RecoveryRequired],
        BackupOutcome.EncryptionMigrationRequired => S[TextId.Overview_EncryptionBusy],
        BackupOutcome.SafeguardDeferred => S[TextId.Outcome_Deferred],
        BackupOutcome.RepositoryUnavailable => S[TextId.Status_BackupUnavailable],
        BackupOutcome.ScanControlFailure => S[TextId.Outcome_Attention],
        _ => S[TextId.Outcome_NoAttempt]
    };
    public static string Safeguard(IAppStrings S, SafeguardReason reason) => reason switch
    {
        SafeguardReason.OnBattery => S[TextId.Safeguard_OnBattery],
        SafeguardReason.UnknownPower => S[TextId.Safeguard_UnknownPower],
        SafeguardReason.MeteredNetwork => S[TextId.Safeguard_MeteredNetwork],
        SafeguardReason.NetworkUnavailable => S[TextId.Safeguard_NoNetwork],
        _ => ""
    };
    public static string Frequency(IAppStrings S, int minutes) => S[minutes switch
    {
        10 => TextId.Frequency_TenMinutes, 30 => TextId.Frequency_ThirtyMinutes, 60 => TextId.Frequency_Hour,
        180 => TextId.Frequency_ThreeHours, 360 => TextId.Frequency_SixHours, 720 => TextId.Frequency_TwelveHours,
        1440 => TextId.Frequency_Day, _ => throw new ArgumentOutOfRangeException(nameof(minutes))
    }];
    public static string Retention(IAppStrings S, RetentionPolicy policy) => S[policy switch
    {
        RetentionPolicy.UntilSaturation => TextId.Retention_UntilSaturation, RetentionPolicy.OneMonth => TextId.Retention_OneMonth,
        RetentionPolicy.ThreeMonths => TextId.Retention_ThreeMonths, RetentionPolicy.SixMonths => TextId.Retention_SixMonths,
        RetentionPolicy.NineMonths => TextId.Retention_NineMonths, RetentionPolicy.OneYear => TextId.Retention_OneYear,
        RetentionPolicy.TwoYears => TextId.Retention_TwoYears, RetentionPolicy.Forever => TextId.Retention_Forever,
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    }];
    public static string RetentionNotice(IAppStrings S, RetentionPolicy policy) => S[policy switch
    {
        RetentionPolicy.UntilSaturation => TextId.Retention_CapacityExplanation, RetentionPolicy.Forever => TextId.Retention_ForeverExplanation,
        _ => TextId.Retention_AgeExplanation
    }];
    public static string Cleanup(IAppStrings S, HistoryCleanupMode mode) => S[mode switch
    {
        HistoryCleanupMode.OlderThanOneMonth => TextId.Cleanup_OlderMonth, HistoryCleanupMode.OlderThanThreeMonths => TextId.Cleanup_OlderThreeMonths,
        HistoryCleanupMode.OlderThanSixMonths => TextId.Cleanup_OlderSixMonths, HistoryCleanupMode.OlderThanNineMonths => TextId.Cleanup_OlderNineMonths,
        HistoryCleanupMode.OlderThanOneYear => TextId.Cleanup_OlderYear, HistoryCleanupMode.OlderThanTwoYears => TextId.Cleanup_OlderTwoYears,
        HistoryCleanupMode.KeepOnlyMostRecent => TextId.Cleanup_KeepRecent, _ => throw new ArgumentOutOfRangeException(nameof(mode))
    }];
    public static string FolderWarning(IAppStrings S, SourceSafetyKind kind) => kind == SourceSafetyKind.Ordinary ? "" : S[kind switch
    {
        SourceSafetyKind.ApplicationData => TextId.FolderSafety_ApplicationData, SourceSafetyKind.System => TextId.FolderSafety_System,
        SourceSafetyKind.SharedApplicationData => TextId.FolderSafety_SharedData, SourceSafetyKind.Temporary => TextId.FolderSafety_Temporary,
        _ => TextId.FolderSafety_Blocked
    }];
    public static string FolderName(IAppStrings S, FolderPresentation folder) => folder.Kind == PersonalFolderKind.Folder ? folder.Label : S[folder.Kind switch
    {
        PersonalFolderKind.Desktop => TextId.Folder_Desktop, PersonalFolderKind.Documents => TextId.Folder_Documents,
        PersonalFolderKind.Downloads => TextId.Folder_Downloads, PersonalFolderKind.Pictures => TextId.Folder_Pictures,
        PersonalFolderKind.Music => TextId.Folder_Music, PersonalFolderKind.Videos => TextId.Folder_Videos,
        _ => throw new ArgumentOutOfRangeException(nameof(folder))
    }];
    public static string Validation(IAppStrings S, Exception error) => ConfigurationIssues.Code(error) is { } code && code != ConfigurationIssueCode.SavedConfigurationInvalid
        ? ConfigurationIssue(S, code) : S[TextId.Configuration_CheckFolders];
    public static string ConfigurationIssue(IAppStrings S, ConfigurationIssueCode code) => code == ConfigurationIssueCode.None ? "" : S[code switch
    {
        ConfigurationIssueCode.Checking => TextId.Configuration_Checking,
        ConfigurationIssueCode.SettingsNeedReset => TextId.Configuration_SettingsNeedReset,
        ConfigurationIssueCode.WorkspaceUnavailable => TextId.Configuration_WorkspaceUnavailable,
        ConfigurationIssueCode.ExclusionConflict => TextId.Configuration_ExclusionConflict,
        ConfigurationIssueCode.ExclusionLimit => TextId.Configuration_ExclusionLimit,
        ConfigurationIssueCode.EffectiveExclusionLimit => TextId.Configuration_EffectiveExclusionLimit,
        ConfigurationIssueCode.DuplicateFolder => TextId.Configuration_DuplicateFolder,
        ConfigurationIssueCode.NestedFolder => TextId.Configuration_NestedFolder,
        ConfigurationIssueCode.AbsolutePathRequired => TextId.Configuration_AbsolutePath,
        ConfigurationIssueCode.OrdinaryPathRequired => TextId.Configuration_OrdinaryPath,
        ConfigurationIssueCode.CompleteShareRequired => TextId.Configuration_CompleteShare,
        ConfigurationIssueCode.AmbiguousPath => TextId.Configuration_AmbiguousPath,
        ConfigurationIssueCode.SourceRepositoryOverlap => TextId.Configuration_Overlap,
        ConfigurationIssueCode.FolderLimit => TextId.Configuration_FolderLimit,
        ConfigurationIssueCode.ConfirmWarning => TextId.Configuration_ConfirmWarning,
        ConfigurationIssueCode.ResolvedWarning => TextId.Configuration_ResolvedWarning,
        ConfigurationIssueCode.ResolvedOverlap => TextId.Configuration_ResolvedOverlap,
        ConfigurationIssueCode.WorkingDataBlocked => TextId.Configuration_OwnedWorkingData,
        ConfigurationIssueCode.FolderBlocked => TextId.FolderSafety_Blocked,
        _ => TextId.Configuration_SavedInvalid
    }];
    public static string EncryptionPhase(IAppStrings S, EncryptionChangePhase phase) => S[phase switch
    {
        EncryptionChangePhase.CheckingSpace => TextId.Encryption_CheckingSpace, EncryptionChangePhase.EncryptingFiles => TextId.Encryption_EncryptingFiles,
        EncryptionChangePhase.DecryptingFiles => TextId.Encryption_DecryptingFiles, EncryptionChangePhase.RebuildingMetadata => TextId.Encryption_RebuildingMetadata,
        EncryptionChangePhase.Finalizing => TextId.Encryption_Finalizing, EncryptionChangePhase.Completed => TextId.Encryption_Completed,
        _ => TextId.Encryption_Preparing
    }];
    public static string Saved(IAppStrings S, ConfigurationSaveNotice notice) => S[notice switch
    {
        ConfigurationSaveNotice.FoldersForScheduledBackup => TextId.Configuration_FoldersForScheduled,
        ConfigurationSaveNotice.FoldersForNextBackup => TextId.Configuration_FoldersForNext,
        _ => TextId.Configuration_Saved
    }];
    public static string Admission(IAppStrings S, BackupAdmission admission) => S[admission switch
    {
        BackupAdmission.Started => TextId.Admission_Started, BackupAdmission.Busy => TextId.Admission_Busy, _ => TextId.Admission_NotReady
    }];
    public static string SourceProblem(IAppStrings S, FluffHistory.Engine.SourceProblem problem) => problem.Kind == FluffHistory.Engine.SourceProblemKind.Access
        ? problem.NativeError switch
        {
            32 or 33 => S[TextId.SourceProblem_FileInUse], 5 => S[TextId.SourceProblem_AccessDenied],
            2 or 3 => S[TextId.SourceProblem_NotFound], { } code => S.Format(TextId.SourceProblem_WindowsError, code),
            _ => S[TextId.SourceProblem_Access]
        }
        : S[problem.Kind switch
        {
            FluffHistory.Engine.SourceProblemKind.UnsupportedName => TextId.SourceProblem_UnsupportedName,
            FluffHistory.Engine.SourceProblemKind.AmbiguousAlias => TextId.SourceProblem_AmbiguousAlias,
            FluffHistory.Engine.SourceProblemKind.UnsupportedBoundary => TextId.SourceProblem_UnsupportedBoundary,
            FluffHistory.Engine.SourceProblemKind.Oversized => TextId.SourceProblem_Oversized,
            FluffHistory.Engine.SourceProblemKind.SourceChanged => TextId.SourceProblem_SourceChanged,
            FluffHistory.Engine.SourceProblemKind.RootBoundary => TextId.SourceProblem_RootBoundary,
            FluffHistory.Engine.SourceProblemKind.DirectoryBoundary => TextId.SourceProblem_DirectoryBoundary,
            _ => TextId.SourceProblem_Access
        }];
}
