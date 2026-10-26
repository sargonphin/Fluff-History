using System.Threading.Channels;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class BackupCoordinatorTests
{
    internal static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static BackupConfiguration Configuration(SafeguardPolicy? policy = null) =>
        new(Path.GetFullPath("repository"), [new(new SourceRootConfiguration(Guid.NewGuid(), "source"), Path.GetFullPath("source"))], safeguards: policy);
    private static BackupCoordinator Create(ManualSchedulerTime time, ControlledRunner runner, IBackupSafeguard? safeguards = null) =>
        new(Configuration(), new(TimeSpan.FromMinutes(10), Anchor), runner, safeguards ?? new Gate(), time);

    [Fact]
    public async Task StartFirstFutureTickCompletionAndNextTick()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner);
        coordinator.Start();
        Assert.Equal(SchedulerLifecycle.Idle, coordinator.Status.Lifecycle);
        Assert.Equal(Anchor, coordinator.Status.NextScheduledUtc);
        time.Advance(TimeSpan.FromMinutes(1)); var first = await runner.Next();
        Assert.Equal(SchedulerLifecycle.Running, coordinator.Status.Lifecycle);
        Assert.Equal(Anchor, coordinator.Status.ActiveStartedUtc);
        Assert.Equal(Anchor.AddMinutes(10), coordinator.Status.NextScheduledUtc);
        first.Complete(); await Until(coordinator, s => s.LastAttempt is not null && !s.IsAttemptActive);
        Assert.Equal(BackupOutcome.Completed, coordinator.Status.LastAttempt!.Result.Outcome);
        Assert.Equal(Anchor, coordinator.Status.LastSuccessfulUtc);
        time.Advance(TimeSpan.FromMinutes(10)); (await runner.Next()).Complete();
        await Until(coordinator, s => s.LastAttempt?.StartedUtc == Anchor.AddMinutes(10) && !s.IsAttemptActive);
        Assert.Equal(1, runner.MaximumActive);
    }

    [Theory]
    [InlineData(29, 2, 30)] [InlineData(31, 3, 40)] [InlineData(1001, 100, 1010)]
    public async Task ActiveCadencesAreDiscardedWithoutBacklog(int duration, int skipped, int next)
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var first = await runner.Next();
        time.Advance(TimeSpan.FromMinutes(duration));
        Assert.Equal(skipped, coordinator.Status.SkippedCadences); Assert.Equal(1, runner.Calls);
        first.Complete(); await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(Anchor.AddMinutes(next), coordinator.Status.NextScheduledUtc);
        Assert.Equal(TimeSpan.FromMinutes(duration), coordinator.Status.LastAttempt!.Duration);
        Assert.Equal(1, runner.Calls);
        time.Advance(TimeSpan.FromMinutes(next - duration)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(1, runner.MaximumActive);
    }

    [Theory]
    [InlineData(BackupOutcome.ScanControlFailure)] [InlineData(BackupOutcome.RecoveryRequired)] [InlineData(BackupOutcome.Cancelled)]
    public async Task NonSuccessReturnsToIdleWithoutImmediateRetry(BackupOutcome outcome)
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        (await runner.Next()).Complete(outcome); await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(outcome, coordinator.Status.LastAttempt!.Result.Outcome);
        Assert.Equal(outcome == BackupOutcome.RecoveryRequired, coordinator.Status.RecoveryRequired);
        Assert.Equal(1, runner.Calls); Assert.Equal(Anchor.AddMinutes(10), coordinator.Status.NextScheduledUtc);
        time.Advance(TimeSpan.FromMinutes(10)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task UnexpectedAttemptExceptionIsBoundedAndDoesNotKillCadence()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        (await runner.Next()).Result.SetException(new InvalidOperationException("synthetic-secret-do-not-retain"));
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(BackupOutcome.ScanControlFailure, coordinator.Status.LastAttempt!.Result.Outcome);
        Assert.DoesNotContain("synthetic-secret", coordinator.Status.ToString());
        time.Advance(TimeSpan.FromMinutes(10)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
    }

    [Fact]
    public async Task StopFutureLeavesActiveAndCancelIsSeparate()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var active = await runner.Next(); coordinator.StopScheduling();
        Assert.False(active.Token.IsCancellationRequested); Assert.True(coordinator.Status.IsAttemptActive);
        Assert.Equal(SchedulerLifecycle.Stopped, coordinator.Status.Lifecycle); Assert.Null(coordinator.Status.NextScheduledUtc);
        time.Advance(TimeSpan.FromHours(1)); Assert.Equal(1, runner.Calls);
        active.Complete(); await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(SchedulerLifecycle.Stopped, coordinator.Status.Lifecycle);
        Assert.True(coordinator.TryRunNow()); var manual = await runner.Next();
        Assert.Equal(AttemptTrigger.Manual, manual.Trigger);
        await coordinator.CancelActiveAsync(); await Until(coordinator, s => !s.IsAttemptActive);
        Assert.True(manual.Token.IsCancellationRequested);
        Assert.Equal(BackupOutcome.Cancelled, coordinator.Status.LastAttempt!.Result.Outcome);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelActiveCanKeepOrStopScheduling(bool stop)
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        await runner.Next(); if (stop) coordinator.StopScheduling();
        await coordinator.CancelActiveAsync(); await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(!stop, coordinator.Status.ScheduleEnabled);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(stop ? SchedulerLifecycle.Stopped : SchedulerLifecycle.Idle, coordinator.Status.Lifecycle);
    }

    [Fact]
    public async Task LargeForwardGapDiscardsAllDuePointsThenResumesCadence()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start();
        time.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, runner.Calls); Assert.Equal(432, coordinator.Status.SkippedCadences);
        Assert.Equal(Anchor.AddDays(3), coordinator.Status.NextScheduledUtc);
        time.Advance(TimeSpan.FromMinutes(1)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ClockRollbackDoesNotReplayConsumedCadenceEvenAfterStopStart()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var first = await runner.Next(); time.SetUtcNow(Anchor.AddMinutes(-20)); first.Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(TimeSpan.Zero, coordinator.Status.LastAttempt!.Duration);
        coordinator.StopScheduling(); coordinator.Start();
        Assert.Equal(Anchor.AddMinutes(10), coordinator.Status.NextScheduledUtc);
        time.Advance(TimeSpan.FromMinutes(20)); Assert.Equal(1, runner.Calls);
        time.Advance(TimeSpan.FromMinutes(10)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive); Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task DeferredTickHasReasonNeverCallsRunnerAndDoesNotRetry()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner(); var gate = new Gate { Decision = new(false, SafeguardReason.OnBattery) };
        await using var coordinator = Create(time, runner, gate); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        await Until(coordinator, s => s.LastDeferredUtc is not null && !s.IsAttemptActive);
        var deferred = coordinator.Status;
        Assert.Equal(SafeguardReason.OnBattery, deferred.LastSafeguard.Reason);
        Assert.Null(deferred.LastAttempt); Assert.Equal(0, runner.Calls);
        gate.Decision = SafeguardDecision.Allow;
        Assert.Equal(SafeguardReason.OnBattery, deferred.LastSafeguard.Reason); // Earlier snapshot remains immutable.
        Assert.Equal(0, runner.Calls);
        time.Advance(TimeSpan.FromMinutes(10)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
    }

    [Fact]
    public async Task ActiveWorkDoesNotReevaluateChangingPlatformPolicy()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner(); var gate = new Gate();
        await using var coordinator = Create(time, runner, gate); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var first = await runner.Next(); gate.Decision = new(false, SafeguardReason.NetworkUnavailable);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(first.Token.IsCancellationRequested); Assert.Equal(1, gate.Calls);
        first.Complete(); await Until(coordinator, s => !s.IsAttemptActive);
        time.Advance(TimeSpan.FromMinutes(10)); await Until(coordinator, s => s.LastDeferredUtc is not null && !s.IsAttemptActive);
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("stopped")] [InlineData("idle")] [InlineData("running")]
    public async Task DisposalStopsTimersCancelsAndJoinsWork(string state)
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        var coordinator = Create(time, runner); Invocation? running = null;
        if (state != "stopped") coordinator.Start();
        if (state == "running") { time.Advance(TimeSpan.FromMinutes(1)); running = await runner.Next(); }
        await coordinator.DisposeAsync(); await coordinator.DisposeAsync();
        Assert.Equal(0, time.TimerCount); Assert.False(coordinator.Status.IsAttemptActive);
        Assert.False(coordinator.Status.ScheduleEnabled); Assert.True(running is null || running.Token.IsCancellationRequested);
        time.Advance(TimeSpan.FromHours(2)); Assert.Equal(state == "running" ? 1 : 0, runner.Calls);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Start());
    }

    [Fact]
    public async Task DisposalJoinsWorkThatOutlastsCancellationRatherThanClaimingImmediateStop()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner { IgnoreCancellation = true };
        var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var active = await runner.Next(); var disposing = coordinator.DisposeAsync().AsTask();
        Assert.True(active.Token.IsCancellationRequested); Assert.False(disposing.IsCompleted);
        Assert.True(coordinator.Status.IsAttemptActive); Assert.True(coordinator.Status.CancellationRequested); Assert.Equal(0, time.TimerCount);
        active.Complete(BackupOutcome.Cancelled); await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(coordinator.Status.IsAttemptActive); Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task PendingCancellationIsObservableWhileNativeStyleWorkHasNotFinished()
    {
        var time = new ManualSchedulerTime(Anchor); var runner = new ControlledRunner { IgnoreCancellation = true };
        await using var coordinator = Create(time, runner);
        Assert.True(coordinator.TryRunNow()); var active = await runner.Next();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += s => { if (s.CancellationRequested && s.IsAttemptActive) observed.TrySetResult(); };
        try
        {
            await coordinator.CancelActiveAsync(); await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(coordinator.Status.CancellationRequested); Assert.True(coordinator.Status.IsAttemptActive);
            active.Complete(BackupOutcome.Cancelled); await Until(coordinator, s => !s.IsAttemptActive);
            Assert.False(coordinator.Status.CancellationRequested);
            Assert.Equal(BackupOutcome.Cancelled, coordinator.Status.LastAttempt!.Result.Outcome);
        }
        finally { active.Result.TrySetResult(new(BackupOutcome.Cancelled)); }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotStrandGateOrDisposal()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var active = await runner.Next();
        using var registration = active.Token.Register(() =>
        {
            _ = coordinator.Status; // Callback may safely reenter application state.
            throw new InvalidOperationException("synthetic callback failure");
        });
        await Assert.ThrowsAsync<AggregateException>(() => coordinator.CancelActiveAsync());
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(BackupOutcome.Cancelled, coordinator.Status.LastAttempt!.Result.Outcome);
        time.Advance(TimeSpan.FromMinutes(10)); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
    }

    [Fact]
    public async Task DisposalDrainsAnAlreadyExecutingFinalStatusSubscriber()
    {
        var time = new ManualSchedulerTime(Anchor); var runner = new ControlledRunner();
        var coordinator = Create(time, runner);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += s =>
        {
            if (s.LastAttempt is not null && !s.IsAttemptActive)
            { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
        };
        try
        {
            Assert.True(coordinator.TryRunNow()); (await runner.Next()).Complete();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposing = coordinator.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted); // The scan ended, but an owned notification is still running.
            release.Set(); await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(coordinator.Status.IsAttemptActive);
        }
        finally { release.Set(); await coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task NotificationsAreOutsideLocksAndThrowingSubscriberCannotBreakWork()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner);
        coordinator.StatusChanged += _ => throw new InvalidOperationException("observer");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += s =>
        {
            // Acquiring the coordinator from another thread proves subscribers do not own its lock.
            Assert.True(Task.Run(() => coordinator.Status).Wait(TimeSpan.FromSeconds(5)));
            if (s.LastAttempt is not null) observed.TrySetResult();
        };
        coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1)); (await runner.Next()).Complete();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BackupOutcome.Completed, coordinator.Status.LastAttempt!.Result.Outcome);
    }

    [Fact]
    public async Task ConcurrentControlsAndManualAttemptsKeepOneGate()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => { if (i % 2 == 0) coordinator.Start(); else coordinator.StopScheduling(); })));
        coordinator.Start(); Assert.Equal(SchedulerLifecycle.Idle, coordinator.Status.Lifecycle);
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(coordinator.TryRunNow)));
        Assert.Single(results, b => b); var first = await runner.Next();
        time.Advance(TimeSpan.FromMinutes(11)); Assert.Equal(1, runner.Calls); Assert.False(coordinator.TryRunNow());
        first.Complete(); await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(1, runner.MaximumActive); Assert.Equal(Anchor.AddMinutes(20), coordinator.Status.NextScheduledUtc);
    }

    [Fact]
    public async Task CompletionAtCadenceDiscardsThatPointAndRecentStatusReplacesRatherThanAccumulates()
    {
        var time = new ManualSchedulerTime(Anchor.AddMinutes(-1)); var runner = new ControlledRunner();
        await using var coordinator = Create(time, runner); coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        var first = await runner.Next(); time.SetUtcNow(Anchor.AddMinutes(10)); first.Complete();
        await Until(coordinator, s => !s.IsAttemptActive);
        Assert.Equal(1, coordinator.Status.SkippedCadences); Assert.Equal(Anchor.AddMinutes(20), coordinator.Status.NextScheduledUtc);
        var old = coordinator.Status;
        for (int i = 0; i < 10; i++)
        {
            Assert.True(coordinator.TryRunNow()); (await runner.Next()).Complete(BackupOutcome.CompletedWithSourceProblems);
            await Until(coordinator, s => !s.IsAttemptActive);
        }
        Assert.Equal(BackupOutcome.Completed, old.LastAttempt!.Result.Outcome);
        Assert.Equal(BackupOutcome.CompletedWithSourceProblems, coordinator.Status.LastAttempt!.Result.Outcome);
        Assert.True(coordinator.Status.Revision > old.Revision);
    }

    [Fact]
    public void ScheduleMathIsAnchoredValidatedAndHandlesRepresentableEnd()
    {
        var schedule = new BackupSchedule(TimeSpan.FromMinutes(10), Anchor);
        Assert.Equal(Anchor, schedule.NextAfter(Anchor.AddTicks(-1)));
        Assert.Equal(Anchor.AddMinutes(10), schedule.NextAfter(Anchor));
        Assert.Equal(Anchor.AddMinutes(40), schedule.NextAfter(Anchor.AddMinutes(31)));
        Assert.Null(schedule.NextAfter(DateTimeOffset.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupSchedule(TimeSpan.Zero, Anchor));
        Assert.Throws<ArgumentException>(() => new BackupSchedule(TimeSpan.FromMinutes(1), Anchor.ToOffset(TimeSpan.FromHours(1))));
    }

    internal static async Task Until(BackupCoordinator coordinator, Func<BackupStatus, bool> predicate, TimeSpan? watchdog = null)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(BackupStatus _) { if (predicate(coordinator.Status)) ready.TrySetResult(); }
        coordinator.StatusChanged += Changed;
        try { Changed(coordinator.Status); await ready.Task.WaitAsync(watchdog ?? TimeSpan.FromSeconds(10)); }
        finally { coordinator.StatusChanged -= Changed; }
    }

    [Fact]
    public async Task TypedBusyAdmissionNeverQueuesAndCanStartImmediatelyAfterCompletionAtSameUtc()
    {
        var time = new ManualSchedulerTime(Anchor); var runner = new ControlledRunner { IgnoreCancellation = true };
        await using var coordinator = Create(time, runner); coordinator.Start();
        Assert.Equal(BackupAdmission.Started, coordinator.RequestRunNow()); var first = await runner.Next();
        Assert.Equal(BackupAdmission.Busy, coordinator.RequestRunNow());
        await coordinator.CancelActiveAsync(); Assert.Equal(BackupAdmission.Busy, coordinator.RequestRunNow());
        time.Advance(TimeSpan.FromMinutes(10)); Assert.Equal(1, runner.Calls);
        first.Complete(); await Until(coordinator, s => !s.IsAttemptActive); var previous = coordinator.Status.LastAttempt;
        Assert.Equal(BackupAdmission.Started, coordinator.RequestRunNow()); (await runner.Next()).Complete();
        await Until(coordinator, s => !s.IsAttemptActive); Assert.NotSame(previous, coordinator.Status.LastAttempt);
        Assert.Equal(previous!.FinishedUtc, coordinator.Status.LastAttempt!.FinishedUtc); Assert.Equal(2, runner.Calls); Assert.Equal(1, runner.MaximumActive);
    }

    private sealed class Gate : IBackupSafeguard
    {
        internal SafeguardDecision Decision = SafeguardDecision.Allow;
        internal int Calls;
        public SafeguardDecision Evaluate(BackupConfiguration configuration) { Interlocked.Increment(ref Calls); return Decision; }
    }
    internal sealed class ControlledRunner : IBackupAttemptRunner
    {
        private readonly Channel<Invocation> _started = Channel.CreateUnbounded<Invocation>();
        private int _active;
        internal int Calls, MaximumActive;
        internal bool IgnoreCancellation;
        internal Action<BackupProgress>? Progress;
        internal Task<Invocation> Next() => _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<BackupAttemptResult> RunAsync(BackupConfiguration configuration, AttemptTrigger trigger,
            Action<BackupProgress>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); var count = Interlocked.Increment(ref _active);
            MaximumActive = Math.Max(MaximumActive, count);
            var invocation = new Invocation(trigger, cancellationToken);
            Progress = progress; progress?.Invoke(new(BackupPhase.Scanning)); _started.Writer.TryWrite(invocation);
            try { return IgnoreCancellation ? await invocation.Result.Task : await invocation.Result.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    internal sealed class Invocation(AttemptTrigger trigger, CancellationToken token)
    {
        internal AttemptTrigger Trigger { get; } = trigger;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<BackupAttemptResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(BackupOutcome outcome = BackupOutcome.Completed) => Result.SetResult(new(outcome));
    }
}
