using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Windows;
using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Dispatching;

namespace FluffHistory.Desktop.Presentation;

/// <summary>One management window. Foreground commands serialize; the separate agent owns backup.
/// Closing drains only this window's commands/browser/preview and disconnects status observation.</summary>
public sealed class DesktopLifetime(DispatcherQueue dispatcher, PreferencesLoadResult initialPreferences) : IAsyncDisposable, IDesktopContext
{
    private static IAppStrings S => MrtAppStrings.Instance;
    public IAppStrings Strings => S;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _commands = new(1);
    private readonly HashSet<Task> _active = [];
    private readonly object _activeLock = new();
    private bool _closing;
    private CancellationTokenSource? _query;
    private PreviewLease? _preview;
    private long _previewEpoch;
    private readonly WindowsAgentClient _agent = new();
    private readonly WindowsStorageUsage _storageUsage = new();
    private AgentState? _agentState;
    private Task? _agentObserver;
    private long _revision = -1;
    private readonly BackupOperationGate _operationGate = new(new WindowsOperationAdmission(WindowsProductPaths.MutationLock));
    private TimelineMaintenanceService? _maintenance;
    private ExactVersionRemovalService? _versionRemoval;
    private RepositorySecurityService? _security;
    private CancellationTokenSource? _encryptionCancellation;
    private long _encryptionEpoch;
    private readonly DesktopLegacyRecoveryCleanup _legacyRecoveryCleanup = new();
    private OneDriveRecoveryService? _cloudSecurity;
    private RepositorySecurityService Security => _security ??= new(_operationGate);
    private OneDriveRecoveryService CloudSecurity => _cloudSecurity ??= new(_operationGate,
        new OneDriveRecoveryStoreFactory(OneDriveApplicationRegistration.ClientId, () => OwnerWindow), _legacyRecoveryCleanup);
    public bool EncryptionBusy { get; private set; }
    public EncryptionChangeProgress? EncryptionProgress { get; private set; }
    private bool _savedNotice;
    private static string DefaultWorkspace => BackupWorkspace.DefaultRoot;
    public ISourceFolderPolicy FolderPolicy { get; } = new WindowsSourceFolders([WindowsProductPaths.Root, WindowsProductPaths.InstallRoot]);
    public string? DefaultSourceFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public TimelineMaintenanceService Maintenance => _maintenance ??= new(_operationGate, DefaultWorkspace);
    public ExactVersionRemovalService VersionRemoval => _versionRemoval ??= new(_operationGate, DefaultWorkspace);
    public string ConfigurationProblem { get; private set; } = "";
    public nint OwnerWindow { get; set; }
    public long HistoryRevision { get; private set; }
    public string CurrentPage { get; set; } = "Overview";
    public ApplicationFailure? LatestFailure { get; private set; }
    public bool HasError { get; private set; }
    public AppSettingsStore SettingsStore { get; } = new(WindowsProductPaths.Settings); // Read-only in the UI; agent is sole writer.
    public ApplicationPreferencesStore PreferencesStore { get; } = new(WindowsProductPaths.Preferences);
    public bool AgentAvailable { get; private set; }
    public ApplicationPreferences Preferences { get; private set; } = initialPreferences.Preferences;
    public bool PreferencesNeedAttention { get; private set; } = initialPreferences.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable;
    public AppSettings Settings { get; private set; } = AppSettings.Empty;
    public RepositoryInspection Inspection { get; private set; } = new(RepositoryReadiness.Missing);
    public HistoryBrowserSession? Browser { get; private set; }
    public BackupStatus? Status { get; private set; }
    public LastBackupStatus? SavedBackupStatus => _agentState?.LastBackupStatus;
    public UpdateSnapshot? Updates => _agentState?.Updates;
    public async Task SaveUpdatePreferencesAsync(UpdatePreferences preferences, CancellationToken token)
    {
        await new UpdateJsonStore<UpdatePreferences>(WindowsProductPaths.UpdatePreferences, UpdatePreferences.Default, p => p.Validate(), 4096).SaveAsync(preferences, token);
        var reply = await _agent.SendAsync(new(AgentOperation.ReloadUpdatePreferences), token); ApplyAgentState(reply.State); RequireSuccess(reply);
    }
    public async Task CheckUpdatesAsync(CancellationToken token)
    { var reply = await _agent.SendAsync(new(AgentOperation.CheckUpdates), token); ApplyAgentState(reply.State); RequireSuccess(reply); }
    public async Task InstallUpdateAsync(bool cancelBackup, CancellationToken token)
    { var reply = await _agent.SendAsync(new(AgentOperation.InstallUpdate, ConfirmUpdateCancellation: cancelBackup), token); ApplyAgentState(reply.State); RequireSuccess(reply); }
    public string LastOutcome { get; set; } = S[TextId.Status_ReadyToConfigure];
    public string BrowserActivity { get; set; } = "Closed";
    public bool ConfigurationNeedsReset { get; private set; }
    public event Action? Changed;
    public Task<FluffHistory.Core.CapacityObservation?> ObserveDriveSpaceAsync(string path, CancellationToken token) => _storageUsage.ObserveDriveAsync(path, token);
    public Task<long?> ObserveBackupSizeAsync(string path, CancellationToken token) => _storageUsage.ObserveBackupAsync(path, token);

