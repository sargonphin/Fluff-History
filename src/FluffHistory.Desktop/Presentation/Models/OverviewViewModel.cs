using FluffHistory.Application;

namespace FluffHistory.Desktop.Presentation.Models;

public sealed class OverviewViewModel : ObservableObject
{
    private IAppStrings S => _owner.Strings;
    private readonly IDesktopContext _owner;
    public OverviewViewModel(IDesktopContext owner, IDesktopDialogs dialogs)
    {
        _owner = owner;
        Storage = new(owner, includeBackupSize: true);
        Storage.PropertyChanged += (_, _) => Changed(nameof(ResultDetail));
        OpenUpdates = new(() => { dialogs.Navigate("Updates"); return Task.CompletedTask; });
        Setup = new(() => { dialogs.Navigate("Configuration"); return Task.CompletedTask; });
        OpenHistory = new(() => { dialogs.Navigate("History"); return Task.CompletedTask; }, () => IsReady);
        BackUp = new(() => owner.RunAsync(owner.BackUpNowAsync), () => owner.AgentAvailable && IsReady && owner.Settings.Sources.Any(s => s.RuntimePath is not null) && !IsActive && owner.ConfigurationProblem.Length == 0);
        Cancel = new(owner.CancelBackupAsync, () => IsActive && owner.Status?.CancellationRequested != true);
        Recover = new(async () =>
        {
            var password = await dialogs.PasswordAsync(false); if (password is null) return;
            try { await owner.RunAsync(token => owner.RecoverAsync(password, token)); }
            finally { Array.Clear(password); }
        }, () => NeedsRecovery);
        owner.Changed += Update;
    }
    public AsyncCommand OpenUpdates { get; }
    public bool HasUpdate => UpdatePresentation.HasNewerRelease(_owner);
    public bool HasNoUpdate => !HasUpdate;
    public bool ShowUpdateStatus => IsReady && !IsActive;
    public string UpdateText => HasUpdate ? S[TextId.Overview_NewVersion] :
        UpdatePresentation.CurrentVersion(_owner) is not { } version ? S[TextId.Update_Development] :
        _owner.Updates?.LastCheckedUtc is null ? S[TextId.Update_NotChecked] :
        _owner.Updates?.Failure != UpdateFailure.None ? S[TextId.Update_Network] : S.Format(TextId.Overview_UpToDate, S.Technical(version));
    public AsyncCommand Setup { get; }
    public StorageUsageViewModel Storage { get; }
    public string StoragePath => _owner.Settings.RepositoryPath;
    public AsyncCommand OpenHistory { get; }
    public AsyncCommand BackUp { get; }
    public AsyncCommand Cancel { get; }
    public AsyncCommand Recover { get; }
    public bool IsUnconfigured => _owner.Settings.RepositoryPath.Length == 0;
    public bool IsReady => !_owner.EncryptionBusy && !IsUnconfigured && _owner.Inspection.State == RepositoryReadiness.Ready;
    public bool NeedsRecovery => _owner.Inspection.State == RepositoryReadiness.RecoveryRequired;
    public bool IsActive => _owner.Status?.IsAttemptActive == true;
    public bool ShowIdleDetails => !IsActive && !IsUnconfigured;
    public bool HasMappings => _owner.Settings.Sources.Any(s => s.RuntimePath is not null && s.IsProtected);
    public string Title => _owner.EncryptionBusy ? S[TextId.Overview_EncryptionBusy] : !_owner.AgentAvailable ? S[TextId.Overview_AgentUnavailable] : IsUnconfigured ? S[TextId.Overview_Setup] : NeedsRecovery ? S[TextId.Status_RecoveryRequired] : IsActive ? S[TextId.Overview_BackupRunning] : _owner.ConfigurationProblem.Length != 0 ? S[TextId.Overview_BackupAttention] :
        !IsReady ? PresentationText.Readiness(S, _owner.Inspection.State) : _owner.Status?.LastActivityOutcome is { } result && result != BackupOutcome.Completed
            ? PresentationText.Outcome(S, result) : S[TextId.Status_Ready];
    public string Summary => IsUnconfigured ? S[TextId.Overview_SetupExplanation] :
        S.Format(TextId.Overview_ConfigurationSummary, S.Technical(_owner.Settings.RepositoryPath), (_owner.Inspection.Encrypted ? S[TextId.Encryption_Encrypted] : S[TextId.Encryption_Unencrypted]), S.Count(CountKind.Folders, _owner.Settings.Sources.Count(s => s.RuntimePath is not null && s.IsProtected)));
    public string Schedule => _owner.Settings.SchedulingEnabled ? S[TextId.Overview_SchedulingEnabled] : S[TextId.Overview_SchedulingDisabled];
    public string CurrentPhase => !IsActive ? "" : _owner.Status!.Progress.Phase switch
    {
        BackupPhase.CheckingSafeguards => S[TextId.Progress_CheckingSafeguards],
        BackupPhase.OpeningRepository => S[TextId.Progress_OpeningBackup],
        BackupPhase.Scanning => S[TextId.Progress_Scanning],
        BackupPhase.Maintaining => S[TextId.Progress_Retention],
        BackupPhase.CheckingMetadata => S[TextId.Progress_CheckingMetadata],
        BackupPhase.CompactingMetadata => S[TextId.Progress_CompactingMetadata],
        BackupPhase.CleaningMetadata => S[TextId.Progress_CleaningMetadata],
        BackupPhase.CompressingMetadata => S[TextId.Progress_CompressingMetadata],
        BackupPhase.Encrypting => S[TextId.Progress_Encrypting],
        BackupPhase.Decrypting => S[TextId.Progress_Decrypting],
        _ => S[TextId.Progress_Finishing]
    };
    public bool IsProgressIndeterminate => IsActive && _owner.Status?.Progress.IsDeterminate != true;
    public double ProgressPercent => IsActive && _owner.Status?.Progress is { IsDeterminate: true, TotalUnits: { } total, CompletedUnits: { } completed }
        ? 100d * completed / total : 0;
    public string CurrentBackup => IsActive ? S[TextId.Overview_BackupRunning] + (_owner.Status!.CancellationRequested ? S[TextId.Overview_CancellationRequested] : "") :
        _owner.Status?.LastAttempt is { } attempt ? LastBackup(attempt) :
        SavedStatus is { } saved ? LastBackup(saved.FinishedUtc, saved.Duration, saved.Outcome) : "";
    public bool HasCurrentBackup => CurrentBackup.Length != 0;
    private LastBackupStatus? SavedStatus => _owner.Status?.LastAttempt is null ? _owner.SavedBackupStatus : null;
    private string LastBackup(AttemptSnapshot attempt)
        => LastBackup(attempt.FinishedUtc, attempt.Duration, attempt.Result.Outcome);
    private string LastBackup(DateTimeOffset finishedUtc, TimeSpan duration, BackupOutcome outcome)
    {
        var success = outcome is BackupOutcome.Completed or BackupOutcome.CompletedWithSourceProblems;
        var text = S.Format(success ? TextId.Overview_LastBackup : TextId.Overview_LastAttempt, finishedUtc.ToLocalTime(), PresentationText.Duration(S, duration));
        return outcome == BackupOutcome.Completed ? text : text + "\n" + PresentationText.Outcome(S, outcome);
    }
    private bool HasCompleteResult => !IsActive && _owner.Status?.LastAttempt?.Result is
        { Outcome: BackupOutcome.Completed or BackupOutcome.CompletedWithSourceProblems, Scan: { Partial: false, Cancelled: false } };
    public bool HasCompletedCounts => Counts.Length != 0;
    public string Counts => HasCompleteResult && _owner.Status?.LastAttempt?.Result.Scan is { } scan ?
        S.Count(CountKind.NewVersions, scan.VersionsCreated) :
        !IsActive && SavedStatus is { Successful: true, VersionsSaved: { } versions } ? S.Count(CountKind.NewVersions, versions) : "";
    public string ResultDetail => IsActive ? "" : HasCompleteResult && _owner.Status?.LastAttempt?.Result.Scan is { } scan ?
        S.Count(CountKind.FileProblems, scan.SourceSkips) + " · " + Storage.BackupSize :
        SavedStatus is { Successful: true, FileProblems: { } problems } ? S.Count(CountKind.FileProblems, problems) + " · " + Storage.BackupSize : Storage.BackupSize;
    public bool HasSourceProblems => !IsActive && _owner.Status?.LastAttempt?.Result.Scan is { SourceSkips: > 0 };
    public string SourceProblems
    {
        get
        {
            if (_owner.Status?.LastAttempt?.Result.Scan is not { SourceSkips: > 0 } scan) return "";
            static string Plain(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            var entries = scan.Problems.Take(32).Select(problem =>
            {
                var source = _owner.Settings.Sources.FirstOrDefault(s => s.SourceRootId == problem.SourceRootId);
                var root = source?.RuntimePath ?? source?.OriginalPath ?? S[TextId.Overview_UnmappedFolder];
                var path = problem.RelativePath.Length == 0 ? root : root.TrimEnd('/', '\\') + Path.DirectorySeparatorChar + problem.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                return S.Technical(Plain(path)) + "\n" + PresentationText.SourceProblem(S, problem);
            }).ToArray();
            return string.Join("\n\n", entries) + (scan.SourceSkips > entries.Length ? S.Format(TextId.Overview_ProblemsLimited, entries.Length, scan.SourceSkips) : "");
        }
    }
    public string Attention => _owner.Inspection.State == RepositoryReadiness.EncryptionMigrationRequired ? S[TextId.Overview_MigrationAdvice] :
        _owner.ConfigurationProblem.Length != 0 ? _owner.ConfigurationProblem : NeedsRecovery ? S[TextId.Overview_RecoveryAdvice] :
        _owner.Status?.LastAttempt?.Result.Maintenance is { NeedsAttention: true } ? S[TextId.Overview_MaintenanceAdvice] :
        _owner.Status?.LastAttempt?.Result.Scan is { ReconciliationMayBeRequired: true } ? S[TextId.Overview_ReconciliationAdvice] :
        _owner.Status?.LastAttempt?.Result.Scan is { CapacityBlocked: true } ? S[TextId.Overview_CapacityAdvice] :
        _owner.Status?.LastSafeguard is { Allowed: false } safeguard ? PresentationText.Safeguard(S, safeguard.Reason) :
        _owner.Status?.LastAttempt?.Result.Scan is { SourceSkips: > 0 } ? S[TextId.Overview_SourceProblemsAdvice] :
        IsReady && !HasMappings ? S[TextId.Overview_AddFoldersAdvice] : "";
    public bool HasAttention => Attention.Length != 0;
    private void Update()
    {
        _ = Storage.SetPathAsync(_owner.Settings.RepositoryPath);
        Storage.ObserveState();
        Changed(""); foreach (var command in new[] { BackUp, Cancel, Recover, OpenHistory }) command.Refresh();
    }
}
