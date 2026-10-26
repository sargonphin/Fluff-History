namespace FluffHistory.Application;

/// <summary>One in-process owner of scheduled and manual attempts. All public operations are thread safe.
/// Notifications contain complete snapshots, run outside locks on arbitrary background/caller threads,
/// and isolate subscriber exceptions. Subscribers must be brief and marshal to their UI themselves.</summary>
public sealed class BackupCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly BackupConfiguration _configuration;
    private readonly IBackupAttemptRunner _runner;
    private readonly IBackupSafeguard _safeguard;
    private readonly TimeProvider _time;
    private readonly BackupOperationGate? _operationGate;
    private readonly ITimer _timer;
    private readonly ITimer _progressTimer;
    private BackupProgress? _pendingProgress;
    private BackupStatus _status;
    private DateTimeOffset? _next, _consumedThrough;
    private ActiveAttempt? _active;
    private bool _disposed;
    private Task? _disposeTask;
    private long? _lastProgressTimestamp;
    private Action<BackupStatus>? _statusChanged;
    private int _notificationsInFlight;
    private TaskCompletionSource? _notificationsDrained;

    public BackupCoordinator(BackupConfiguration configuration, BackupSchedule schedule, IBackupAttemptRunner runner,
        IBackupSafeguard safeguard, TimeProvider? timeProvider = null, BackupOperationGate? operationGate = null)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(safeguard);
        _configuration = configuration; _runner = runner; _safeguard = safeguard; _time = timeProvider ?? TimeProvider.System;
        _operationGate = operationGate;
        _status = new(0, SchedulerLifecycle.Stopped, false, schedule, null, false, false, null, null,
            new(BackupPhase.None), null, null, 0, SafeguardDecision.Allow, null);
        _timer = _time.CreateTimer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _progressTimer = _time.CreateTimer(FlushProgress, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public BackupStatus Status { get { lock (_gate) return _status; } }
    public event Action<BackupStatus> StatusChanged
    {
        add { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _statusChanged += value; } }
        remove { lock (_gate) _statusChanged -= value; }
    }

    public void Start()
    {
        BackupStatus snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_status.ScheduleEnabled) return;
            var now = _time.GetUtcNow();
            _next = NextFuture(now);
            snapshot = Update(_status with { ScheduleEnabled = true });
            Arm(now);
        }
        Notify(snapshot);
    }

    /// <summary>Stops future cadence only. An admitted attempt may finish; this never requests cancellation.</summary>
    public void StopScheduling()
    {
        BackupStatus snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_status.ScheduleEnabled) return;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _next = null;
            snapshot = Update(_status with { ScheduleEnabled = false });
        }
        Notify(snapshot);
    }

    /// <summary>Requests cooperative cancellation separately from scheduling. Callback completion is awaitable;
    /// blocked native I/O can still outlast cancellation. Does not wait for the attempt itself.</summary>
    public Task CancelActiveAsync()
    {
        Task cancellation; BackupStatus? snapshot = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not { } active) return Task.CompletedTask;
            var alreadyRequested = active.CancellationRequested;
            cancellation = active.RequestCancellation();
            if (!alreadyRequested && active.CancellationRequested) snapshot = Update(_status);
        }
        if (snapshot is not null) Notify(snapshot);
        return cancellation;
    }

    /// <summary>Manual entry point shares admission/network safeguards; the battery start preference applies only to scheduled work.
    /// Returns false when another attempt is admitted. Manual work does not request scheduled background impact.</summary>
    public bool TryRunNow() => RequestRunNow() == BackupAdmission.Started;
    public BackupAdmission RequestRunNow()
    {
        ActiveAttempt active; BackupStatus snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null || _operationGate is not null && !_operationGate.TryEnter()) return BackupAdmission.Busy;
            var now = _time.GetUtcNow();
            SkipDue(now);
            active = Reserve(AttemptTrigger.Manual, null, now);
            snapshot = Update(_status);
            Arm(now);
        }
        Launch(active); Notify(snapshot); return BackupAdmission.Started;
    }

    private void Tick(object? unused)
    {
        ActiveAttempt? active = null; BackupStatus? snapshot = null;
        lock (_gate)
        {
            if (_disposed || !_status.ScheduleEnabled) return;
            var now = _time.GetUtcNow();
            if (_next is { } due && now >= due)
            {
                // A callback delayed across a whole additional interval is a resume-style gap.
                // Discard all due points. Smaller ordinary delivery latency admits at most one occurrence.
                if (_active is not null || now.UtcTicks - due.UtcTicks >= _status.Schedule.Interval.Ticks ||
                    _operationGate is not null && !_operationGate.TryEnter()) SkipDue(now);
                else
                {
                    _consumedThrough = due;
                    _next = NextFuture(now);
                    active = Reserve(AttemptTrigger.Scheduled, due, now);
                }
                snapshot = Update(_status);
            }
            Arm(now); // At most one minute between UTC rechecks, also handles wall-clock rollback.
        }
        if (active is not null) Launch(active);
        if (snapshot is not null) Notify(snapshot);
    }

    private ActiveAttempt Reserve(AttemptTrigger trigger, DateTimeOffset? due, DateTimeOffset now)
    {
        var active = new ActiveAttempt(trigger, due, now, _time.GetTimestamp());
        _active = active;
        _lastProgressTimestamp = null;
        _pendingProgress = null;
        _progressTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _status = _status with { Progress = new(BackupPhase.CheckingSafeguards) };
        return active;
    }

    private void Launch(ActiveAttempt active) => _ = Task.Run(() => ExecuteAsync(active));

    private async Task ExecuteAsync(ActiveAttempt active)
    {
        BackupAttemptResult? result = null;
        var decision = SafeguardDecision.Allow;
        bool admitted = false;
        try
        {
            active.Cancellation.Token.ThrowIfCancellationRequested();
            decision = _safeguard.Evaluate(_configuration, active.Trigger);
            ReportSafeguard(active, decision);
            if (decision.Allowed)
            {
                admitted = true;
                result = await _runner.RunAsync(_configuration, active.Trigger,
                    p => ReportProgress(active, p), active.Cancellation.Token).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(result);
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        { admitted = true; result = new(BackupOutcome.Cancelled); }
        catch (Exception error)
        {
            admitted = true;
            // Only type, never the potentially sensitive exception message/graph.
            System.Diagnostics.Trace.TraceError("Backup orchestration failure ({0}).", error.GetType().Name);
            result = new(BackupOutcome.ScanControlFailure, SafeSummary: "Backup orchestration failed.", CommittedProgressMayExist: true);
        }

        Task cancellation;
        lock (_gate)
        {
            active.Completing = true;
            cancellation = active.CancellationTask ?? Task.CompletedTask;
        }
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception) { System.Diagnostics.Trace.TraceError("Backup cancellation callback failed."); }
        active.Cancellation.Dispose();

        BackupStatus snapshot;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            SkipDue(now); // The gate stays owned through completion, including exactly-due points.
            _operationGate?.Exit();
            _pendingProgress = null;
            _progressTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _active = null;
            var last = admitted ? new AttemptSnapshot(active.Trigger, active.Due, active.StartedUtc, now,
                _time.GetElapsedTime(active.StartedTimestamp), result!) : _status.LastAttempt;
            snapshot = Update(_status with
            {
                Progress = new(BackupPhase.Finished), LastAttempt = last, LastSafeguard = decision,
                LastDeferredUtc = decision.Allowed ? _status.LastDeferredUtc : now,
                LastSuccessfulUtc = result?.Outcome is BackupOutcome.Completed or BackupOutcome.CompletedWithSourceProblems ? now : _status.LastSuccessfulUtc
            });
            Arm(now);
            active.Completion.TrySetResult();
        }
        Notify(snapshot);
    }

    private void ReportProgress(ActiveAttempt active, BackupProgress progress)
    {
        try { progress.Validate(); } catch (InvalidDataException) { return; }
        BackupStatus snapshot;
        lock (_gate)
        {
            if (_disposed || _active != active) return;
            var now = _time.GetTimestamp();
            if (_status.Progress == progress)
            { _pendingProgress = null; _progressTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
            if (progress.Phase != BackupPhase.Finished && _lastProgressTimestamp is { } last &&
                _time.GetElapsedTime(last, now) is var elapsed && elapsed < TimeSpan.FromMilliseconds(250))
            {
                _pendingProgress = progress;
                _progressTimer.Change(TimeSpan.FromMilliseconds(250) - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }
            _pendingProgress = null;
            _progressTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lastProgressTimestamp = now;
            snapshot = Update(_status with { Progress = progress });
        }
        Notify(snapshot);
    }

    private void FlushProgress(object? unused)
    {
        BackupStatus snapshot;
        lock (_gate)
        {
            if (_disposed || _active is null || _pendingProgress is null) return;
            if (_lastProgressTimestamp is { } last && _time.GetElapsedTime(last) is var elapsed && elapsed < TimeSpan.FromMilliseconds(250))
            { _progressTimer.Change(TimeSpan.FromMilliseconds(250) - elapsed, Timeout.InfiniteTimeSpan); return; }
            snapshot = Update(_status with { Progress = _pendingProgress });
            _pendingProgress = null; _lastProgressTimestamp = _time.GetTimestamp();
        }
        Notify(snapshot);
    }

    private void ReportSafeguard(ActiveAttempt active, SafeguardDecision decision)
    {
        BackupStatus snapshot;
        lock (_gate)
        {
            if (_disposed || _active != active) return;
            snapshot = Update(_status with { LastSafeguard = decision });
        }
        Notify(snapshot);
    }

    private DateTimeOffset? NextFuture(DateTimeOffset now)
    {
        var after = _consumedThrough is { } consumed && consumed > now ? consumed : now;
        return _status.Schedule.NextAfter(after);
    }

    private void SkipDue(DateTimeOffset now)
    {
        if (!_status.ScheduleEnabled || _next is not { } due || now < due) return;
        var count = (now.UtcTicks - due.UtcTicks) / _status.Schedule.Interval.Ticks + 1;
        _consumedThrough = due.AddTicks((count - 1) * _status.Schedule.Interval.Ticks);
        _next = NextFuture(now);
        _status = _status with { SkippedCadences = _status.SkippedCadences > long.MaxValue - count ? long.MaxValue : _status.SkippedCadences + count };
    }

    private BackupStatus Update(BackupStatus value) => _status = value with
    {
        Revision = _status.Revision + 1,
        Lifecycle = !value.ScheduleEnabled ? SchedulerLifecycle.Stopped : _active is null ? SchedulerLifecycle.Idle : SchedulerLifecycle.Running,
        NextScheduledUtc = value.ScheduleEnabled ? _next : null,
        IsAttemptActive = _active is not null, CancellationRequested = _active?.CancellationRequested ?? false,
        ActiveStartedUtc = _active?.StartedUtc, ActiveTrigger = _active?.Trigger
    };

    private void Arm(DateTimeOffset now)
    {
        if (_disposed) return;
        var delay = !_disposed && _status.ScheduleEnabled && _next is { } next ? next - now : Timeout.InfiniteTimeSpan;
        if (delay != Timeout.InfiniteTimeSpan)
        {
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
        }
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Notify(BackupStatus snapshot)
    {
        Action<BackupStatus>? subscribers;
        lock (_gate)
        {
            subscribers = _disposed ? null : _statusChanged;
            if (subscribers is null) return;
            if (_notificationsInFlight++ == 0) _notificationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            foreach (Action<BackupStatus> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(snapshot); }
                catch (Exception) { System.Diagnostics.Trace.TraceError("Backup status subscriber failed."); }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--_notificationsInFlight == 0)
                { _notificationsDrained!.TrySetResult(); _notificationsDrained = null; }
            }
        }
    }

    /// <summary>Stops timers, requests cancellation and asynchronously joins all admitted work.
    /// Native I/O may delay completion; no forced thread/process termination.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true; _next = null;
            _statusChanged = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _progressTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _active?.RequestCancellation();
            Update(_status with { ScheduleEnabled = false });
            _disposeTask = DisposeCoreAsync(_active?.Completion.Task ?? Task.CompletedTask,
                _notificationsDrained?.Task ?? Task.CompletedTask);
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task active, Task notifications)
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _progressTimer.DisposeAsync().ConfigureAwait(false);
        await active.ConfigureAwait(false);
        await notifications.ConfigureAwait(false);
    }

    private sealed class ActiveAttempt(AttemptTrigger trigger, DateTimeOffset? due, DateTimeOffset startedUtc, long timestamp)
    {
        internal AttemptTrigger Trigger { get; } = trigger;
        internal DateTimeOffset? Due { get; } = due;
        internal DateTimeOffset StartedUtc { get; } = startedUtc;
        internal long StartedTimestamp { get; } = timestamp;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? CancellationTask { get; private set; }
        internal bool Completing { get; set; }
        internal bool CancellationRequested { get; private set; }
        internal Task RequestCancellation()
        {
            if (Completing) return CancellationTask ?? Task.CompletedTask;
            CancellationRequested = true;
            return CancellationTask ??= Cancellation.CancelAsync();
        }
    }
}
