using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class UpdateAdmissionTests
{
    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)]
    public async Task AutomaticOrDeclinedManualNeverRequestsCancellation(bool manual, bool consent)
    {
        var gate = new BackupOperationGate(); using var backup = gate.TryAcquire(); var cancelled = false;
        using var result = await new UpdateInstallAdmission(gate, () => true, () => { cancelled = true; return Task.CompletedTask; }).AcquireAsync(manual, consent, default);
        Assert.Equal(UpdateFailure.BackupStillRunning, result.Failure); Assert.False(cancelled); Assert.True(gate.IsBusy);
    }
    [Fact]
    public async Task ConfirmedManualWaitsForCancellationThenOwnsSameGate()
    {
        var gate = new BackupOperationGate(); var backup = gate.TryAcquire(); bool active = true, cancelled = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new UpdateInstallAdmission(gate, () => active, () => { cancelled = true; return completion.Task; }).AcquireAsync(true, true, default);
        Assert.True(cancelled); Assert.False(pending.IsCompleted); backup!.Dispose(); active = false; completion.SetResult();
        using (var result = await pending) { Assert.Equal(UpdateFailure.None, result.Failure); Assert.True(gate.IsBusy); }
        Assert.False(gate.IsBusy);
    }
    [Fact]
    public async Task BlockedCancellationTimesOutWithoutLosingBackupOwnership()
    {
        var gate = new BackupOperationGate(); using var backup = gate.TryAcquire(); var clock = new ManualSchedulerTime(BackupCoordinatorTests.Anchor);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new UpdateInstallAdmission(gate, () => true, () => blocked.Task, clock).AcquireAsync(true, true, default);
        clock.Advance(TimeSpan.FromSeconds(20)); using var result = await pending;
        Assert.Equal(UpdateFailure.BackupStillRunning, result.Failure); Assert.True(gate.IsBusy); Assert.False(blocked.Task.IsCompleted);
        blocked.SetResult();
    }
    [Fact]
    public async Task AcknowledgedCancellationStillWaitsForActualAttemptExit()
    {
        var gate = new BackupOperationGate(); var backup = gate.TryAcquire(); var clock = new ManualSchedulerTime(BackupCoordinatorTests.Anchor); var active = true;
        var pending = new UpdateInstallAdmission(gate, () => active, () => Task.CompletedTask, clock).AcquireAsync(true, true, default);
        Assert.False(pending.IsCompleted); backup!.Dispose(); active = false;
        clock.Advance(TimeSpan.FromMilliseconds(100)); using var ready = await pending;
        Assert.Equal(UpdateFailure.None, ready.Failure);
    }
    [Fact]
    public async Task OtherMaintenanceCannotBeOverriddenAndIdleLaterSucceeds()
    {
        var gate = new BackupOperationGate(); var maintenance = gate.TryAcquire();
        var admission = new UpdateInstallAdmission(gate, () => false, () => throw new InvalidOperationException());
        using (var busy = await admission.AcquireAsync(false, false, default)) Assert.Equal(UpdateFailure.Busy, busy.Failure);
        maintenance!.Dispose(); using var ready = await admission.AcquireAsync(false, false, default); Assert.Equal(UpdateFailure.None, ready.Failure);
    }
    [Fact]
    public async Task RealCoordinatorAutomaticBackupCompletesNormallyBeforeUpdaterEnters()
    {
        using var temp = new TemporaryDirectory(); var gate = new BackupOperationGate(); var runner = new BackupCoordinatorTests.ControlledRunner();
        var settings = AppSettings.Empty with { RepositoryPath = temp.Child("repo"), Sources = [SourceMapping.New(temp.Child("source"))] };
        await using var backup = new BackupCoordinator(settings.ToBackupConfiguration(SourceFolderPolicy.Empty, temp.Child("workspace")),
            settings.ToSchedule(), runner, new Allowed(), operationGate: gate);
        Assert.Equal(BackupAdmission.Started, backup.RequestRunNow()); var call = await runner.Next();
        var update = new UpdateInstallAdmission(gate, () => backup.Status.IsAttemptActive, backup.CancelActiveAsync);
        using (var waiting = await update.AcquireAsync(false, false, default)) Assert.Equal(UpdateFailure.BackupStillRunning, waiting.Failure);
        Assert.True(backup.Status.IsAttemptActive); call.Complete(); await BackupCoordinatorTests.Until(backup, s => !s.IsAttemptActive);
        Assert.Equal(BackupOutcome.Completed, backup.Status.LastAttempt!.Result.Outcome);
        using var idle = await update.AcquireAsync(false, false, default); Assert.Equal(UpdateFailure.None, idle.Failure);
    }
    private sealed class Allowed : IBackupSafeguard { public SafeguardDecision Evaluate(BackupConfiguration configuration) => SafeguardDecision.Allow; }
    [Fact]
    public async Task SuccessfulHandoffRetainsAdmissionUntilAgentShutdownOwnerReleasesIt()
    {
        var gate = new BackupOperationGate(); var admission = new UpdateInstallAdmission(gate, () => false, () => Task.CompletedTask);
        var initial = await admission.AcquireAsync(false, false, default);
        var shutdownOwner = initial.TransferOwnership(); initial.Dispose();
        Assert.True(gate.IsBusy); Assert.Throws<InvalidOperationException>(() => initial.TransferOwnership());
        shutdownOwner.Dispose(); Assert.False(gate.IsBusy);
    }
}
