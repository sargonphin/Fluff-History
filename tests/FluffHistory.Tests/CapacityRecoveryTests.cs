using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class CapacityRecoveryTests
{
    [Theory]
    [InlineData("before")] [InlineData("uncertain")] [InlineData("cleanup-control")] [InlineData("probe")]
    public void ErrorsCarryExactConfirmedProgressAndRequireFreshReconciliationWhereNeeded(string fault)
    {
        using var f = new HistoryFixture(); for (int i = 0; i < 6; i++) f.Add(day: i);
        var probe = CapacityRetentionTests.Pressure(); probe.BeforeObserve = (i, _) => { if (fault == "probe" && i == 3) throw new IOException("Probe failed after first cleanup"); };
        var engine = CapacityRetentionTests.Engine(f.Repository, probe, batch: 2); int publications = 0;
        engine.WriterAcquired = writer =>
        {
            writer.Observe = stage =>
            {
                if (stage == PublicationStage.BeforeReplace && ++publications == 2 && fault == "before") throw new IOException("Injected publication failure");
                if (stage == PublicationStage.AfterReplace && publications == 2 && fault == "uncertain") writer.LoseHandleForTest();
            };
            writer.PayloadCleanupObserve = (stage, _) => { if (stage == PayloadCleanupStage.BeforeDelete && publications == 2 && fault == "cleanup-control") throw new InvalidDataException("Injected cleanup control error"); };
        };
        var error = Assert.Throws<CapacityRetentionException>(() => engine.EnsureCapacity(0)); var result = error.Result;
        Assert.Equal(CapacityRetentionOutcome.ControlError, result.Outcome); Assert.True(result.Partial);
        Assert.Equal(fault == "cleanup-control" ? 4 : 2, result.Removed); Assert.Equal(fault == "cleanup-control" ? 2 : 1, result.BatchesCommitted);
        Assert.Equal(2, result.Cleanup.Deleted); Assert.Equal(0, result.LatestObservation!.Free);
        if (fault == "uncertain") Assert.IsType<PublicationUncertainException>(error.InnerException);
        if (fault == "cleanup-control") Assert.IsType<VersionRemovalCommittedException>(error.InnerException);
        using (var writer = f.Repository.OpenWriter())
        {
            foreach (var tx in writer.EnumeratePendingPublications().ToArray()) Assert.Equal(fault == "before" ? PublicationOutcome.Unpublished : PublicationOutcome.Published, writer.Reconcile(tx));
            Assert.Equal(fault is "uncertain" or "cleanup-control" ? 2 : 0, writer.CleanupRetiredPayloads().Result.Deleted);
        }
        var rest = CapacityRetentionTests.Engine(f.Repository).EnsureCapacity(0); Assert.Equal(fault is "uncertain" or "cleanup-control" ? 1 : 3, rest.Removed);
    }

    [Theory]
    [InlineData("capacity-derived", 0)] [InlineData("capacity-cleaned", 2)]
    public async Task ProcessDeathDiscardsOrderingAndCapacityTruthButPreservesConfirmedRemovals(string mode, int removed)
    {
        using var f = new HistoryFixture(); for (int i = 0; i < 6; i++) f.Add(day: i);
        var work = Path.Combine(Path.GetDirectoryName(f.Repository.DirectoryPath)!, "capacity-work");
        await using (var child = WriterProcess.Start(mode, f.Repository.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        var stale = Assert.Single(Directory.GetDirectories(work, "FluffHistory-capacity-*")); var evidence = TemporaryDirectory.Snapshot(stale);
        using (var reader = f.Repository.OpenReader()) Assert.Equal(6 - removed, reader.EnumerateVersions(new(FoundationTests.Root, "a")).Count());
        using (var writer = f.Repository.OpenWriter()) { Assert.Empty(writer.EnumeratePendingPublications()); Assert.Equal(removed, writer.CleanupRetiredPayloads().Result.AlreadyAbsent); }
        var result = CapacityRetentionTests.Engine(f.Repository, workspace: work).EnsureCapacity(0);
        Assert.Equal(5 - removed, result.Removed); Assert.Equal(1, result.HistoryScans); Assert.True(result.CapacityObservations >= 3);
        Assert.Equal(evidence, TemporaryDirectory.Snapshot(stale)); Assert.Equal(stale, Assert.Single(Directory.GetDirectories(work, "FluffHistory-capacity-*")));
    }

    [Fact]
    public void CurrentConfigurationCorruptionStopsBeforeRemovalAndUnresolvedCandidatesBlockExecution()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1);
        RetentionProjectionTests.Corrupt(f.Repository, (reader, _) => reader.Captured.Root with { Sources = null });
        var before = RetentionTests.Inventory(f.Repository); var error = Assert.Throws<CapacityRetentionException>(() => CapacityRetentionTests.Engine(f.Repository).EnsureCapacity(0));
        Assert.IsType<InvalidDataException>(error.InnerException); Assert.Equal(0, error.Result.Removed); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
        using var other = new HistoryFixture(); other.Add();
        using (var writer = other.Repository.OpenWriter())
        {
            writer.Observe = stage => { if (stage == PublicationStage.CandidateFlushed) throw new IOException("Injected"); };
            using var reader = other.Repository.OpenReader(); Assert.Throws<IOException>(() => writer.Apply(new(sourceRoots: [new(Guid.NewGuid(), "other")]), reader.Authority));
        }
        var blocked = Assert.Throws<CapacityRetentionException>(() => CapacityRetentionTests.Engine(other.Repository).EnsureCapacity(0));
        Assert.Equal(0, blocked.Result.HistoryScans); Assert.Equal(1, blocked.Result.CapacityObservations); Assert.Equal(0, blocked.Result.Removed);
    }

    [Fact]
    public void TimelineSupportAndSurvivorsRemainExactThroughRebuildAndCompaction()
    {
        using var f = new HistoryFixture(); var old = f.Add("a"); var b = f.Add("b"); var current = f.Add("a", day: 1);
        var probe = new ScriptedCapacityProbe(new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 5));
        CapacityRetentionTests.Engine(f.Repository, probe).EnsureCapacity(0);
        using (var reader = f.Repository.OpenReader()) { Assert.Null(reader.GetVersion(old.Identity)); Assert.Contains(FoundationTests.Time, reader.GetTimelinePage(new(FoundationTests.Root)).Items); }
        f.Remove("b"); CapacityRetentionTests.Engine(f.Repository).EnsureCapacity(0);
        using (var writer = f.Repository.OpenWriter()) { writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes(); }
        using var fresh = f.Repository.OpenReader(); Assert.Null(fresh.GetVersion(b.Identity)); Assert.Equal(current, fresh.GetCurrent(current.Identity.Path));
        Assert.Equal(new[] { current.StoredUtc }, fresh.GetTimelinePage(new(FoundationTests.Root)).Items); Assert.Equal(4, RepositoryIdentity.FormatVersion); Assert.Equal(5, Enum.GetValues<SemanticKind>().Length);
    }

    [WindowsFact]
    public void DeferredCapacityCleanupAnchorSurvivesCompactionUntilIndependentRetry()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        using (var reader = f.Repository.OpenReader()) using (var held = reader.OpenVersionPayload(old.Identity))
        {
            var result = CapacityRetentionTests.Engine(f.Repository).EnsureCapacity(0); Assert.Equal(1, result.Cleanup.Deferred);
            using var writer = f.Repository.OpenWriter(); writer.RebuildIndexes(); writer.CompactMetadata(); Assert.Equal(1, VersionRemovalTests.Tombstones(f.Repository));
        }
        using var recovery = f.Repository.OpenWriter(); Assert.Equal(1, recovery.CleanupRetiredPayloads().Result.Deleted); recovery.CompactMetadata(); recovery.RebuildIndexes(); Assert.Equal(0, VersionRemovalTests.Tombstones(f.Repository));
    }
}
