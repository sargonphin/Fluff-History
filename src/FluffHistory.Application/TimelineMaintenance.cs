using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum MaintenanceState { NotNeeded, Throttled, Completed, Cancelled, Incomplete, Stale, NeedsAttention }
public sealed record MaintenanceSummary(MaintenanceState State, long Removed = 0, long LogicalBytes = 0,
    long CleanupDeferred = 0, long CleanupFailed = 0, bool ChangesMayExist = false)
{
    public bool NeedsAttention => State is MaintenanceState.Incomplete or MaintenanceState.Stale or MaintenanceState.NeedsAttention || CleanupDeferred > 0 || CleanupFailed > 0;
}

/// <summary>A process-independent, nonblocking admission lease. Disposal may occur on any thread.</summary>
public interface IOperationAdmission
{
    IDisposable? TryAcquire();
}

/// <summary>Shares backup, maintenance and enrollment admission. Optional platform lease is crash-safe;
/// the portable default is in-process. Neither implementation records operation history.</summary>
public sealed class BackupOperationGate(IOperationAdmission? admission = null)
{
    private readonly SemaphoreSlim _gate = new(1);
    private IDisposable? _lease;
    public bool IsBusy
    {
        get
        {
            if (!TryEnter()) return true;
            Exit(); return false;
        }
    }
    internal Task EnterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (admission is null) return _gate.WaitAsync(token);
        if (!TryEnter()) throw new ApplicationOperationException(ApplicationFailure.Busy);
        return Task.CompletedTask;
    }
    internal bool TryEnter()
    {
        if (!_gate.Wait(0)) return false;
        try
        {
            if (admission is null) return true;
            _lease = admission.TryAcquire();
            if (_lease is not null) return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _gate.Release(); return false; } // Admission infrastructure failure is fail-closed, including timer callbacks.
        catch { _gate.Release(); throw; }
        _gate.Release(); return false;
    }
    internal void Exit() { try { Interlocked.Exchange(ref _lease, null)?.Dispose(); } finally { _gate.Release(); } }
    public IDisposable? TryAcquire() => TryEnter() ? new GateLease(this) : null;
    private sealed class GateLease(BackupOperationGate owner) : IDisposable
    {
        private BackupOperationGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}

public sealed class TimelineCleanupPreview
{
    internal TimelineCleanupPreview(Guid owner, string path, Guid repository, HistoryCleanupPreview preview)
    { Owner = owner; Path = path; Repository = repository; Preview = preview; }
    internal Guid Owner { get; }
    internal string Path { get; }
    internal Guid Repository { get; }
    internal HistoryCleanupPreview Preview { get; }
    public HistoryCleanupMode Mode => Preview.Evaluation.Mode;
    public bool Complete => Preview.Complete && !Preview.Cancelled && !Preview.BudgetExceeded;
    public bool Cancelled => Preview.Cancelled;
    public bool BudgetExceeded => Preview.BudgetExceeded;
    public long Candidates => Preview.Eligible;
    public long AffectedPaths => Preview.AffectedPaths;
    public long LogicalBytes => Preview.EligibleLogicalBytes;
    public long ProtectedCurrent => Preview.Protected;
}

