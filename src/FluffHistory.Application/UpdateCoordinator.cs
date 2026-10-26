namespace FluffHistory.Application;

public enum UpdatePhase { Idle, Checking, Available, Downloading, Verifying, Ready, WaitingForIdle, HandingOff, Failed }
public sealed record UpdateSnapshot(string? CurrentReleaseVersion, UpdatePreferences Preferences, DateTimeOffset? LastCheckedUtc,
    UpdatePhase Phase, UpdateRelease? Available, UpdateFailure Failure = UpdateFailure.None, long DownloadedBytes = 0, UpdateInstallResult? LastInstall = null)
{
    public void Validate()
    {
        (Preferences ?? throw new InvalidDataException("Missing update preferences.")).Validate();
        if (CurrentReleaseVersion is not null && (!ReleaseVersion.TryParse(CurrentReleaseVersion, out var version) || version.ToString() != CurrentReleaseVersion) ||
            !Enum.IsDefined(Phase) || !Enum.IsDefined(Failure) || LastCheckedUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            DownloadedBytes < 0 || DownloadedBytes > UpdateRelease.MaximumInstallerBytes || Available is { } a && DownloadedBytes > a.Size)
            throw new InvalidDataException("Invalid update snapshot.");
        Available?.Validate();
        LastInstall?.Validate();
    }
}
public sealed record StagedUpdate(UpdateRelease Release, string Path);
public sealed class UpdateException(UpdateFailure failure) : Exception("Update operation failed.")
{ public UpdateFailure Failure { get; } = failure; }
public interface IUpdateInstaller
{
    Task<StagedUpdate> StageAsync(UpdateRelease release, IProgress<long> progress, CancellationToken token);
    /// <summary>Must reverify, retain mutation admission through handoff and never force terminate.</summary>
    Task<UpdateFailure> HandoffAsync(StagedUpdate update, bool manual, bool cancelBackup, CancellationToken token);
}
public readonly record struct UpdateNetworkPolicy(bool CheckAllowed, bool DownloadAllowed);

