using FluffHistory.Core;

namespace FluffHistory.Desktop.Presentation.Models;

/// <summary>Visible-page observations coalesced across entry/completion/location changes.
/// Only the cheap drive query repeats while active. No I/O in paint/layout getters.</summary>
public sealed class StorageUsageViewModel(IDesktopContext owner, bool includeBackupSize, TimeProvider? timeProvider = null) : ObservableObject
{
    private IAppStrings S => owner.Strings;
    private string _language = owner.Strings.Language;
    private bool _active, _checkingDrive, _checkingBackup, _pending, _fullPending;
    private bool _backupActive = owner.Status?.IsAttemptActive == true;
    private string _path = "";
    private long _generation, _historyRevision = owner.HistoryRevision;
    private (DateTimeOffset?, DateTimeOffset?) _lastAttempt = (owner.Status?.LastAttempt?.StartedUtc, owner.Status?.LastAttempt?.FinishedUtc);
    private CancellationTokenSource? _query, _poll;
    private Task? _refresh;
    private CapacityObservation? _drive;
    private long? _backupBytes;
    public bool HasLocation => _path.Length != 0;
    public bool HasDriveSpace => _drive is not null;
    public double UsedPercent => _drive is { } drive ? 100d * (drive.Capacity - drive.Free) / drive.Capacity : 0;
    public string DriveSpace => _drive is { } drive ?
        S.Format(TextId.Storage_DriveUsage, PresentationText.Size(S, drive.Capacity - drive.Free), PresentationText.Size(S, drive.Capacity), PresentationText.Size(S, drive.Free)) :
        _checkingDrive ? S[TextId.Storage_CheckingDrive] : S[TextId.Storage_DriveUnavailable];
    public string BackupSize => _checkingBackup ? S[TextId.Storage_CheckingBackup] : _backupBytes is { } bytes ?
        _backupActive ? S.Format(TextId.Storage_LastBackupSize, PresentationText.Size(S, bytes)) : S.Format(TextId.Storage_BackupSize, PresentationText.Size(S, bytes)) : S[TextId.Storage_BackupSizeUnavailable];

    public Task ActivateAsync(string path)
    {
        if (_active) return SetPathAsync(path);
        _backupActive = owner.Status?.IsAttemptActive == true;
        _lastAttempt = (owner.Status?.LastAttempt?.StartedUtc, owner.Status?.LastAttempt?.FinishedUtc);
        _historyRevision = owner.HistoryRevision;
        _active = true;
        if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) ResetPath(path);
        UpdatePolling(); return RequestRefreshAsync(true);
    }
    public Task SetPathAsync(string path)
    {
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        ResetPath(path); UpdatePolling();
        return _active ? RequestRefreshAsync(true) : Task.CompletedTask;
    }
    private void ResetPath(string path)
    {
        _path = path; _generation++; _query?.Cancel();
        UpdateDrive(null, false); _backupBytes = null; _checkingBackup = false;
        Changed(nameof(HasLocation)); Changed(nameof(BackupSize));
    }
    private void UpdateDrive(CapacityObservation? drive, bool checking)
    {
        var hadSpace = HasDriveSpace; var percent = UsedPercent; var text = DriveSpace;
        _drive = drive; _checkingDrive = checking;
        if (hadSpace != HasDriveSpace) Changed(nameof(HasDriveSpace));
        if (percent != UsedPercent) Changed(nameof(UsedPercent));
        if (text != DriveSpace) Changed(nameof(DriveSpace));
    }
    public void ObserveState()
    {
        if (_language != S.Language)
        { _language = S.Language; Changed(nameof(DriveSpace)); Changed(nameof(BackupSize)); }
        var attempt = (owner.Status?.LastAttempt?.StartedUtc, owner.Status?.LastAttempt?.FinishedUtc);
        var completed = attempt.Item2 is not null && (attempt != _lastAttempt ||
            _backupActive && owner.Status is { IsAttemptActive: false, LastSafeguard.Allowed: true });
        var changed = _historyRevision != owner.HistoryRevision;
        _lastAttempt = attempt; _historyRevision = owner.HistoryRevision;
        var wasActive = _backupActive; _backupActive = owner.Status?.IsAttemptActive == true;
        if (wasActive != _backupActive) { UpdatePolling(); Changed(nameof(BackupSize)); }
        if (_active && (completed || changed)) _ = RequestRefreshAsync(true);
    }
    public void Deactivate()
    {
        _active = false; _generation++; _query?.Cancel(); _poll?.Cancel();
        _pending = _fullPending = false;
    }
    private void UpdatePolling()
    {
        if (!_active || !_backupActive || !HasLocation) { _poll?.Cancel(); return; }
        if (_poll is { IsCancellationRequested: false }) return;
        _poll = new(); _ = PollAsync(_poll);
    }
    private async Task PollAsync(CancellationTokenSource poll)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider ?? TimeProvider.System, poll.Token);
                if (!_active || !_backupActive) break;
                await RequestRefreshAsync(false);
            }
        }
        catch (OperationCanceledException) when (poll.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_poll, poll)) _poll = null; poll.Dispose(); }
    }
    private Task RequestRefreshAsync(bool full)
    {
        if (!_active) return Task.CompletedTask;
        _pending = true; _fullPending |= full;
        if (full) { _generation++; _backupBytes = null; _checkingBackup = HasLocation && includeBackupSize; Changed(nameof(BackupSize)); }
        if (_refresh is not null) return _refresh;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refresh = completion.Task; _ = DrainAsync(completion); return completion.Task;
    }
    private async Task DrainAsync(TaskCompletionSource completion)
    {
        try
        {
            while (_active && _pending)
            {
                var full = _fullPending; _pending = _fullPending = false;
                await ObserveAsync(full);
            }
        }
        finally { _refresh = null; completion.TrySetResult(); }
    }
    private async Task ObserveAsync(bool full)
    {
        var generation = _generation;
        using var query = new CancellationTokenSource(); _query = query;
        var path = _path;
        UpdateDrive(_drive, HasLocation);
        if (full) { _backupBytes = null; _checkingBackup = HasLocation && includeBackupSize; Changed(nameof(BackupSize)); }
        bool Current() => _active && generation == _generation;
        async Task DriveAsync()
        {
            CapacityObservation? value = null;
            try { value = await owner.ObserveDriveSpaceAsync(path, query.Token); }
            catch (Exception) { }
            finally { if (Current()) UpdateDrive(value, false); }
        }
        async Task BackupAsync()
        {
            try { var value = await owner.ObserveBackupSizeAsync(path, query.Token); if (Current() && value >= 0) _backupBytes = value; }
            catch (Exception) { }
            finally { if (Current()) { _checkingBackup = false; Changed(nameof(BackupSize)); } }
        }
        try { if (HasLocation) await Task.WhenAll(DriveAsync(), full && includeBackupSize ? BackupAsync() : Task.CompletedTask); }
        finally { if (ReferenceEquals(_query, query)) _query = null; }
    }
}