/// <summary>Opaque exact-preview adapter. Engine/Storage owners and candidates never escape this boundary.</summary>
public sealed class TimelineMaintenanceService(BackupOperationGate gate, string? workspace = null)
{
    public bool IsBusy => gate.IsBusy;
    private readonly Guid _owner = Guid.NewGuid();
    internal RetentionOptions Options { get; init; } = new(WorkspaceRoot: workspace ?? Path.Combine(Path.GetTempPath(), "Fluff-History", "Work"));
    public Task<TimelineCleanupPreview> PreviewAsync(string path, HistoryCleanupMode mode, DateTimeOffset utc, CancellationToken token = default)
        => Task.Run(() =>
        {
            if (!gate.TryEnter()) throw new ApplicationOperationException(ApplicationFailure.Busy);
            try
            {
                token.ThrowIfCancellationRequested(); var normalized = SourcePaths.Normalize(path); using var repository = ApplicationRepository.Open(normalized);
                var evaluation = mode == HistoryCleanupMode.KeepOnlyMostRecent ? new HistoryCleanupEvaluation(mode) : new(mode, utc);
                var preview = new HistoryCleanupEngine(repository, Options).Preview(evaluation, token);
                return new TimelineCleanupPreview(_owner, normalized, repository.Identity.RepositoryId, preview);
            }
            catch (ApplicationOperationException) { throw; }
            catch (Exception error) { throw ApplicationRepository.Failure(error); }
            finally { gate.Exit(); }
        });
    public Task<MaintenanceSummary> ExecuteAsync(TimelineCleanupPreview preview, CancellationToken token = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            if (preview.Owner != _owner || !preview.Complete) throw new ApplicationOperationException(ApplicationFailure.InvalidSelection);
            if (!gate.TryEnter()) throw new ApplicationOperationException(ApplicationFailure.Busy);
            try
            {
                token.ThrowIfCancellationRequested(); using var repository = ApplicationRepository.Open(preview.Path);
                if (repository.Identity.RepositoryId != preview.Repository) return new MaintenanceSummary(MaintenanceState.Stale);
                try { return Summary(new HistoryCleanupEngine(repository, Options).Execute(preview.Preview, token)); }
                catch (StaleHistoryCleanupPreviewException) { return new(MaintenanceState.Stale); }
                catch (HistoryCleanupExecutionException error) { return Summary(error.Result) with { State = MaintenanceState.NeedsAttention, ChangesMayExist = true }; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(MaintenanceState.Cancelled); }
            catch (ApplicationOperationException) { throw; }
            catch (Exception error) { throw ApplicationRepository.Failure(error); }
            finally { gate.Exit(); }
        });
    private static MaintenanceSummary Summary(HistoryCleanupExecutionResult r) => new(r.Cancelled ? MaintenanceState.Cancelled : r.Complete ? MaintenanceState.Completed : MaintenanceState.Incomplete,
        r.Removed, r.RemovedLogicalBytes, r.Cleanup.Deferred, r.Cleanup.Failed, r.BatchesCommitted > 0);
}

/// <summary>Runner-lifetime, once per repository/policy/UTC day. One fixed coordinator configuration;
/// changing repository/policy resets this small transient throttle, including its prior dates.</summary>
internal sealed class AutomaticRetention
{
    internal RetentionOptions? Options { get; init; }
    internal Action? PreviewCompleted { get; init; }
    private readonly object _gate = new();
    private string? _repository;
    private RetentionPolicy _policy;
    private readonly HashSet<DateOnly> _days = [];
    internal MaintenanceSummary Run(Repository repository, BackupConfiguration configuration, ScanResult scan, DateTimeOffset utc, CancellationToken token)
    {
        var policy = configuration.RetentionPolicy;
        if (policy is RetentionPolicy.Forever or RetentionPolicy.UntilSaturation || scan.Outcome != ScanOutcome.Completed || scan.Partial || scan.Cancelled || scan.ReconciliationMayBeRequired)
            return new(MaintenanceState.NotNeeded);
        lock (_gate)
        {
            if (_repository != configuration.RepositoryPath || _policy != policy) { _repository = configuration.RepositoryPath; _policy = policy; _days.Clear(); }
            // A fixed bound avoids unbounded session state. After 4096 distinct dates, defer to restart.
            if (_days.Count == 4096 || !_days.Add(DateOnly.FromDateTime(utc.UtcDateTime))) return new(MaintenanceState.Throttled);
        }
        try
        {
            var engine = new RetentionEngine(repository, Options ?? new(WorkspaceRoot: configuration.ScanOptions.WorkspaceRoot));
            var preview = engine.Preview(policy, utc, token);
            if (!preview.Complete || preview.Cancelled || preview.BudgetExceeded) return new(preview.Cancelled ? MaintenanceState.Cancelled : MaintenanceState.Incomplete);
            PreviewCompleted?.Invoke();
            try { return Summary(engine.Execute(preview, token)); }
            catch (StaleRetentionPreviewException) { return new(MaintenanceState.Stale); }
            catch (RetentionExecutionException error) { return Summary(error.Result) with { State = MaintenanceState.NeedsAttention, ChangesMayExist = true }; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(MaintenanceState.Cancelled); }
        catch (Exception) { return new(MaintenanceState.NeedsAttention); }
    }
    private static MaintenanceSummary Summary(RetentionExecutionResult r) => new(r.Cancelled ? MaintenanceState.Cancelled : r.Complete ? MaintenanceState.Completed : MaintenanceState.Incomplete,
        r.Removed, r.RemovedLogicalBytes, r.Cleanup.Deferred, r.Cleanup.Failed, r.BatchesCommitted > 0);
}

public static class BackupWorkspace
{
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "Fluff-History", "Work");
    public static Task<string?> ValidateAsync(string repositoryPath, string workspace, CancellationToken token = default) => Task.Run(() =>
    {
        try
        {
            token.ThrowIfCancellationRequested(); using var repository = ApplicationRepository.Open(repositoryPath);
            _ = repository.ObserveAllocationGeometry(workspace); return (string?)null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return "Windows could not validate the backup location or the app's temporary working data. Check access and free space, then try again."; }
    });
}
