using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class AgentTests
{
    [Fact]
    public async Task ProtocolTransportsDetachedMetadataDiagnosticsAndRejectsInvalidValues()
    {
        using var fixture = new Fixture(); await fixture.Store.SaveAsync(fixture.Settings);
        await using var agent = fixture.Create(); await agent.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var values = new MetadataMaintenanceSummary[] { new(MetadataMaintenanceState.Compacted, true, 1000, 900, 100, 75, 3, 1),
            new((MetadataMaintenanceState)999), new(MetadataMaintenanceState.BelowThreshold, ActiveDecodedBytes: -1),
            new(MetadataMaintenanceState.Compacted, ActiveDecodedBytes: 1, ReclaimableDecodedBytes: 2),
            new(MetadataMaintenanceState.Compacted, RetiredDeferred: -1) };
        foreach (var summary in values)
        {
            var attempt = new AttemptSnapshot(AttemptTrigger.Manual, null, now, now, TimeSpan.Zero,
                new(BackupOutcome.Completed, MetadataMaintenance: summary));
            var state = agent.State with { Backup = agent.State.Backup! with { LastAttempt = attempt } };
            using var stream = new MemoryStream(); await AgentProtocol.WriteAsync(stream, new AgentReply(AgentError.None, state), true, default);
            stream.Position = 0;
            if (ReferenceEquals(summary, values[0])) Assert.Equal(summary, (await AgentProtocol.ReadAsync<AgentReply>(stream, true, default)).State.Backup!.LastAttempt!.Result.MetadataMaintenance);
            else await Assert.ThrowsAsync<InvalidDataException>(() => AgentProtocol.ReadAsync<AgentReply>(stream, true, default));
        }
    }

    [Fact]
    public async Task ProtocolTransportsEveryPhaseAndRejectsInvalidProgress()
    {
        using var fixture = new Fixture(); await fixture.Store.SaveAsync(fixture.Settings);
        await using var agent = fixture.Create(); await agent.InitializeAsync();
        foreach (var phase in Enum.GetValues<BackupPhase>())
        {
            var progress = new BackupProgress(phase, 5, 10);
            var state = agent.State with { Backup = agent.State.Backup! with { Progress = progress } };
            using var stream = new MemoryStream(); await AgentProtocol.WriteAsync(stream, new AgentReply(AgentError.None, state), true, default);
            stream.Position = 0; var reply = await AgentProtocol.ReadAsync<AgentReply>(stream, true, default);
            Assert.Equal(progress, reply.State.Backup!.Progress);
        }
        foreach (var progress in new BackupProgress[] { new((BackupPhase)999), new(BackupPhase.Scanning, -1, 5),
            new(BackupPhase.Scanning, 1, 0), new(BackupPhase.Scanning, 6, 5), new(BackupPhase.Scanning, null, 5) })
        {
            using var stream = new MemoryStream();
            await AgentProtocol.WriteAsync(stream, new AgentReply(AgentError.None, agent.State with { Backup = agent.State.Backup! with { Progress = progress } }), true, default);
            stream.Position = 0; await Assert.ThrowsAsync<InvalidDataException>(() => AgentProtocol.ReadAsync<AgentReply>(stream, true, default));
        }
        var saved = File.ReadAllBytes(fixture.Store.FilePath);
        Assert.DoesNotContain("progress", System.Text.Encoding.UTF8.GetString(saved), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BackupProgressTests
{
    [Fact]
    public async Task FloodCoalescesIntoOnePendingProgressAndCompletionCancelsIt()
    {
        var time = new ManualSchedulerTime(BackupCoordinatorTests.Anchor);
        var runner = new BackupCoordinatorTests.ControlledRunner();
        await using var coordinator = new BackupCoordinator(BackupCoordinatorTests.Configuration(),
            new(TimeSpan.FromHours(1), BackupCoordinatorTests.Anchor), runner, new Allowed(), time);
        Assert.True(coordinator.TryRunNow()); var call = await runner.Next();
        var revision = coordinator.Status.Revision;
        for (var i = 0; i < 10_000; i++) runner.Progress!(new(i % 2 == 0 ? BackupPhase.Maintaining : BackupPhase.CheckingMetadata, i, 10_000));
        Assert.Equal(revision, coordinator.Status.Revision);
        time.Advance(TimeSpan.FromMilliseconds(249)); Assert.Equal(BackupPhase.Scanning, coordinator.Status.Progress.Phase);
        time.Advance(TimeSpan.FromMilliseconds(1)); Assert.Equal(revision + 1, coordinator.Status.Revision);
        Assert.Equal(new(BackupPhase.CheckingMetadata, 9999, 10_000), coordinator.Status.Progress);
        runner.Progress!(new(BackupPhase.CompactingMetadata)); call.Complete();
        await BackupCoordinatorTests.Until(coordinator, s => !s.IsAttemptActive);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(new(BackupPhase.Finished), coordinator.Status.Progress);
    }
    private sealed class Allowed : IBackupSafeguard { public SafeguardDecision Evaluate(BackupConfiguration configuration) => SafeguardDecision.Allow; }
}