    public async Task RunAsync(Func<CancellationToken, Task> command)
    {
        Task task;
        lock (_activeLock)
        {
            if (_closing) return;
            task = RunSerializedAsync(command); _active.Add(task);
        }
        try { await task; }
        finally { lock (_activeLock) _active.Remove(task); }
    }
    private async Task RunSerializedAsync(Func<CancellationToken, Task> command)
    {
        try
        {
            await _commands.WaitAsync(_shutdown.Token);
            try { _shutdown.Token.ThrowIfCancellationRequested(); HasError = false; LatestFailure = null; await command(_shutdown.Token); }
            finally { _commands.Release(); }
        }
        catch (OperationCanceledException) { LastOutcome = S[TextId.Status_CancelledOperations]; }
        catch (ApplicationOperationException error) when (error.Failure == ApplicationFailure.Busy) { HasError = false; LatestFailure = null; LastOutcome = PresentationText.Failure(S, error.Failure); }
        catch (ApplicationOperationException error) { HasError = true; LatestFailure = error.Failure; LastOutcome = PresentationText.Failure(S, error.Failure); }
        catch (Exception) { HasError = true; LastOutcome = S[TextId.Status_OperationFailure]; }
        Notify();
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        if (FolderPolicy is WindowsSourceFolders windowsFolders) await windowsFolders.InitializeAsync(token);
        try
        {
            ApplyAgentState((await _agent.ConnectOrStartAsync(token)).State);
            _agentObserver = ObserveAgentAsync(_shutdown.Token);
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            token.ThrowIfCancellationRequested();
            var loaded = await SettingsStore.LoadAsync(token); Settings = loaded.Settings ?? AppSettings.Empty;
            ConfigurationNeedsReset = loaded.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable;
            AgentDisconnected(); _agentObserver = ObserveAgentAsync(_shutdown.Token);
        }
        LastOutcome = Settings.RepositoryPath.Length == 0 ? S[TextId.Status_ReadyToConfigure] : PresentationText.Readiness(S, Inspection.State);
    }
    public async Task SavePreferencesAsync(ApplicationPreferences preferences, CancellationToken token)
    {
        preferences.Validate();
        var languageChanged = preferences.Language != Preferences.Language;
        var selection = languageChanged ? MrtAppStrings.Instance.PrepareLanguage(preferences.Language) : default;
        await PreferencesStore.SaveAsync(preferences, token);
        Preferences = preferences; PreferencesNeedAttention = false;
        if (languageChanged) { MrtAppStrings.Instance.ApplyLanguage(selection); Localize.Refresh(); }
        LastOutcome = S[TextId.Status_PreferencesSaved]; Notify();
    }
    public async Task SaveAsync(AppSettings replacement, CancellationToken token, bool reset = false)
    {
        var previous = Settings;
        replacement = replacement.RepositoryPath.Length == 0 ? replacement : Prepare(replacement); replacement.Validate();
        if (await ValidateWorkspaceAsync(replacement, token) is { } problem) { ConfigurationProblem = LastOutcome = problem; Notify(); return; }
        if (ConfigurationNeedsReset && !reset) { LastOutcome = S[TextId.Status_ResetRequired]; return; }
        await RetireOwnersAsync();
        if (_agentState is null || !AgentAvailable) throw new ApplicationOperationException(ApplicationFailure.Unavailable);
        var reply = await _agent.SendAsync(new(AgentOperation.ApplySettings, replacement, reset,
            AgentId: _agentState.AgentId, ConfigurationRevision: _agentState.ConfigurationRevision), token);
        ApplyAgentState(reply.State); RequireSuccess(reply); _maintenance = null;
        LastOutcome = PresentationText.Saved(S, ConfigurationActivation.SavedNotice(previous, Settings)); _savedNotice = true;
        if (reply.Admission is { } admission) { LastOutcome = PresentationText.Admission(S, admission); _savedNotice = false; }
    }
    public async Task InspectAndComposeAsync(CancellationToken token)
    {
        var reply = await _agent.SendAsync(new(AgentOperation.Reinspect), token); ApplyAgentState(reply.State); RequireSuccess(reply);
    }
    private void ApplyAgentState(AgentState state)
    {
        if (_closing || _agentState?.AgentId == state.AgentId && state.Revision <= _revision) return;
        var previous = _agentState;
        // Keep the settings reference stable through status updates, preserving an unsaved UI draft.
        if (previous is null || previous.AgentId != state.AgentId || previous.ConfigurationRevision != state.ConfigurationRevision)
        {
            if (previous is null || System.Text.Json.JsonSerializer.Serialize(Settings) != System.Text.Json.JsonSerializer.Serialize(state.Settings)) Settings = state.Settings;
        }
        if (previous is not null && (previous.AgentId != state.AgentId || previous.TimelineRevision != state.TimelineRevision)) HistoryRevision++;
        _agentState = state; _revision = state.Revision; Status = state.Backup; Inspection = state.Inspection;
        AgentAvailable = !state.ShuttingDown; ConfigurationNeedsReset = state.ConfigurationNeedsReset;
        ConfigurationProblem = state.ShuttingDown ? S[TextId.Status_UpdateClosing] : PresentationText.ConfigurationIssue(S,
            state.ConfigurationIssue ?? (state.ConfigurationProblem.Length == 0 ? ConfigurationIssueCode.None : ConfigurationIssueCode.SavedConfigurationInvalid));
        Notify();
    }
    private void AgentDisconnected()
    {
        if (_closing) return;
        AgentAvailable = false; Status = null;
        ConfigurationProblem = S[TextId.Status_AgentUnavailableAdvice]; Notify();
    }
    private async Task ObserveAgentAsync(CancellationToken token)
    {
        var connected = AgentAvailable; var revision = _revision;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var expectedAgent = _agentState?.AgentId;
                var reply = connected ? await _agent.SendAsync(new(AgentOperation.WaitForState, SinceRevision: revision), token).ConfigureAwait(false) :
                    await _agent.ConnectOrStartAsync(token).ConfigureAwait(false);
                connected = true; revision = reply.State.Revision;
                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        if (_agentState is null || _agentState.AgentId == reply.State.AgentId || _agentState.AgentId == expectedAgent)
                            ApplyAgentState(reply.State);
                    }
                    finally { delivered.TrySetResult(); }
                })) return;
                await delivered.Task.WaitAsync(token).ConfigureAwait(false); // At most one queued snapshot; slow UI never accumulates an event backlog.
                if (reply.State.ShuttingDown) return; // Installer must not race an automatic UI restart.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception)
            {
                connected = false;
                dispatcher.TryEnqueue(AgentDisconnected);
                try { await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
    private static void RequireSuccess(AgentReply reply)
    {
        if (reply.Error == AgentError.None) return;
        throw new ApplicationOperationException(reply.Error == AgentError.Busy ? ApplicationFailure.Busy :
            reply.Error == AgentError.Conflict ? ApplicationFailure.RefreshRequired : ApplicationFailure.Unavailable);
    }
    public void BackUpNow() => _ = RunAsync(BackUpNowAsync);
    public async Task BackUpNowAsync(CancellationToken token)
    {
        HasError = false; LatestFailure = null; _savedNotice = false;
        var reply = await _agent.SendAsync(new(AgentOperation.RunNow), token); ApplyAgentState(reply.State);
        if (reply.Admission is { } admission) LastOutcome = PresentationText.Admission(S, admission);
        RequireSuccess(reply); Notify();
    }
    public void ClearSavedNotice() { if (_savedNotice) { _savedNotice = false; LastOutcome = S[TextId.Status_Ready]; } }
    private AppSettings Prepare(AppSettings settings) => settings with
    { Sources = FolderPolicy.Prepare(settings.RepositoryPath, settings.Sources, DefaultWorkspace) };
    public async Task<string?> ValidateWorkspaceAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.RepositoryPath.Length == 0) return null;
        try { await Task.Run(() => { token.ThrowIfCancellationRequested(); FolderPolicy.ValidateAliases(settings.RepositoryPath, settings.Sources, DefaultWorkspace); }, token); }
        catch (InvalidDataException error) { return PresentationText.Validation(S, error); }
        var problem = !settings.Sources.Any(s => s.RuntimePath is not null) ? null :
            await BackupWorkspace.ValidateAsync(settings.RepositoryPath, DefaultWorkspace, token);
        return problem is null ? null : S[TextId.Configuration_WorkspaceUnavailable];
    }
    public void TimelineChanged() { HistoryRevision++; ReleasePreview(); Notify(); }
    public void StopScheduling() => _ = RunAsync(token => SaveAsync(Settings with { SchedulingEnabled = false }, token));
    public void StartScheduling() => _ = RunAsync(token => SaveAsync(Settings with { SchedulingEnabled = true }, token));
    public async Task CancelBackupAsync()
    { var reply = await _agent.SendAsync(new(AgentOperation.CancelActive), _shutdown.Token); ApplyAgentState(reply.State); RequireSuccess(reply); }
    public void CancelHistory() => _query?.Cancel();
    public void ReleasePreview() { _previewEpoch++; _preview?.Dispose(); _preview = null; }
    public async Task WithBrowserAsync(Func<HistoryBrowserSession, CancellationToken, Task> action, CancellationToken token)
    {
        using var query = CancellationTokenSource.CreateLinkedTokenSource(token); _query = query;
        try
        {
            Browser ??= await HistoryBrowserSession.OpenAsync(Settings.RepositoryPath, query.Token);
            BrowserActivity = $"Captured {Browser.State.CaptureId}";
            await action(Browser, query.Token);
        }
        finally { if (ReferenceEquals(_query, query)) _query = null; }
    }
    public async Task PreviewAsync(BrowserVersion version, nint owner, CancellationToken token)
    {
        ReleasePreview(); var epoch = _previewEpoch;
        var lease = await new WindowsHistoryActions().PreviewAsync(Settings.RepositoryPath, version, owner, token);
        if (epoch != _previewEpoch || _closing) lease.Dispose(); else _preview = lease;
        LastOutcome = S[TextId.Status_PreviewOpened];
    }
    public Task PreviewAsync(BrowserVersion version, CancellationToken token) => PreviewAsync(version, OwnerWindow, token);
    public async Task RestoreSelectionAsync(BrowserSelection selection, string? destination, Guid root, bool original, CancellationToken token)
    {
        var source = original ? Settings.Sources.FirstOrDefault(s => s.SourceRootId == root && s.IsProtected)?.ToSelection() : null;
        if (original && source is null) throw new ApplicationOperationException(ApplicationFailure.InvalidSelection);
        var result = await new WindowsHistoryActions().RestoreAsync(Settings.RepositoryPath, selection, destination, OwnerWindow, source, token);
        HasError = result.Failed > 0 || result.Unconfirmed > 0;
        LastOutcome = S.Format(TextId.Status_RestoreResult, result.Copied, result.Skipped, result.Failed, result.Unconfirmed, result.MetadataWarnings) + (result.Cancelled ? S[TextId.Status_RestoreCancelled] : "");
    }
    public async Task RestoreExactVersionAsync(BrowserVersion version, string? destination, bool original, CancellationToken token)
    {
        var source = original ? Settings.Sources.FirstOrDefault(s => s.SourceRootId == version.SourceRootId && s.IsProtected)?.ToSelection() : null;
        if (original && source is null) throw new ApplicationOperationException(ApplicationFailure.InvalidSelection);
        var result = await new WindowsHistoryActions().RestoreExactAsync(Settings.RepositoryPath, version, destination, OwnerWindow, source, token);
        HasError = result.Failed > 0 || result.Unconfirmed > 0;
        LastOutcome = S.Format(TextId.Status_RestoreResult, result.Copied, result.Skipped, result.Failed, result.Unconfirmed, result.MetadataWarnings) + (result.Cancelled ? S[TextId.Status_RestoreCancelled] : "");
    }
    public async Task RecoverAsync(char[] password, CancellationToken token)
    {
        try
        {
            await RetireOwnersAsync();
            var result = await RecoverLocationAsync(Settings.RepositoryPath, password, token);
            HasError = result.Outcome is not (RecoveryOutcome.Success or RecoveryOutcome.Cancelled);
            LastOutcome = result.Outcome switch
            {
                RecoveryOutcome.Success => S[TextId.Recovery_Completed],
                RecoveryOutcome.WrongPassword => S[TextId.Recovery_WrongPassword],
                RecoveryOutcome.EnrollmentUncertain => result.LocalOpenVerified ? S[TextId.Recovery_UncertainOpen] : S[TextId.Recovery_UncertainAccess],
                RecoveryOutcome.RepositoryInvalid => S[TextId.Recovery_InvalidBackup],
                RecoveryOutcome.Cancelled => S[TextId.Recovery_Cancelled],
                _ => S[TextId.Recovery_Unavailable]
            };
        }
        finally { Array.Clear(password); }
    }
    public async Task<RecoveryResult> RecoverLocationAsync(string path, char[] password, CancellationToken token)
    {
        RecoveryResult result;
        try
        {
            using var lease = _operationGate.TryAcquire() ?? throw new ApplicationOperationException(ApplicationFailure.Busy);
            result = await new RepositorySetupService().RecoverAsync(path, password, token);
        }
        finally { Array.Clear(password); }
        if (result.LocalOpenVerified && SourcePaths.Same(path, Settings.RepositoryPath)) await InspectAndComposeAsync(token);
        return result;
    }
    private async Task RetireOwnersAsync()
    {
        ReleasePreview(); CancelHistory();
        if (Browser is { } browser) { Browser = null; await browser.DisposeAsync(); }
        BrowserActivity = "Closed";
    }
    public async Task<ExperimentalRecoveryState> InspectSecurityAsync(CancellationToken token)
        => Settings.RepositoryPath.Length == 0 ? ExperimentalRecoveryState.Off : (await CloudSecurity.InspectAsync(Settings.RepositoryPath, token)) switch
        {
            OneDriveRecoveryState.Enabled => ExperimentalRecoveryState.Available,
            OneDriveRecoveryState.LegacyUpgradeRequired => ExperimentalRecoveryState.Legacy,
            OneDriveRecoveryState.ConfigurationRequired => ExperimentalRecoveryState.ConfigurationRequired,
            OneDriveRecoveryState.Invalid => ExperimentalRecoveryState.Invalid,
            OneDriveRecoveryState.Unavailable => ExperimentalRecoveryState.Unavailable,
            _ => ExperimentalRecoveryState.Off
        };
    public async Task<SecurityOperationResult> ExecuteSecurityAsync(RepositorySecurityOperation operation, char[]? password, CancellationToken token)
    {
        try
        {
            await RetireOwnersAsync();
            var result = operation switch
            {
                RepositorySecurityOperation.ChangePassword => await Security.ChangePasswordAsync(Settings.RepositoryPath, password ?? throw new ArgumentNullException(nameof(password)), token),
                RepositorySecurityOperation.EnableRoaming => CloudResult(await CloudSecurity.EnableAsync(Settings.RepositoryPath, token: token)),
                RepositorySecurityOperation.RepairRoaming => CloudResult(await CloudSecurity.EnableAsync(Settings.RepositoryPath, repair: true, token)),
                RepositorySecurityOperation.DisableRoaming => CloudResult(await CloudSecurity.DisableAsync(Settings.RepositoryPath, token: token)),
                RepositorySecurityOperation.DisableRoamingLocally => CloudResult(await CloudSecurity.DisableAsync(Settings.RepositoryPath, localOnly: true, token)),
                RepositorySecurityOperation.RecoverRoaming => CloudResult(await CloudSecurity.VerifyAsync(Settings.RepositoryPath, enroll: true, token)),
                RepositorySecurityOperation.VerifyRoaming => CloudResult(await CloudSecurity.VerifyAsync(Settings.RepositoryPath, token: token)),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            // Physical key wrappers change no logical Timeline revision; readers were retired above.
            await InspectAndComposeAsync(token);
            return result;
        }
        finally { if (password is not null) Array.Clear(password); }
    }
    public void PauseEncryption() => _encryptionCancellation?.Cancel();
    private static SecurityOperationResult CloudResult(OneDriveRecoveryResult result) => new(result.Outcome switch
    {
        OneDriveRecoveryOutcome.Verified or OneDriveRecoveryOutcome.Disabled or OneDriveRecoveryOutcome.DisabledLocally => SecurityOperationOutcome.Success,
        OneDriveRecoveryOutcome.Uncertain => SecurityOperationOutcome.Uncertain,
        OneDriveRecoveryOutcome.Cancelled => SecurityOperationOutcome.Cancelled,
        _ => result.Failure == CloudRecoveryFailure.ObjectNotFound ? SecurityOperationOutcome.NotAvailable : SecurityOperationOutcome.Unavailable
    }, result.Failure, result.Cleanup, result.CleanupFileName);
    public async Task<EncryptionChangeResult> ChangeEncryptionAsync(bool encrypted, char[]? password, CancellationToken token)
    {
        var operationPath = Settings.RepositoryPath;
        EncryptionChangeResult? result = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var epoch = ++_encryptionEpoch;
        _encryptionCancellation = cancellation; EncryptionBusy = true; EncryptionProgress = null; Notify();
        try
        {
            await RetireOwnersAsync();
            var progress = new Progress<EncryptionChangeProgress>(value =>
            { if (EncryptionBusy && epoch == _encryptionEpoch) { EncryptionProgress = value; Notify(); } });
            result = await new RepositoryEncryptionService(_operationGate, _legacyRecoveryCleanup, DefaultWorkspace, CloudSecurity)
                .ChangeAsync(operationPath, encrypted, password, progress, cancellation.Token);
            return result;
        }
        finally
        {
            if (password is not null) Array.Clear(password);
            _encryptionEpoch++; _encryptionCancellation = null; EncryptionBusy = false; EncryptionProgress = null;
            await InspectAndComposeAsync(CancellationToken.None);
            // Clean adoption can encounter a paused conversion before source configurations are readable.
            // Recover their original IDs as inactive mappings once maintenance finishes; never invent roots.
            if (result?.Outcome == EncryptionChangeOutcome.Completed && Settings.Sources.Length == 0 &&
                SourcePaths.Same(operationPath, Settings.RepositoryPath))
            {
                var adopted = await new RepositorySetupService().InspectAsync(operationPath);
                if (adopted.State == RepositoryReadiness.Ready && !adopted.Sources.IsDefaultOrEmpty)
                    await SaveAsync(Settings with { Sources = adopted.Sources.Select(source => SourceMapping.FromConfiguration(source) with { IsProtected = false }).ToImmutableArray() }, CancellationToken.None);
            }
            Notify();
        }
    }
    public void Notify() => Changed?.Invoke();
    public async ValueTask DisposeAsync()
    {
        Task[] commands;
        lock (_activeLock) { _closing = true; commands = _active.ToArray(); }
        await _shutdown.CancelAsync(); CancelHistory();
        if (_agentObserver is not null) await _agentObserver;
        await Task.WhenAll(commands);
        await RetireOwnersAsync(); Changed = null; _shutdown.Dispose();
    }
}
