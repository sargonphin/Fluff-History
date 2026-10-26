using System.Collections.ObjectModel;
using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Core;

namespace FluffHistory.Desktop.Presentation.Models;

public sealed record SourceRow(SourceMapping Mapping, FolderPresentation Presentation)
{
    public string Path => Mapping.RuntimePath ?? Mapping.OriginalPath;
    public string Label => Presentation.Label;
    public PersonalFolderKind IconKind => Presentation.Kind;
    public string Description => Label == Path ? "" : Path;
    public override string ToString() => Label;
}

public sealed record ExcludedFolderRow(string Path)
{
    public string Label => Path;
}

public sealed class ConfigurationViewModel : ObservableObject
{
    private IAppStrings S => _owner.Strings;
    private readonly IDesktopContext _owner;
    private readonly IDesktopDialogs _dialogs;
    private readonly List<SourceMapping> _mappings = [];
    private AppSettings? _baseline;
    private AppSettings? _editableBaseline;
    private ImmutableArray<string> _excluded = [];
    private long _editGeneration;
    private string _repository = "", _inspectedPath = "", _message = "";
    private double _interval = 60;
    private bool _enabled, _battery, _metered, _offline = true, _unknown;
    private RepositoryInspection _inspection = new(RepositoryReadiness.Missing);
    private SourceRow? _selected;
    private ExperimentalRecoveryState _experimentalState;
    private string _experimentalPath = "";
    private Guid? _experimentalRepository;
    private bool _securityActivated;
    private string _requestedSecurityPath = "";
    private Guid? _requestedSecurityRepository;
    private long _securityReadEpoch;
    private bool ExperimentalStateApplies => SamePath(_experimentalPath, _owner.Settings.RepositoryPath) && _experimentalRepository == _owner.Inspection.RepositoryId;
    private ExcludedFolderRow? _selectedExclusion;
    private SavedVersionPolicy _retention = ProductConfiguration.Retention(ProductConfiguration.DefaultRetentionPolicyName);
    public ObservableCollection<SourceRow> Sources { get; } = [];
    public ObservableCollection<ExcludedFolderRow> ExcludedFolders { get; } = [];
    public ImmutableArray<BackupFrequency> Frequencies { get; private set; }
    public ImmutableArray<SavedVersionPolicy> RetentionPolicies { get; private set; }
    private string? _choicesLanguage;
    private void RefreshChoices()
    {
        if (_choicesLanguage == S.Language) return;
        _choicesLanguage = S.Language;
        Frequencies = ProductConfiguration.Frequencies.Select(f => f with { Label = PresentationText.Frequency(S, f.Minutes) }).ToImmutableArray();
        RetentionPolicies = ProductConfiguration.RetentionPolicies.Select(p => p with
        { Label = PresentationText.Retention(S, p.Policy), Notice = PresentationText.RetentionNotice(S, p.Policy) }).ToImmutableArray();
        _retention = RetentionPolicies.Single(p => p.Name == _retention.Name);
    }
    public ConfigurationViewModel(IDesktopContext owner, IDesktopDialogs dialogs)
    {
        _owner = owner; _dialogs = dialogs;
        RefreshChoices();
        Storage = new(owner, includeBackupSize: false);
        Apply = new(() => owner.RunAsync(async token =>
        {
            var draft = BuildDraft(DateTimeOffset.UtcNow);
            if (await owner.ValidateWorkspaceAsync(draft, token) is { } failure) { _message = failure; Changed(""); return; }
            await owner.SaveAsync(draft, token); Load(owner.Settings); await RefreshSecurityAsync(token);
        }), () => IsDirty && Validation.Length == 0 && !owner.ConfigurationNeedsReset && !MigrationRequired && !owner.EncryptionBusy);
        Discard = new(() => { Load(owner.Settings); return Task.CompletedTask; }, () => IsDirty);
        ChangeLocation = new(() => owner.RunAsync(ChangeLocationAsync));
        Add = new(() => owner.RunAsync(_ => AddFoldersAsync()), () => RepositoryPath.Length != 0 && _inspection.State != RepositoryReadiness.EncryptionMigrationRequired);
        Remove = new(() =>
        {
            if (SelectedSource is { } row)
            {
                var index = _mappings.FindIndex(s => s.SourceRootId == row.Mapping.SourceRootId);
                _mappings[index] = row.Mapping with { IsProtected = false }; RebuildSources();
                _message = S[TextId.Configuration_FolderRemoved]; DraftChanged();
            }
            return Task.CompletedTask;
        }, () => SelectedSource is not null);
        ExcludeFolder = new(() => owner.RunAsync(_ => ExcludeFolderAsync()));
        RemoveExclusion = new(() =>
        {
            if (SelectedExclusion is { } exclusion)
            {
                _excluded = FolderExclusions.Remove(_excluded, exclusion.Path);
                RebuildExclusions();
                _message = S[TextId.Configuration_ExclusionRemoved];
                DraftChanged();
            }
            return Task.CompletedTask;
        }, () => SelectedExclusion is not null);
        Recover = new(() => owner.RunAsync(async token =>
        {
            var password = await dialogs.PasswordAsync(false); if (password is null) return;
            try { await owner.RecoverAsync(password, token); _inspection = owner.Inspection; DraftChanged(); }
            finally { Array.Clear(password); }
        }), () => NeedsRecovery);
        ChangePassword = new(() => owner.RunAsync(async token =>
        {
            var password = await dialogs.NewRecoveryPasswordAsync(); if (password is null) return;
            try { await SecurityOperationAsync(RepositorySecurityOperation.ChangePassword, password, token); }
            finally { Array.Clear(password); }
        }), () => SecurityReady);
        ToggleExperimental = new(() => owner.RunAsync(token => SecurityOperationAsync(
            ExperimentalEnabled ? RepositorySecurityOperation.DisableRoaming : RepositorySecurityOperation.EnableRoaming, null, token)), () => SecurityReady && ExperimentalStateApplies);
        VerifyExperimental = new(() => owner.RunAsync(token => SecurityOperationAsync(RepositorySecurityOperation.VerifyRoaming, null, token)),
            () => SecurityReady && ExperimentalStateApplies && _experimentalState == ExperimentalRecoveryState.Available);
        RepairExperimental = new(() => owner.RunAsync(async token =>
        {
            if (await dialogs.ConfirmAsync(S[TextId.OneDrive_RepairTitle], S[TextId.OneDrive_RepairExplanation], S[TextId.Action_Continue]))
                await SecurityOperationAsync(RepositorySecurityOperation.RepairRoaming, null, token);
        }), () => SecurityReady && ExperimentalStateApplies && ExperimentalEnabled);
        DisableExperimentalLocally = new(() => owner.RunAsync(async token =>
        {
            if (await dialogs.ConfirmAsync(S[TextId.OneDrive_DisableLocallyTitle], S[TextId.OneDrive_DisableLocallyExplanation], S[TextId.Action_Continue]))
                await SecurityOperationAsync(RepositorySecurityOperation.DisableRoamingLocally, null, token);
        }), () => SecurityReady && ExperimentalStateApplies && ExperimentalEnabled);
        RecoverExperimental = new(() => owner.RunAsync(token => SecurityOperationAsync(RepositorySecurityOperation.RecoverRoaming, null, token)),
            () => CanOfferExperimentalRecovery);
        TurnOnEncryption = new(() => ChangeEncryptionAsync(true, false), () => CanChangeEncryption && !IsEncrypted);
        TurnOffEncryption = new(() => ChangeEncryptionAsync(false, false), () => CanChangeEncryption && IsEncrypted);
        ResumeEncryption = new(() => ChangeEncryptionAsync(_owner.Inspection.EncryptionChange?.TargetEncrypted ?? false, true),
            () => MigrationRequired && _owner.Inspection.EncryptionChange is not null && !_owner.EncryptionBusy);
        PauseEncryption = new(() => { _owner.PauseEncryption(); return Task.CompletedTask; }, () => _owner.EncryptionBusy);
        Reset = new(async () =>
        {
            if (await dialogs.ConfirmAsync(S[TextId.Configuration_ResetTitle], S[TextId.Configuration_ResetExplanation], S[TextId.Configuration_ResetAction]))
                await owner.RunAsync(token => owner.SaveAsync(AppSettings.Empty, token, reset: true));
        });
        CommandGroup.Attach(owner, () => Changed(""), Apply, Discard, ChangeLocation, Add, Remove, ExcludeFolder, RemoveExclusion, Recover, Reset,
            ChangePassword, ToggleExperimental, RecoverExperimental, VerifyExperimental, RepairExperimental, DisableExperimentalLocally,
            TurnOnEncryption, TurnOffEncryption, ResumeEncryption, PauseEncryption);
        PauseEncryption.GroupAvailable = null; // Pause must remain available while a conversion command owns the group.
        Load(owner.Settings);
        owner.Changed += () =>
        {
            RefreshChoices();
            Storage.ObserveState();
            if (!ReferenceEquals(_baseline, owner.Settings)) Load(owner.Settings);
            else { if (SamePath(RepositoryPath, owner.Settings.RepositoryPath)) _inspection = owner.Inspection; DraftChanged(); }
            if (_securityActivated && (!string.Equals(_requestedSecurityPath, owner.Settings.RepositoryPath, StringComparison.OrdinalIgnoreCase) ||
                _requestedSecurityRepository != owner.Inspection.RepositoryId)) _ = RefreshSecurityAsync();
        };
    }
    public AsyncCommand Apply { get; }
    public StorageUsageViewModel Storage { get; }
    public AsyncCommand Discard { get; }
    public AsyncCommand ChangeLocation { get; }
    public AsyncCommand Add { get; }
    public AsyncCommand Remove { get; }
    public AsyncCommand ExcludeFolder { get; }
    public AsyncCommand RemoveExclusion { get; }
    public AsyncCommand Recover { get; }
    public AsyncCommand Reset { get; }
    public AsyncCommand ChangePassword { get; }
    public AsyncCommand ToggleExperimental { get; }
    public AsyncCommand RecoverExperimental { get; }
    public AsyncCommand VerifyExperimental { get; }
    public AsyncCommand RepairExperimental { get; }
    public AsyncCommand DisableExperimentalLocally { get; }
    public AsyncCommand TurnOnEncryption { get; }
    public AsyncCommand TurnOffEncryption { get; }
    public AsyncCommand ResumeEncryption { get; }
    public AsyncCommand PauseEncryption { get; }
    public bool MigrationRequired => _owner.Inspection.State == RepositoryReadiness.EncryptionMigrationRequired;
    public bool ShowEncryptionMaintenance => MigrationRequired || _owner.EncryptionBusy;
    public bool EncryptionIsActive => _owner.EncryptionBusy;
    public string EncryptionMaintenanceTitle => _owner.EncryptionProgress is { } progress ? PresentationText.EncryptionPhase(S, progress.Phase) : (_owner.Inspection.EncryptionChange is { } change
        ? change.TargetEncrypted ? S[TextId.Encryption_Paused] : S[TextId.Encryption_RemovalPaused] : MigrationRequired ? S[TextId.Encryption_Attention] : S[TextId.Encryption_Preparing]);
    public string EncryptionMaintenanceDetail => _owner.EncryptionProgress is { } progress && progress.TotalBytes > 0
        ? S.Format(TextId.Encryption_Progress, PresentationText.Size(S, progress.CompletedBytes), PresentationText.Size(S, progress.TotalBytes), progress.CompletedFiles, progress.TotalFiles)
        : MigrationRequired && _owner.Inspection.EncryptionChange is null
            ? S[TextId.Encryption_ControlUnverified]
            : S[TextId.Encryption_UnavailableResumable];
    public double EncryptionProgressPercent => _owner.EncryptionProgress is { TotalBytes: > 0 } progress
        ? Math.Clamp(100d * progress.CompletedBytes / progress.TotalBytes, 0, 100) : 0;
    public bool EncryptionIndeterminate => _owner.EncryptionProgress is not
        { Phase: EncryptionChangePhase.EncryptingFiles or EncryptionChangePhase.DecryptingFiles, TotalBytes: > 0 };
    public bool CanChangeEncryption => HasEncryptionStatus && !IsDirty && _owner.AgentAvailable && _owner.Inspection.State == RepositoryReadiness.Ready && _owner.Status?.IsAttemptActive != true && !_owner.EncryptionBusy;
    public bool CanEdit => !MigrationRequired && !_owner.EncryptionBusy && !new[] { Apply, Discard, ChangeLocation, Add, Remove, ExcludeFolder, RemoveExclusion, Recover, Reset,
        ChangePassword, ToggleExperimental, RecoverExperimental, VerifyExperimental, RepairExperimental, DisableExperimentalLocally }.Any(c => c.IsBusy);
    public bool HasEncryptionStatus => _owner.Settings.RepositoryPath.Length != 0 && _owner.Inspection.State is RepositoryReadiness.Ready or RepositoryReadiness.RecoveryRequired;
    public string EncryptionStatus => _owner.Inspection.Encrypted ? S[TextId.Encryption_Encrypted] : S[TextId.Encryption_Unencrypted];
    public bool IsEncrypted => HasEncryptionStatus && _owner.Inspection.Encrypted;
    public bool IsUnencrypted => HasEncryptionStatus && !_owner.Inspection.Encrypted;
    public bool SecurityReady => IsEncrypted && _owner.Inspection.State == RepositoryReadiness.Ready && !IsDirty && _owner.AgentAvailable && !_owner.EncryptionBusy && _owner.Status?.IsAttemptActive != true;
    public string WindowsUnlockStatus => !IsEncrypted ? "" : NeedsRecovery ? S[TextId.Recovery_EnableWindowsAdvice] : S[TextId.Recovery_WindowsAvailable];
    public bool CanOfferExperimentalRecovery => NeedsRecovery && ExperimentalStateApplies && _experimentalState is ExperimentalRecoveryState.Available or ExperimentalRecoveryState.NotAvailable;
    public bool ExperimentalEnabled => ExperimentalStateApplies && _experimentalState is ExperimentalRecoveryState.Available or ExperimentalRecoveryState.NotAvailable or ExperimentalRecoveryState.CleanupRequired or ExperimentalRecoveryState.Invalid;
    public string ExperimentalStatus => !ExperimentalStateApplies ? S[TextId.Experimental_Checking] : _experimentalState switch
    {
        ExperimentalRecoveryState.Available => S[TextId.Experimental_Available],
        ExperimentalRecoveryState.NotAvailable => S[TextId.Experimental_NotAvailable],
        ExperimentalRecoveryState.CleanupRequired => S[TextId.Experimental_CleanupRequired],
        ExperimentalRecoveryState.Invalid => S[TextId.Experimental_Invalid],
        ExperimentalRecoveryState.Unavailable => S[TextId.Experimental_Unavailable],
        ExperimentalRecoveryState.Legacy => S[TextId.OneDrive_Legacy],
        ExperimentalRecoveryState.ConfigurationRequired => S[TextId.OneDrive_NotConfigured],
        _ => S[TextId.Experimental_Off]
    };
    public string ExperimentalExplanation => S[TextId.Experimental_Explanation];
    public async Task RefreshSecurityAsync(CancellationToken token = default)
    {
        var path = _owner.Settings.RepositoryPath; var repository = _owner.Inspection.RepositoryId;
        _securityActivated = true; _requestedSecurityPath = path; _requestedSecurityRepository = repository;
        var epoch = ++_securityReadEpoch;
        var state = await _owner.InspectSecurityAsync(token);
        if (epoch == _securityReadEpoch && SamePath(path, _owner.Settings.RepositoryPath) && repository == _owner.Inspection.RepositoryId)
        { _experimentalPath = path; _experimentalRepository = repository; _experimentalState = state; }
        DraftChanged();
    }
    private async Task SecurityOperationAsync(RepositorySecurityOperation operation, char[]? password, CancellationToken token)
    {
        var result = await _owner.ExecuteSecurityAsync(operation, password, token);
        await RefreshSecurityAsync(token);
        _message = result.CloudFailure is { } cloudFailure && result.Outcome is SecurityOperationOutcome.Unavailable or SecurityOperationOutcome.NotAvailable or SecurityOperationOutcome.AuthenticationFailed
            ? CloudFailureText(cloudFailure) : result.Outcome switch
        {
            SecurityOperationOutcome.Success => operation switch
            {
                RepositorySecurityOperation.ChangePassword => S[TextId.Security_PasswordChanged],
                RepositorySecurityOperation.EnableRoaming or RepositorySecurityOperation.RepairRoaming => S[TextId.Security_ExperimentalEnabled],
                RepositorySecurityOperation.DisableRoaming => S[TextId.Security_ExperimentalDisabled],
                RepositorySecurityOperation.DisableRoamingLocally => S[TextId.OneDrive_DisabledLocally],
                RepositorySecurityOperation.VerifyRoaming => S[TextId.Security_ExperimentalVerified],
                _ => S[TextId.Security_RecoveryCompleted]
            },
            SecurityOperationOutcome.NotAvailable => S[TextId.Security_CredentialNotAvailable],
            SecurityOperationOutcome.AuthenticationFailed => S[TextId.Security_AuthenticationFailed],
            SecurityOperationOutcome.Uncertain => S[TextId.Security_Uncertain],
            SecurityOperationOutcome.Cancelled => S[TextId.Security_Cancelled],
            _ => S[TextId.Security_Failed]
        };
        if (result.Cleanup.HasFlag(OneDriveRecoveryCleanup.Remote))
        {
            var cleanup = RemoteCleanupText(result.CleanupFileName);
            _message = result.CloudFailure == CloudRecoveryFailure.CleanupRequired ? cleanup : _message + "\n\n" + cleanup;
        }
        if (result.Cleanup.HasFlag(OneDriveRecoveryCleanup.Legacy)) _message += "\n\n" + S[TextId.OneDrive_LegacyCleanup];
        await _dialogs.NoticeAsync(S[TextId.Security_Title], _message); DraftChanged();
    }
    private string RemoteCleanupText(string? fileName) => fileName is null ? S[TextId.OneDrive_RemoteCleanup]
        : S.Format(TextId.OneDrive_RemoteCleanupFile, fileName);
    private string CloudFailureText(CloudRecoveryFailure failure) => S[failure switch
    {
        CloudRecoveryFailure.ConfigurationRequired => TextId.OneDrive_NotConfigured,
        CloudRecoveryFailure.SignInCancelled => TextId.Security_Cancelled,
        CloudRecoveryFailure.AuthenticationRequired => TextId.OneDrive_SignInRequired,
        CloudRecoveryFailure.ConsentDenied => TextId.OneDrive_ConsentDenied,
        CloudRecoveryFailure.PermissionUnavailable => TextId.OneDrive_PermissionUnavailable,
        CloudRecoveryFailure.OneDriveUnavailable => TextId.OneDrive_Unavailable,
        CloudRecoveryFailure.ObjectNotFound => TextId.Security_CredentialNotAvailable,
        CloudRecoveryFailure.ObjectChanged or CloudRecoveryFailure.InvalidObject => TextId.OneDrive_InvalidObject,
        CloudRecoveryFailure.NetworkUnavailable => TextId.OneDrive_Offline,
        CloudRecoveryFailure.Throttled => TextId.OneDrive_Throttled,
        CloudRecoveryFailure.CleanupRequired => TextId.OneDrive_RemoteCleanup,
        _ => TextId.OneDrive_ServiceUnavailable
    }];
    private async Task ChangeEncryptionAsync(bool encrypted, bool resume)
    {
        await _owner.RunAsync(async token =>
        {
            char[]? password = null;
            try
            {
                if (!resume)
                {
                    var explanation = encrypted
                        ? S[TextId.Encryption_EnableExplanation]
                        : S[TextId.Encryption_DisableExplanation];
                    if (!await _dialogs.ConfirmAsync(encrypted ? S[TextId.Encryption_EnableTitle] : S[TextId.Encryption_DisableTitle], explanation, S[TextId.Action_Continue])) return;
                    if (encrypted) { password = await _dialogs.PasswordAsync(true); if (password is null) return; }
                }
                var result = await _owner.ChangeEncryptionAsync(encrypted, password, token); password = null;
                if (result.Outcome == EncryptionChangeOutcome.RecoveryRequired && !token.IsCancellationRequested)
                {
                    password = await _dialogs.MigrationRecoveryPasswordAsync(); if (password is null) return;
                    result = await _owner.ChangeEncryptionAsync(encrypted, password, token); password = null;
                }
                _message = result.Outcome switch
                {
                    EncryptionChangeOutcome.Completed => S[TextId.Encryption_Completed] + (result.CleanupDeferred ? encrypted
                        ? S[TextId.Encryption_PlaintextCleanupDeferred]
                        : S[TextId.Encryption_CleanupDeferred] : ""),
                    EncryptionChangeOutcome.Paused => MigrationRequired ? S[TextId.Encryption_PausedAdvice] : S[TextId.Encryption_NotStarted],
                    EncryptionChangeOutcome.MoreSpaceRequired => MigrationRequired ? S[TextId.Encryption_MoreSpaceResume]
                        : S[TextId.Encryption_MoreSpaceStart],
                    EncryptionChangeOutcome.RecoveryRequired => S[TextId.Encryption_RecoveryRequired],
                    _ => MigrationRequired ? S[TextId.Encryption_ResumeAttention]
                        : S[TextId.Encryption_StartAttention]
                };
                if (result.RemoteRecoveryCleanupRequired) _message += "\n\n" + RemoteCleanupText(result.RemoteCleanupFileName);
                await RefreshSecurityAsync(token);
                await _dialogs.NoticeAsync(S[TextId.Security_Title], _message); DraftChanged();
            }
            finally { if (password is not null) Array.Clear(password); }
        });
    }
    public string RepositoryPath { get => _repository; set { if (Set(ref _repository, value)) DraftChanged(); } }
    public double IntervalMinutes { get => _interval; set { if (Set(ref _interval, value)) DraftChanged(); } }
    public BackupFrequency SelectedFrequency { get => Frequencies.FirstOrDefault(f => f.Minutes == IntervalMinutes) ?? Frequencies[2]; set { if (value is not null) IntervalMinutes = value.Minutes; } }
    public SavedVersionPolicy SelectedRetention { get => _retention; set { if (value is not null && Set(ref _retention, value)) DraftChanged(); } }
    public string RetentionNotice => SelectedRetention.Notice;
    public bool ForeverWarning => SelectedRetention.Name == "forever";
    public bool RetentionInformation => !ForeverWarning;
    public string RetentionNoticeTitle => ForeverWarning ? S[TextId.Configuration_ForeverTitle] : "";
    public bool SchedulingEnabled { get => _enabled; set { if (Set(ref _enabled, value)) DraftChanged(); } }
    public bool PauseOnBattery { get => !_battery; set { if (Set(ref _battery, !value)) DraftChanged(); } }
    public bool AllowMetered { get => _metered; set { if (Set(ref _metered, value)) DraftChanged(); } }
    public bool DeferOffline { get => _offline; set { if (Set(ref _offline, value)) DraftChanged(); } }
    public bool BlockUnknownPower { get => _unknown; set { if (Set(ref _unknown, value)) DraftChanged(); } }
    public SourceRow? SelectedSource { get => _selected; set { if (Set(ref _selected, value is not null && Sources.Contains(value) ? value : null)) DraftChanged(); } }
    public ExcludedFolderRow? SelectedExclusion { get => _selectedExclusion; set { if (Set(ref _selectedExclusion, value is not null && ExcludedFolders.Contains(value) ? value : null)) RemoveExclusion.Refresh(); } }
    public bool HasExcludedFolders => ExcludedFolders.Count != 0;
    public bool NoExcludedFolders => !HasExcludedFolders;
    public string ExclusionsEmptyMessage => S[TextId.Configuration_NoExclusions];
    public string RepositoryStatus => RepositoryPath.Length == 0 ? S[TextId.Configuration_ChooseStorage] :
        !SamePath(RepositoryPath, _inspectedPath) ? S[TextId.Configuration_ChooseLocationContinue] :
        _inspection.State == RepositoryReadiness.EncryptionMigrationRequired && _inspection.EncryptionChange is { } change ?
            change.TargetEncrypted ? S[TextId.Configuration_EncryptionPaused] : S[TextId.Configuration_DecryptionPaused] :
        PresentationText.Readiness(S, _inspection.State) + (_inspection.State is RepositoryReadiness.Ready or RepositoryReadiness.RecoveryRequired ? $" · {(_inspection.Encrypted ? S[TextId.Encryption_Encrypted] : S[TextId.Encryption_Unencrypted])}" : "");
    public bool NeedsRecovery => _owner.Inspection.State == RepositoryReadiness.RecoveryRequired && SamePath(RepositoryPath, _owner.Settings.RepositoryPath);
    public string DraftStatus => IsDirty ? S[TextId.Configuration_Unsaved] : S[TextId.Configuration_UpToDate];
    public string Message => _message;
    public bool IsDirty => _baseline is not null && (RepositoryPath != _baseline.RepositoryPath || IntervalMinutes != _baseline.IntervalMinutes ||
        SchedulingEnabled != _baseline.SchedulingEnabled || SelectedRetention.Name != _baseline.RetentionPolicyName ||
        new SafeguardPolicy(_battery, _unknown, _metered, _offline) != _baseline.Safeguards ||
        !SameSources(_mappings, _editableBaseline!.Sources) || !_excluded.SequenceEqual(_editableBaseline.ExcludedFolders, StringComparer.OrdinalIgnoreCase));
    public string Validation
    {
        get
        {
            if (!ProductConfiguration.Frequencies.Any(f => f.Minutes == IntervalMinutes)) return S[TextId.Configuration_ChooseFrequency];
            if (RepositoryPath.Length == 0) return S[TextId.Configuration_ChooseLocation];
            if (!SamePath(RepositoryPath, _inspectedPath)) return S[TextId.Configuration_ChooseLocationSave];
            if (_inspection.State is not (RepositoryReadiness.Ready or RepositoryReadiness.RecoveryRequired or RepositoryReadiness.EncryptionMigrationRequired)) return S[TextId.Configuration_LocationUnavailable];
            try { BuildDraft(DateTimeOffset.UtcNow).Validate(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { return PresentationText.Validation(S, error); }
            return "";
        }
    }
    public AppSettings BuildDraft(DateTimeOffset saveUtc) => (_baseline ?? _owner.Settings) with
    {
        Schema = 2, RepositoryPath = RepositoryPath,
        Sources = _owner.FolderPolicy.Prepare(RepositoryPath, _mappings, BackupWorkspace.DefaultRoot),
        ExcludedFolders = _excluded,
        IntervalMinutes = checked((int)IntervalMinutes), AnchorUtc = IntervalMinutes == _baseline?.IntervalMinutes ? _baseline.AnchorUtc : saveUtc,
        SchedulingEnabled = SchedulingEnabled, Safeguards = new(_battery, _unknown, _metered, _offline), RetentionPolicyName = SelectedRetention.Name
    };
    private async Task ChangeLocationAsync(CancellationToken token)
    {
        var path = await _dialogs.FolderAsync(); if (path is null) return;
        var setup = new RepositorySetupService(); var location = await setup.InspectLocationAsync(path, token); var inspection = location.Backup;
        if (location.Kind == BackupLocationKind.Unrelated)
        {
            _message = S[TextId.Configuration_UnrelatedFolder];
            await _dialogs.NoticeAsync(S[TextId.Configuration_ChooseAnotherLocation], _message); Changed(""); return;
        }
        if (location.Kind == BackupLocationKind.Unavailable)
        {
            _message = S[TextId.Configuration_InaccessibleLocation];
            await _dialogs.NoticeAsync(S[TextId.Status_BackupUnavailable], _message); Changed(""); return;
        }
        if (location.Kind == BackupLocationKind.Empty)
        {
            var protect = await _dialogs.ProtectBackupAsync(path); if (protect is null) return;
            var password = protect.Value ? await _dialogs.PasswordAsync(true) : null; if (protect.Value && password is null) return;
            try { inspection = await setup.CreateAsync(path, password, token); }
            finally { if (password is not null) Array.Clear(password); }
        }
        else
        {
            var count = inspection.Sources.IsDefault ? "" : S.Format(TextId.Configuration_FoldersInTimeline, inspection.Sources.Length);
            if (!await _dialogs.ConfirmLocationAsync(new(path,
                $"{PresentationText.Readiness(S, inspection.State)} · {(inspection.Encrypted ? S[TextId.Encryption_Encrypted] : S[TextId.Encryption_Unencrypted])}{count}",
                S[TextId.Configuration_AdoptExplanation]))) return;
            if (inspection.State == RepositoryReadiness.RecoveryRequired)
            {
                var password = await _dialogs.PasswordAsync(false); if (password is null) return;
                RecoveryResult recovery;
                try { recovery = await _owner.RecoverLocationAsync(path, password, token); } finally { Array.Clear(password); }
                if (recovery.Outcome != RecoveryOutcome.Success) { _message = recovery.Outcome == RecoveryOutcome.WrongPassword ? S[TextId.Configuration_PasswordFailed] : S[TextId.Configuration_RecoveryUncertain]; Changed(""); return; }
                inspection = await setup.InspectAsync(path, token);
            }
        }
        if (!SamePath(path, RepositoryPath))
        {
            // Keep the removable first-setup suggestion when choosing an empty backup.
            // Adopting existing roots retains its explicit remapping workflow/identities.
            if (RepositoryPath.Length != 0 || !inspection.Sources.IsDefaultOrEmpty) _mappings.Clear();
            _selected = null; _editGeneration++;
        }
        _repository = _inspectedPath = path; _inspection = inspection;
        foreach (var root in inspection.Sources.IsDefault ? [] : inspection.Sources)
            if (_mappings.All(s => s.SourceRootId != root.SourceRootId)) _mappings.Add(SourceMapping.FromConfiguration(root) with { IsProtected = false });
        RebuildSources(); _message = S[TextId.Configuration_LocationSelected]; DraftChanged();
    }
    public async Task AddFoldersAsync()
    {
        var paths = await _dialogs.FoldersAsync(); if (paths.Count == 0) return;
        var candidate = _mappings.ToList(); var added = 0; var enabled = 0; var duplicate = 0; var rejected = Math.Max(0, paths.Count - 1025);
        var warnings = new List<string>(); var rejectedMessages = new List<string>(); var exclusionConflict = false;
        if (rejected > 0) rejectedMessages.Add(S[TextId.Configuration_FolderLimit]);
        foreach (var selected in paths.Take(1025))
        {
            try
            {
                var path = SourcePaths.Normalize(selected);
                FolderExclusions.ValidateSource(path, _excluded);
                if (SourcePaths.Overlaps(path, RepositoryPath)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.SourceRepositoryOverlap, SourcePaths.OverlapMessage);
                var safety = _owner.FolderPolicy.Classify(path);
                if (safety.Kind == SourceSafetyKind.HardBlocked) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderBlocked, safety.Warning);
                if (candidate.Any(s => s.IsProtected && SamePath(s.RuntimePath, path))) { duplicate++; continue; }
                var parent = candidate.FirstOrDefault(s => s.IsProtected && s.RuntimePath is not null && SourcePaths.Within(path, s.RuntimePath) && !Excluded(s, path));
                if (parent is not null && !safety.RequiresConfirmation) { duplicate++; rejectedMessages.Add(S.Format(TextId.Configuration_FolderCovered, _owner.FolderPolicy.Describe(parent.RuntimePath!).Label)); continue; }
                var existing = candidate.FirstOrDefault(s => !s.IsProtected && (SamePath(s.RuntimePath, path) || s.RuntimePath is null && SamePath(s.OriginalPath, path)));
                if (safety.RequiresConfirmation) warnings.Add(PresentationText.FolderWarning(S, safety.Kind));
                if (existing is not null) { candidate[candidate.IndexOf(existing)] = existing with { IsProtected = true, RuntimePath = path, ForceInclude = safety.RequiresConfirmation || existing.ForceInclude }; enabled++; }
                else { if (candidate.Count >= 1024) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderLimit, "Source folder limit exceeded."); candidate.Add(SourceMapping.New(path) with { ForceInclude = safety.RequiresConfirmation }); added++; }
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                rejected++;
                if (ConfigurationIssues.Code(error) == ConfigurationIssueCode.ExclusionConflict) exclusionConflict = true;
                else rejectedMessages.Add(PresentationText.Validation(S, error));
            }
        }
        if (exclusionConflict) await _dialogs.NoticeAsync(S[TextId.Configuration_ConflictTitle], S[TextId.Configuration_ExclusionConflict]);
        if (warnings.Count != 0 && !await _dialogs.ConfirmAsync(S[TextId.Configuration_ConfirmFoldersTitle], string.Join("\n\n", warnings.Distinct()), S[TextId.Configuration_ForceInclude])) return;
        try
        {
            var prepared = _owner.FolderPolicy.Prepare(RepositoryPath, candidate, BackupWorkspace.DefaultRoot);
            await Task.Run(() => _owner.FolderPolicy.ValidateAliases(RepositoryPath, prepared, BackupWorkspace.DefaultRoot));
            _mappings.Clear(); _mappings.AddRange(prepared); RebuildSources();
            _message = S.Format(TextId.Configuration_FoldersAdded, S.Count(CountKind.Folders, added), S.Count(CountKind.ExistingFolders, enabled), duplicate) + (rejected > 0 ? S.Format(TextId.Configuration_FoldersRejected, rejected) : "") +
                (rejectedMessages.Count == 0 ? "" : "\n" + string.Join("\n", rejectedMessages.Distinct().Take(3)));
        }
        catch (InvalidDataException error) { _message = PresentationText.Validation(S, error); }
        DraftChanged();
    }
    private async Task ExcludeFolderAsync()
    {
        var generation = _editGeneration;
        var path = await _dialogs.FolderAsync();
        if (path is null || generation != _editGeneration) return;
        try
        {
            var updated = FolderExclusions.Add(_excluded, path);
            if (updated.SequenceEqual(_excluded, StringComparer.OrdinalIgnoreCase)) { _message = S[TextId.Configuration_AlreadyExcluded]; DraftChanged(); return; }
            (BuildDraft(DateTimeOffset.UtcNow) with { ExcludedFolders = updated }).Validate();
            _excluded = updated; RebuildExclusions();
            _message = S[TextId.Configuration_FolderExcluded];
        }
        catch (InvalidDataException error) when (ConfigurationIssues.Code(error) == ConfigurationIssueCode.ExclusionConflict)
        {
            await _dialogs.NoticeAsync(S[TextId.Configuration_ConflictTitle], S[TextId.Configuration_ExclusionConflict]);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { _message = PresentationText.Validation(S, error); }
        DraftChanged();
    }
    private static bool Excluded(SourceMapping source, string child)
    {
        var relative = new HistoryPath(source.SourceRootId, SourcePaths.Relative(source.RuntimePath!, child)).CanonicalPath;
        return source.ToConfiguration().ExcludedDirectories.Any(e => relative == e || relative.StartsWith(e + "/", StringComparison.Ordinal));
    }
    private void RebuildSources(Guid? selectedId = null)
    {
        Sources.Clear(); foreach (var source in _mappings.Where(s => s.IsProtected && s.RuntimePath is not null))
        {
            var folder = _owner.FolderPolicy.Describe(source.RuntimePath!);
            Sources.Add(new(source, folder with { Label = PresentationText.FolderName(S, folder) }));
        }
        _selected = Sources.FirstOrDefault(s => s.Mapping.SourceRootId == selectedId);
    }
    private void RebuildExclusions()
    {
        _selectedExclusion = null; ExcludedFolders.Clear();
        foreach (var exclusion in _excluded) ExcludedFolders.Add(new(exclusion));
    }
    private void Load(AppSettings settings)
    {
        _editGeneration++; _baseline = settings; _editableBaseline = FolderExclusions.ForEditing(settings);
        _excluded = _editableBaseline.ExcludedFolders;
        _repository = _inspectedPath = settings.RepositoryPath; _inspection = _owner.Inspection;
        _interval = settings.IntervalMinutes; _enabled = settings.SchedulingEnabled; _retention = RetentionPolicies.Single(p => p.Name == settings.RetentionPolicyName);
        _battery = settings.Safeguards.AllowOnBattery; _unknown = settings.Safeguards.BlockUnknownPower;
        _metered = settings.Safeguards.AllowMeteredNetwork; _offline = settings.Safeguards.DeferNetworkPathsWhenUnavailable;
        _mappings.Clear(); _mappings.AddRange(_editableBaseline.Sources);
        if (settings.RepositoryPath.Length == 0 && settings.Sources.IsEmpty && !_owner.ConfigurationNeedsReset && _owner.DefaultSourceFolder is { Length: > 0 } profile)
        {
            try
            {
                var path = SourcePaths.Normalize(profile);
                FolderExclusions.ValidateSource(path, _excluded);
                if (_owner.FolderPolicy.Classify(path).Kind == SourceSafetyKind.Ordinary) _mappings.Add(SourceMapping.New(path));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { /* An unavailable suggestion must not prevent configuration. */ }
        }
        RebuildSources(); RebuildExclusions(); _message = ""; DraftChanged();
    }
    private void DraftChanged()
    { _ = Storage.SetPathAsync(RepositoryPath); Changed(""); foreach (var command in new[] { Apply, Discard, ChangeLocation, Add, Remove, ExcludeFolder, RemoveExclusion, Recover,
        ChangePassword, ToggleExperimental, RecoverExperimental, VerifyExperimental, RepairExperimental, DisableExperimentalLocally,
        TurnOnEncryption, TurnOffEncryption, ResumeEncryption, PauseEncryption }) command?.Refresh(); }
    private static bool SamePath(string? a, string? b)
    { try { return SourcePaths.Same(a, b); } catch (InvalidDataException) { return false; } }
    private static bool SameSources(IReadOnlyList<SourceMapping> a, IReadOnlyList<SourceMapping> b) => a.Count == b.Count && a.Zip(b).All(pair =>
        pair.First.SourceRootId == pair.Second.SourceRootId && pair.First.OriginalPath == pair.Second.OriginalPath && pair.First.RuntimePath == pair.Second.RuntimePath &&
        pair.First.IsProtected == pair.Second.IsProtected && pair.First.MaximumFileSize == pair.Second.MaximumFileSize && pair.First.ForceInclude == pair.Second.ForceInclude &&
        pair.First.ExcludedDirectories.SequenceEqual(pair.Second.ExcludedDirectories) && pair.First.AutomaticExcludedDirectories.SequenceEqual(pair.Second.AutomaticExcludedDirectories));
}
