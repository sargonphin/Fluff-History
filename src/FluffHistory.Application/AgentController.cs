namespace FluffHistory.Application;

/// <summary>The one long-lived scheduling/configuration owner. One optional replaceable display
/// status survives restart; no run history, persisted progress or retained keys.</summary>
public sealed class AgentController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1);
    private readonly AppSettingsStore _store;
    private readonly ISourceFolderPolicy _policy;
    private readonly Func<BackupConfiguration, BackupSchedule, BackupCoordinator> _factory;
    private readonly string _workspace;
    private AgentState _state;
    private BackupCoordinator? _coordinator;
    private Action<BackupStatus>? _handler;
    private ConfigurationActivation _activation = new(AppSettings.Empty);
    private AttemptSnapshot? _observed;
    private long _coordinatorRevision = -1;
    private TaskCompletionSource _changed = NewSignal();
    private Task? _shutdown;
    private UpdateCoordinator? _updates;
    public void AttachUpdates(UpdateCoordinator updates)
    {
        lock (_sync)
        {
            if (_updates is not null) throw new InvalidOperationException("Updates already attached.");
            _updates = updates;
            updates.Changed += state => Publish(s => s with { Updates = state });
            Publish(s => s with { Updates = updates.State });
        }
    }
    public Task ReloadUpdatePreferencesAsync(CancellationToken token) => _updates?.ReloadPreferencesAsync(token) ?? Task.CompletedTask;
    public bool RequestUpdateCheck() => !State.ShuttingDown && _updates?.RequestCheck() == true;
    public bool RequestUpdateInstall(bool cancelBackup) => !State.ShuttingDown && _updates?.RequestInstall(cancelBackup) == true;
    private readonly LastBackupStatusStore? _statusStore;
    private LastBackupStatus? _lastKnownStatus, _pendingStatus;
    private Task? _statusWrite;
    internal Func<string, CancellationToken, Task<RepositoryInspection>> Inspect { get; init; } =
        (path, token) => new RepositorySetupService().InspectAsync(path, token);
    internal Func<string, string, CancellationToken, Task<string?>> ValidateWorkspace { get; init; } = BackupWorkspace.ValidateAsync;
    public AgentController(AppSettingsStore store, ISourceFolderPolicy policy,
        Func<BackupConfiguration, BackupSchedule, BackupCoordinator> factory, string? workspace = null,
        string productVersion = "development", LastBackupStatusStore? lastStatusStore = null)
    {
        _store = store; _policy = policy; _factory = factory; _workspace = workspace ?? BackupWorkspace.DefaultRoot;
        _statusStore = lastStatusStore;
        _state = new(Guid.NewGuid(), Environment.ProcessId, DateTimeOffset.UtcNow, productVersion, 0, 0, 0,
            AppSettings.Empty, new(RepositoryReadiness.Missing, Sources: []), false, "Checking backup configuration…", null, false,
            ConfigurationIssue: ConfigurationIssueCode.Checking);
    }
    public AgentState State { get { lock (_sync) return _state; } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Publish(Func<AgentState, AgentState> update)
    {
        lock (_sync)
        {
            _state = update(_state) with { Revision = _state.Revision + 1 };
            _state = _state with { LastBackupStatus = _lastKnownStatus is { } saved &&
                saved.Matches(_state.Settings.RepositoryPath, _state.Inspection.RepositoryId) ? saved : null };
            var signal = _changed; _changed = NewSignal(); signal.TrySetResult();
        }
    }
    public async Task<AgentState> WaitForStateAsync(long revision, CancellationToken token)
    {
        Task signal;
        lock (_sync) { if (_state.Revision > revision || _state.ShuttingDown) return _state; signal = _changed.Task; }
        await signal.WaitAsync(token).ConfigureAwait(false); return State;
    }
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(token).ConfigureAwait(false);
            var savedStatus = _statusStore is null ? null : await _statusStore.LoadAsync(token).ConfigureAwait(false);
            lock (_sync) _lastKnownStatus = savedStatus;
            var settings = loaded.Settings ?? AppSettings.Empty;
            _activation = new(settings);
            // Replace the provisional startup configuration as a new settings capture. A UI
            // connected before loading must refresh, and its earlier save capture must be stale.
            Publish(s => s with { Settings = settings, ConfigurationRevision = s.ConfigurationRevision + 1, ConfigurationNeedsReset = loaded.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable,
                ConfigurationProblem = loaded.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable ? "Application settings need attention. Reset configuration explicitly; backup data is untouched." : "",
                ConfigurationIssue = loaded.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable ? ConfigurationIssueCode.SettingsNeedReset : ConfigurationIssueCode.None });
            if (!State.ConfigurationNeedsReset) await ComposeAsync(settings, token).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }
    public BackupAdmission RunNow()
    {
        lock (_sync) return _state.ShuttingDown || _state.Inspection.State == RepositoryReadiness.EncryptionMigrationRequired ? BackupAdmission.NotReady : _coordinator?.RequestRunNow() ?? BackupAdmission.NotReady;
    }
    public Task CancelActiveAsync()
    {
        lock (_sync) return _coordinator?.CancelActiveAsync() ?? Task.CompletedTask;
    }
    public async Task<AgentReply> ApplySettingsAsync(AgentRequest request, CancellationToken token)
    {
        AgentProtocol.Validate(request);
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = State;
            if (previous.ShuttingDown) return new(AgentError.ShuttingDown, previous);
            if (previous.Inspection.State == RepositoryReadiness.EncryptionMigrationRequired) return new(AgentError.Conflict, previous);
            if (previous.Settings.RepositoryPath.Length != 0 && (await Inspect(previous.Settings.RepositoryPath, token).ConfigureAwait(false)) is
                { State: RepositoryReadiness.EncryptionMigrationRequired } migration)
            { Publish(s => s with { Inspection = migration }); return new(AgentError.Conflict, State); }
            if (request.AgentId != previous.AgentId || request.ConfigurationRevision != previous.ConfigurationRevision) return new(AgentError.Conflict, previous);
            if (previous.ConfigurationNeedsReset && !request.Reset) return new(AgentError.Conflict, previous);
            var replacement = await Task.Run(() => Prepare(request.Settings!), token).ConfigureAwait(false);
            await RetireAsync().ConfigureAwait(false);
            try { await _store.SaveAsync(replacement, token).ConfigureAwait(false); }
            catch
            {
                // A cancelled/failed save leaves the existing canonical configuration authoritative.
                await ComposeAsync(previous.Settings, CancellationToken.None).ConfigureAwait(false); throw;
            }
            Publish(s => s with { Settings = replacement, ConfigurationRevision = s.ConfigurationRevision + 1, ConfigurationNeedsReset = false, ConfigurationProblem = "", ConfigurationIssue = ConfigurationIssueCode.None });
            // Once saved, finish the authoritative transition even if its client disconnects.
            await ComposeAsync(replacement, CancellationToken.None).ConfigureAwait(false);
            if (request.Reset) _activation.Reset();
            var admission = _activation.AfterSaved(replacement, State.Inspection.State == RepositoryReadiness.Ready && _coordinator is not null, RunNow);
            return new(AgentError.None, State, admission);
        }
        finally { _commands.Release(); }
    }
    public async Task ReinspectAsync(CancellationToken token = default)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (State.ShuttingDown || State.ConfigurationNeedsReset) return;
            if (State.Backup?.IsAttemptActive == true) throw new ApplicationOperationException(ApplicationFailure.Busy);
            await RetireAsync().ConfigureAwait(false);
            await ComposeAsync(State.Settings, token).ConfigureAwait(false);
            // Recovery/restart is not first-configuration activation and never retries an attempt.
        }
        finally { _commands.Release(); }
    }
    private AppSettings Prepare(AppSettings settings)
    {
        var prepared = settings.RepositoryPath.Length == 0 ? settings : settings with { Sources = _policy.Prepare(settings.RepositoryPath, settings.Sources, _workspace) };
        prepared.Validate();
        if (prepared.RepositoryPath.Length != 0) _policy.ValidateAliases(prepared.RepositoryPath, prepared.Sources, _workspace);
        return prepared;
    }
    private async Task ComposeAsync(AppSettings desired, CancellationToken token)
    {
        try
        {
            if (State.ShuttingDown) return;
            var settings = await Task.Run(() => Prepare(desired), token).ConfigureAwait(false);
            if (settings.RepositoryPath.Length == 0)
            { Publish(s => s with { Inspection = new(RepositoryReadiness.Missing, Sources: []), ConfigurationProblem = "", ConfigurationIssue = ConfigurationIssueCode.None, Backup = null }); return; }
            var inspection = await Inspect(settings.RepositoryPath, token).ConfigureAwait(false);
            // Source roots are already represented by canonical settings; do not duplicate an unbounded repository projection in status.
            inspection = inspection with { Sources = [] };
            Publish(s => s with { Inspection = inspection, ConfigurationProblem = "", ConfigurationIssue = ConfigurationIssueCode.None, Backup = null });
            if (inspection.State is RepositoryReadiness.Invalid or RepositoryReadiness.EncryptionMigrationRequired || !settings.Sources.Any(s => s.RuntimePath is not null)) return;
            // Valid desired configuration retains future cadence across unavailable/removable/SMB destinations.
            // Admission attempts, not a background reachability poll, determine when the repository is usable again.
            var problem = inspection.State == RepositoryReadiness.Ready ? await ValidateWorkspace(settings.RepositoryPath, _workspace, token).ConfigureAwait(false) : null;
            if (problem is not null) { Publish(s => s with { ConfigurationProblem = problem, ConfigurationIssue = ConfigurationIssueCode.WorkspaceUnavailable }); return; }
            var configuration = await Task.Run(() => settings.ToBackupConfiguration(_policy, _workspace), token).ConfigureAwait(false);
            var coordinator = _factory(configuration, settings.ToSchedule());
            lock (_sync)
            {
                _coordinator = coordinator; _coordinatorRevision = -1; _observed = null;
                _handler = status => Observe(coordinator, status); coordinator.StatusChanged += _handler;
            }
            if (settings.SchedulingEnabled) coordinator.Start();
            Observe(coordinator, coordinator.Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        { Publish(s => s with { ConfigurationProblem = "The saved backup configuration is unavailable or invalid. Check Configuration before continuing.",
            ConfigurationIssue = ConfigurationIssues.Code(error), Backup = null }); }
    }
    private void Observe(BackupCoordinator coordinator, BackupStatus status)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(coordinator, _coordinator) || status.Revision <= _coordinatorRevision) return;
            _coordinatorRevision = status.Revision;
            var changed = status.LastAttempt is { } attempt && !ReferenceEquals(attempt, _observed) && attempt.Result.TimelineChanged;
            var newAttempt = status.LastAttempt is not null && !ReferenceEquals(status.LastAttempt, _observed);
            _observed = status.LastAttempt;
            var inspection = _state.Inspection;
            if (newAttempt)
            {
                var result = status.LastAttempt!.Result;
                if (result.OpenedRepository is { } opened) inspection = opened;
                if (result.Outcome == BackupOutcome.RepositoryUnavailable) inspection = inspection with { State = RepositoryReadiness.Unavailable };
                if (result.Outcome == BackupOutcome.RecoveryRequired) inspection = inspection with { State = RepositoryReadiness.RecoveryRequired, Encrypted = true };
                RecordStatus(status.LastAttempt!, inspection);
            }
            Publish(s => s with { Backup = status, TimelineRevision = s.TimelineRevision + (changed ? 1 : 0),
                Inspection = inspection });
        }
    }
    // Caller holds _sync. Disk I/O runs separately with one write plus one coalesced pending value.
    private void RecordStatus(AttemptSnapshot attempt, RepositoryInspection inspection)
    {
        if (attempt.Result.Outcome == BackupOutcome.EncryptionMigrationRequired) return;
        if (_statusStore is null || inspection.RepositoryId is not { } id || id == Guid.Empty || _state.Settings.RepositoryPath.Length == 0) return;
        try { _lastKnownStatus = LastBackupStatus.FromAttempt(_state.Settings.RepositoryPath, id, attempt); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { System.Diagnostics.Trace.TraceWarning("Last backup display status invalid ({0}).", error.GetType().Name); return; }
        _pendingStatus = _lastKnownStatus;
        _statusWrite ??= Task.Run(WriteStatusAsync);
    }
    private async Task WriteStatusAsync()
    {
        while (true)
        {
            LastBackupStatus value;
            lock (_sync)
            {
                if (_pendingStatus is null) { _statusWrite = null; return; }
                value = _pendingStatus; _pendingStatus = null;
            }
            try { await _statusStore!.SaveAsync(value).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Last backup display status unavailable ({0}).", error.GetType().Name); }
        }
    }
    private async Task RetireAsync()
    {
        BackupCoordinator? coordinator; Action<BackupStatus>? handler;
        lock (_sync) { coordinator = _coordinator; handler = _handler; _coordinator = null; _handler = null; }
        if (coordinator is not null)
        {
            coordinator.StopScheduling(); if (handler is not null) coordinator.StatusChanged -= handler;
            await coordinator.DisposeAsync().ConfigureAwait(false);
            lock (_sync)
            {
                var last = coordinator.Status.LastAttempt;
                if (last is not null && !ReferenceEquals(last, _observed))
                { _observed = last; RecordStatus(last, last.Result.OpenedRepository ?? _state.Inspection); }
            }
        }
        Publish(s => s with { Backup = null });
    }
    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            if (_shutdown is not null) return _shutdown;
            Publish(s => s with { ShuttingDown = true });
            return _shutdown = Task.Run(async () =>
            {
                await _commands.WaitAsync().ConfigureAwait(false);
                try
                {
                    await RetireAsync().ConfigureAwait(false);
                    Task? pending; lock (_sync) pending = _statusWrite;
                    if (pending is not null) await pending.ConfigureAwait(false);
                }
                finally { _commands.Release(); }
            });
        }
    }
    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