/// <summary>Agent-owned update scheduler. No repository/configuration authority. All work is serialized and drained.</summary>
public sealed class UpdateCoordinator : IAsyncDisposable
{
    private readonly IUpdateReleaseSource _source;
    private readonly IUpdateInstaller _installer;
    private readonly UpdateJsonStore<UpdatePreferences> _preferencesStore;
    private readonly UpdateJsonStore<UpdateDiskState> _stateStore;
    private readonly UpdateJsonStore<UpdateInstallResult?>? _resultStore;
    private readonly Func<UpdateNetworkPolicy> _network;
    private readonly TimeProvider _clock;
    private readonly ReleaseVersion? _current;
    private readonly SemaphoreSlim _work = new(1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _automatic;
    private Task? _loop, _requested;
    private StagedUpdate? _staged;
    private readonly TaskCompletionSource _handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task HandoffReady => _handoff.Task;
    private UpdateDiskState _disk = UpdateDiskState.Empty;
    private UpdateSnapshot _snapshot;
    private bool _loaded;
    public UpdateCoordinator(IUpdateReleaseSource source, IUpdateInstaller installer,
        UpdateJsonStore<UpdatePreferences> preferencesStore, UpdateJsonStore<UpdateDiskState> stateStore,
        ReleaseVersion? current, Func<UpdateNetworkPolicy>? network = null, TimeProvider? clock = null, UpdateJsonStore<UpdateInstallResult?>? resultStore = null)
    {
        _source = source; _installer = installer; _preferencesStore = preferencesStore; _stateStore = stateStore;
        _network = network ?? (() => new(true, true)); _clock = clock ?? TimeProvider.System; _current = current;
        _resultStore = resultStore;
        _snapshot = new(current?.ToString(), UpdatePreferences.Default, null, UpdatePhase.Idle, null);
    }
    public UpdateSnapshot State { get { lock (_sync) return _snapshot; } }
    public event Action<UpdateSnapshot>? Changed;
    private void Publish(Func<UpdateSnapshot, UpdateSnapshot> update)
    {
        UpdateSnapshot value; lock (_sync) { value = _snapshot = update(_snapshot); }
        Changed?.Invoke(value);
    }
    public void Start()
    {
        lock (_sync) _loop ??= Task.Run(async () =>
        {
            while (!_stopping.IsCancellationRequested)
            {
                try { await TickAsync(_stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Update scheduler failed ({0}).", error.GetType().Name); }
                try { await Task.Delay(TimeSpan.FromMinutes(1), _clock, _stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
            }
        });
    }
    public bool RequestCheck() => Request(token => CheckNowAsync(token));
    public bool RequestInstall(bool cancelBackup) => Request(token => InstallNowAsync(cancelBackup, token));
    private bool Request(Func<CancellationToken, Task> operation)
    {
        lock (_sync)
        {
            if (_stopping.IsCancellationRequested || _requested is { IsCompleted: false }) return false;
            _requested = Task.Run(async () =>
            {
                try { await operation(_stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                catch (Exception error)
                { System.Diagnostics.Trace.TraceWarning("Update command failed ({0}).", error.GetType().Name); Publish(s => s with { Phase = UpdatePhase.Failed, Failure = UpdateFailure.Unavailable }); }
            });
            return true;
        }
    }
    private async Task LoadAsync(CancellationToken token)
    {
        if (_loaded) return;
        var preferences = await _preferencesStore.LoadAsync(token).ConfigureAwait(false);
        _disk = (await _stateStore.LoadAsync(token).ConfigureAwait(false)).Value;
        // A corrupt desired policy is not consent to automatic network traffic.
        var desired = preferences.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable ?
            UpdatePreferences.Default with { Frequency = UpdateFrequency.Never } : preferences.Value;
        UpdateRelease? selected = null;
        try { selected = UpdateReleaseSelection.Select(_disk.Releases, desired.Channel, _current); }
        catch (InvalidDataException) { _disk = UpdateDiskState.Empty; }
        // Older state may have armed an announcement-only release. Never retry it on idle ticks.
        if (_disk.AutomaticPending && selected is not { CanDownload: true })
            await StopAutomaticRetryAsync(token).ConfigureAwait(false);
        Publish(s => s with { Preferences = desired, Available = selected, LastCheckedUtc = _disk.LastSuccessUtc,
            Phase = selected is null ? UpdatePhase.Idle : UpdatePhase.Available });
        await RefreshResultAsync(token).ConfigureAwait(false);
        _loaded = true;
    }
    private async Task RefreshResultAsync(CancellationToken token)
    {
        if (_resultStore is null) return;
        var result = (await _resultStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (result is null || result == State.LastInstall) return;
        // Setup starts the replacement Agent before the detached old helper records its exit.
        // The bounded local result is observed independently of all network preferences.
        var failure = result.ReleaseVersion == _current?.ToString() ?
            result.RelaunchFailed ? UpdateFailure.Relaunch : UpdateFailure.None :
            result.Installed ? UpdateFailure.None : result.Failure == UpdateFailure.None ? UpdateFailure.Installation : result.Failure;
        Publish(s => s with { LastInstall = result, Failure = failure });
    }
    public async Task ReloadPreferencesAsync(CancellationToken token = default)
    {
        lock (_sync) _automatic?.Cancel();
        await _work.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            var result = await _preferencesStore.LoadAsync(token).ConfigureAwait(false);
            var desired = result.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable ?
                UpdatePreferences.Default with { Frequency = UpdateFrequency.Never } : result.Value;
            var selected = UpdateReleaseSelection.Select(_disk.Releases, desired.Channel, _current);
            Publish(s => s with { Preferences = desired, Available = selected, Phase = selected is null ? UpdatePhase.Idle : UpdatePhase.Available,
                Failure = UpdateFailure.None, DownloadedBytes = 0 });
        }
        finally { _work.Release(); }
    }
    public async Task TickAsync(CancellationToken token = default)
    {
        if (!await _work.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            await RefreshResultAsync(token).ConfigureAwait(false);
            if (State.Phase == UpdatePhase.HandingOff || State.Preferences.Frequency == UpdateFrequency.Never) return;
            using var automatic = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
            lock (_sync) _automatic = automatic;
            try
            {
                var now = _clock.GetUtcNow(); var normalized = UpdateSchedule.ReanchorFuture(_disk, now);
                if (normalized != _disk) { _disk = normalized; await _stateStore.SaveAsync(_disk, automatic.Token).ConfigureAwait(false); }
                var network = _network();
                if (network.CheckAllowed && UpdateSchedule.IsDue(State.Preferences, _disk, now)) await CheckAsync(true, automatic.Token).ConfigureAwait(false);
                if (_disk.AutomaticPending && State.Available is not null && network.DownloadAllowed && _current is not null)
                    await InstallAsync(false, false, automatic.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_stopping.IsCancellationRequested) { }
            finally { lock (_sync) _automatic = null; }
        }
        finally { _work.Release(); }
    }
    public async Task CheckNowAsync(CancellationToken token = default)
    {
        await _work.WaitAsync(token).ConfigureAwait(false);
        try { await LoadAsync(token).ConfigureAwait(false); await CheckAsync(false, token).ConfigureAwait(false); }
        finally { _work.Release(); }
    }
    private async Task CheckAsync(bool automatic, CancellationToken token)
    {
        Publish(s => s with { Phase = UpdatePhase.Checking, Failure = UpdateFailure.None });
        var now = _clock.GetUtcNow();
        _disk = _disk with { LastAttemptUtc = now };
        await _stateStore.SaveAsync(_disk, token).ConfigureAwait(false);
        var response = await _source.CheckAsync(_disk.ETag, _disk.Releases, token).ConfigureAwait(false);
        if (response.Failure != UpdateFailure.None)
        { Publish(s => s with { Phase = UpdatePhase.Failed, Failure = response.Failure }); return; }
        try
        {
            var selected = UpdateReleaseSelection.Select(response.Releases, State.Preferences.Channel, _current);
            _disk = _disk with { LastSuccessUtc = now, ETag = response.ETag, Releases = response.Releases, AutomaticPending = automatic && selected is { CanDownload: true } };
            await _stateStore.SaveAsync(_disk, token).ConfigureAwait(false);
            Publish(s => s with { LastCheckedUtc = now, Available = selected, Phase = selected is null ? UpdatePhase.Idle : UpdatePhase.Available,
                Failure = response.Releases.Length == 0 ? UpdateFailure.NoValidRelease : UpdateFailure.None, DownloadedBytes = 0 });
        }
        catch (InvalidDataException) { Publish(s => s with { Phase = UpdatePhase.Failed, Failure = UpdateFailure.InvalidResponse }); }
    }
    public async Task InstallNowAsync(bool cancelBackup, CancellationToken token = default)
    {
        await _work.WaitAsync(token).ConfigureAwait(false);
        try { await LoadAsync(token).ConfigureAwait(false); await InstallAsync(true, cancelBackup, token).ConfigureAwait(false); }
        finally { _work.Release(); }
    }
    private async Task InstallAsync(bool manual, bool cancelBackup, CancellationToken token)
    {
        var release = State.Available;
        if (_current is null) { Publish(s => s with { Failure = UpdateFailure.DevelopmentBuild }); return; }
        if (release is null || release.Version <= _current.Value) return;
        if (!release.CanDownload) { Publish(s => s with { Phase = UpdatePhase.Failed, Failure = UpdateFailure.Integrity }); return; }
        try
        {
            Publish(s => s with { Phase = UpdatePhase.Downloading, Failure = UpdateFailure.None });
            // Synchronous callback avoids queued progress racing final Ready/HandingOff transitions.
            if (_staged?.Release != release)
                _staged = await _installer.StageAsync(release, new InlineProgress(bytes =>
                    Publish(s => s with { DownloadedBytes = bytes, Phase = bytes == release.Size ? UpdatePhase.Verifying : UpdatePhase.Downloading })), token).ConfigureAwait(false);
            Publish(s => s with { Phase = UpdatePhase.Ready });
            // HandoffAsync can commit the detached helper before returning. Consume durably FIRST,
            // so a rollback/restarted old Agent cannot inherit an automatic retry. A failed save
            // must not launch a helper. Only explicit admission deferral restores the pending flag.
            var retryWhenIdle = _disk.AutomaticPending;
            if (retryWhenIdle) await StopAutomaticRetryAsync(token).ConfigureAwait(false);
            var result = await _installer.HandoffAsync(_staged, manual, manual && cancelBackup, token).ConfigureAwait(false);
            if (retryWhenIdle && (result is UpdateFailure.Busy or UpdateFailure.BackupStillRunning))
            {
                _disk = _disk with { AutomaticPending = true };
                await _stateStore.SaveAsync(_disk, CancellationToken.None).ConfigureAwait(false);
            }
            Publish(s => s with { Phase = result == UpdateFailure.None ? UpdatePhase.HandingOff :
                result is UpdateFailure.Busy or UpdateFailure.BackupStillRunning ? UpdatePhase.WaitingForIdle : UpdatePhase.Failed, Failure = result });
            if (result == UpdateFailure.None) _handoff.TrySetResult();
            else if (result is not (UpdateFailure.Busy or UpdateFailure.BackupStillRunning)) await StopAutomaticRetryAsync(token).ConfigureAwait(false);
        }
        catch (UpdateException error) { Publish(s => s with { Phase = UpdatePhase.Failed, Failure = error.Failure }); await StopAutomaticRetryAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException)
        { Publish(s => s with { Phase = UpdatePhase.Failed, Failure = UpdateFailure.Download }); await StopAutomaticRetryAsync(token).ConfigureAwait(false); }
    }
    private async Task StopAutomaticRetryAsync(CancellationToken token)
    { _disk = _disk with { AutomaticPending = false }; await _stateStore.SaveAsync(_disk, token).ConfigureAwait(false); }
    private sealed class InlineProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task? loop, requested; lock (_sync) { _automatic?.Cancel(); loop = _loop; requested = _requested; }
        if (loop is not null) await loop.ConfigureAwait(false);
        if (requested is not null) await requested.ConfigureAwait(false);
        await _work.WaitAsync().ConfigureAwait(false); _work.Release();
        _stopping.Dispose();
    }
}
