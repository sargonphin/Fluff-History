using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class HistoryCleanupRecoveryTests
{
    [Fact]
    public async Task ProcessDeathBetweenBatchesNeedsOnlyFreshPreviewAndCommittedEvidence()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 6; i++) f.Add(day: i);
        await using (var child = WriterProcess.Start("manual-cleanup-between-batches", f.Repository.DirectoryPath))
        { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        var preview = HistoryCleanupTests.Preview(f.Repository); Assert.Equal(4, preview.Examined); Assert.Equal(3, preview.Eligible);
        using (var writer = f.Repository.OpenWriter()) { Assert.Empty(writer.EnumeratePendingPublications()); Assert.Equal(2, writer.CleanupRetiredPayloads().Result.AlreadyAbsent); }
        Assert.Equal(3, new HistoryCleanupEngine(f.Repository).Execute(preview).Removed);
        using var reader = f.Repository.OpenReader(); Assert.Single(reader.EnumerateVersions(new(FoundationTests.Root, "a")));
    }

    [Theory]
    [InlineData("backup")] [InlineData("membership")] [InlineData("configuration")] [InlineData("retention")]
    [InlineData("manual")] [InlineData("capacity")] [InlineData("rebuild")] [InlineData("compaction")]
    public void EveryExternalAuthorityChangeRefusesStalePreviewBeforeDestruction(string change)
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1); var engine = new HistoryCleanupEngine(f.Repository); var preview = engine.Preview(HistoryCleanupTests.Latest);
        switch (change)
        {
            case "backup": f.Add("new"); break;
            case "membership": f.Remove("a"); break;
            case "retention": new RetentionEngine(f.Repository).Execute(RetentionTests.Preview(f.Repository)); break;
            case "manual": engine.Execute(preview); break;
            case "capacity": new CapacityRetentionEngine(f.Repository, probe: new Pressure()).EnsureCapacity(0); break;
            default:
                using (var writer = f.Repository.OpenWriter())
                {
                    if (change == "rebuild") writer.RebuildIndexes();
                    else if (change == "compaction") writer.CompactMetadata();
                    else using (var reader = f.Repository.OpenReader()) writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "changed")]), reader.Authority);
                }
                break;
        }
        var before = RetentionTests.Inventory(f.Repository);
        var stale = Assert.Throws<StaleHistoryCleanupPreviewException>(() => engine.Execute(preview)); Assert.Equal(preview.Authority, stale.PreviewAuthority);
        Assert.NotEqual(stale.PreviewAuthority, stale.ActualAuthority); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
        if (change is not "retention" and not "manual" and not "capacity") Assert.True(File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId)));
    }
    private sealed class Pressure : IRepositoryCapacityProbe
    { public CapacityObservation Observe(Repository repository) => new(100, 0); }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ExecutionRegeneratesTruthOneScanAndOwnAuthorityAdvancement(bool latest)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 8; i++) f.Add(day: i);
        var engine = new HistoryCleanupEngine(f.Repository, new(BatchSize: 2)); var preview = HistoryCleanupTests.Preview(f.Repository, latest);
        preview.Eligible = 999; preview.EligibleLogicalBytes = long.MaxValue; preview.Examined = 0; preview.AffectedPaths = 999;
        var authorities = new List<AuthorityIdentity>(); engine.BatchCommitted = r => authorities.Add(r.FinalConfirmedAuthority);
        var result = engine.Execute(preview); Assert.True(result.Complete); Assert.Equal(8, result.Examined); Assert.Equal(1, result.Protected);
        Assert.Equal(1, result.PathsExamined); Assert.Equal(1, result.AffectedPaths); Assert.Equal(7, result.Eligible); Assert.Equal(7, result.Removed);
        Assert.Equal(35, result.RemovedLogicalBytes); Assert.Equal(7, result.Cleanup.Deleted); Assert.Equal(4, result.BatchesCommitted); Assert.Equal(1, result.HistoryScans);
        Assert.Equal(8, result.Reads!.VersionsExamined); Assert.Equal(2, result.MaximumBatchCount); Assert.InRange(result.MaximumBatchBytes, 1, 4 * 1024 * 1024);
        Assert.Equal(Enumerable.Range(1, 4).Select(i => preview.Authority.Generation + i), authorities.Select(a => a.Generation));
        Assert.Equal(preview.Authority, result.StartingAuthority); Assert.Equal(authorities[^1], result.FinalConfirmedAuthority);
        Assert.True(result.VersionPagesWritten > 0); Assert.True(result.MetadataBytesAppended > 0);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CancellationBeforeAndAfterConfirmedProgressAndFreshCompletion(bool latest)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 6; i++) f.Add(day: i);
        var engine = new HistoryCleanupEngine(f.Repository, new(BatchSize: 2)); var preview = HistoryCleanupTests.Preview(f.Repository, latest);
        var cancelledPreview = engine.Preview(preview.Evaluation, new(true)); Assert.Throws<InvalidOperationException>(() => engine.Execute(cancelledPreview));
        var before = RetentionTests.Inventory(f.Repository); var none = engine.Execute(preview, new(true)); Assert.True(none.Cancelled); Assert.Equal(0, none.Removed); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
        using var cts = new CancellationTokenSource(); engine.BatchCommitted = _ => cts.Cancel(); var result = engine.Execute(preview, cts.Token);
        Assert.True(result.Cancelled); Assert.True(result.Partial); Assert.Equal(2, result.Removed); Assert.Equal(1, result.BatchesCommitted);
        engine.BatchCommitted = null; var rest = engine.Execute(HistoryCleanupTests.Preview(f.Repository, latest)); Assert.True(rest.Complete); Assert.Equal(3, rest.Removed);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void WorkLimitDropsUncommittedBatchAndKeepsConfirmedProgress(bool latest)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 7; i++) f.Add(day: i);
        var engine = new HistoryCleanupEngine(f.Repository, new(BatchSize: 2, MaximumExaminedVersions: latest ? 4 : 3));
        var result = engine.Execute(HistoryCleanupTests.Preview(f.Repository, latest)); Assert.True(result.BudgetExceeded); Assert.True(result.Partial);
        Assert.Equal(3, result.Eligible); Assert.Equal(2, result.Removed); Assert.Equal(1, result.BatchesCommitted);
        Assert.Equal(4, HistoryCleanupTests.Preview(f.Repository, latest).Eligible);
    }

    [Theory]
    [InlineData("failure")] [InlineData("uncertain")] [InlineData("cleanup-control")]
    public void FailurePreservesExactConfirmedProgressAndRequiresExplicitReconciliation(string fault)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 5; i++) f.Add(day: i);
        var engine = new HistoryCleanupEngine(f.Repository, new(BatchSize: 2)); int publications = 0;
        engine.WriterAcquired = writer =>
        {
            writer.Observe = stage =>
            {
                if (stage == PublicationStage.BeforeReplace && ++publications == 2 && fault == "failure") throw new IOException("Injected second batch failure");
                if (stage == PublicationStage.AfterReplace && publications == 2 && fault == "uncertain") writer.LoseHandleForTest();
            };
            writer.PayloadCleanupObserve = (stage, _) => { if (fault == "cleanup-control" && publications == 2 && stage == PayloadCleanupStage.BeforeDelete) throw new InvalidDataException("Injected cleanup control failure"); };
        };
        var error = Assert.Throws<HistoryCleanupExecutionException>(() => engine.Execute(HistoryCleanupTests.Preview(f.Repository)));
        Assert.True(error.Result.Partial); Assert.False(error.Result.Complete); Assert.Equal(fault == "cleanup-control" ? 4 : 2, error.Result.Removed);
        Assert.Equal(fault == "cleanup-control" ? 2 : 1, error.Result.BatchesCommitted); Assert.Equal(2, error.Result.Cleanup.Deleted);
        if (fault == "uncertain") Assert.IsType<PublicationUncertainException>(error.InnerException);
        if (fault == "cleanup-control") Assert.IsType<VersionRemovalCommittedException>(error.InnerException);
        using (var writer = f.Repository.OpenWriter())
        {
            foreach (var tx in writer.EnumeratePendingPublications().ToArray()) Assert.Equal(fault == "failure" ? PublicationOutcome.Unpublished : PublicationOutcome.Published, writer.Reconcile(tx));
            Assert.Equal(fault == "failure" ? 0 : 2, writer.CleanupRetiredPayloads().Result.Deleted);
        }
        engine.WriterAcquired = null; Assert.Equal(fault == "failure" ? 2 : 0, engine.Execute(HistoryCleanupTests.Preview(f.Repository)).Removed);
    }

    [Fact]
    public void UnresolvedAndPendingPublicationBlockExecution()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var preview = HistoryCleanupTests.Preview(f.Repository);
        var engine = new HistoryCleanupEngine(f.Repository);
        engine.WriterAcquired = writer => { using var reader = f.Repository.OpenReader(); writer.PrepareMutation(new(sourceRoots: [new(Guid.NewGuid(), "other")]), reader.Authority); };
        Assert.Throws<IOException>(() => engine.Execute(preview));
        using (var writer = f.Repository.OpenWriter())
            foreach (var tx in writer.EnumeratePendingPublications().ToArray()) Assert.Equal(PublicationOutcome.Unpublished, writer.Reconcile(tx));
        using (var writer = f.Repository.OpenWriter())
        {
            writer.Observe = stage => { if (stage == PublicationStage.CandidateFlushed) throw new IOException("Injected"); };
            using var reader = f.Repository.OpenReader(); Assert.Throws<IOException>(() => writer.Apply(new(sourceRoots: [new(Guid.NewGuid(), "other")]), reader.Authority));
        }
        engine.WriterAcquired = null; var before = RetentionTests.Inventory(f.Repository);
        Assert.Throws<IOException>(() => engine.Execute(preview)); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void HistoryTimelineAndMaintenanceFollowSurvivorsAndCreateNoCleanupTime(bool latest)
    {
        using var f = new HistoryFixture(); var old = f.Add("a"); var shared = f.Add("b"); var survivor = f.Add("a", day: 1);
        var engine = new HistoryCleanupEngine(f.Repository); engine.Execute(HistoryCleanupTests.Preview(f.Repository, latest));
        using (var reader = f.Repository.OpenReader())
        {
            Assert.Null(reader.GetVersion(old.Identity)); Assert.Contains(FoundationTests.Time, reader.GetTimelinePage(new(FoundationTests.Root)).Items);
            var assembled = reader.GetScopeHistoryPage(new(FoundationTests.Root), new HistoryView(FoundationTests.Time));
            Assert.DoesNotContain(assembled.Items, i => i.Version.Identity == old.Identity);
        }
        f.Remove("b");
        if (latest) { engine.Execute(HistoryCleanupTests.Preview(f.Repository)); using var reader = f.Repository.OpenReader(); Assert.Single(reader.EnumerateVersions(shared.Identity.Path)); }
        engine.Execute(engine.Preview(HistoryCleanupTests.Month));
        using (var writer = f.Repository.OpenWriter()) { writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes(); }
        using var fresh = f.Repository.OpenReader(); Assert.Null(fresh.GetVersion(shared.Identity)); Assert.Equal(survivor, fresh.GetCurrent(survivor.Identity.Path));
        var times = fresh.GetTimelinePage(new(FoundationTests.Root)).Items; Assert.Single(times); Assert.DoesNotContain(FoundationTests.Time, times); Assert.DoesNotContain(RetentionTests.Evaluation, times);
        Assert.Equal(4, RepositoryIdentity.FormatVersion); Assert.Equal(5, Enum.GetValues<SemanticKind>().Length);
    }

    [WindowsFact]
    public void HeldPayloadAnchorSurvivesMaintenanceAndIndependentCleanupFinishes()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        using (var reader = f.Repository.OpenReader()) using (var held = reader.OpenVersionPayload(old.Identity))
        {
            var result = new HistoryCleanupEngine(f.Repository).Execute(HistoryCleanupTests.Preview(f.Repository)); Assert.Equal(1, result.Removed); Assert.Equal(1, result.Cleanup.Deferred);
            using var writer = f.Repository.OpenWriter(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes();
            Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deferred);
            using var fresh = f.Repository.OpenReader(); Assert.Null(fresh.GetVersion(old.Identity));
        }
        using var recovery = f.Repository.OpenWriter(); Assert.Equal(1, recovery.CleanupRetiredPayloads().Result.Deleted); recovery.RebuildIndexes(); recovery.CompactMetadata();
    }

    [Fact]
    public void AbsentAndOrdinaryFailedPayloadDeletionAreSeparateFromLogicalRemoval()
    {
        using var f = new HistoryFixture(); var a = f.Add(); var b = f.Add(day: 1); f.Add(day: 2); File.Delete(f.Repository.GetPayloadPath(a.Identity.VersionId));
        var engine = new HistoryCleanupEngine(f.Repository); engine.WriterAcquired = w => w.PayloadCleanupObserve = (stage, _) => { if (stage == PayloadCleanupStage.DeleteAttempt) throw new IOException("ordinary failure"); };
        var result = engine.Execute(HistoryCleanupTests.Preview(f.Repository)); Assert.Equal(2, result.Removed); Assert.Equal(1, result.Cleanup.AlreadyAbsent); Assert.Equal(1, result.Cleanup.Failed);
        using var writer = f.Repository.OpenWriter(); Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deleted); Assert.False(File.Exists(f.Repository.GetPayloadPath(b.Identity.VersionId)));
    }

    [Fact]
    public void LaterCurrentInconsistencyKeepsEarlierConfirmedProgressAndNeverDeletesCurrent()
    {
        using var f = new HistoryFixture(); f.Add("a"); f.Add("a", day: 1); f.Add("a", day: 2);
        var current = f.Add("z"); var newer = f.Add("z", day: -100);
        RetentionProjectionTests.Corrupt(f.Repository, (reader, append) => reader.Captured.Root with {
            Current = new OrderedIndex(IndexKind.Current, a => append.ReadNew(a) ?? reader.Segments.Read(a), append).Update(reader.Captured.Root.Current,
                [(PathKeys.Current(current.Identity.Path), reader.VersionIndex.Find(reader.Captured.Root.Versions, PathKeys.Version(current.Identity))!)]) });
        var engine = new HistoryCleanupEngine(f.Repository, new(BatchSize: 2)); Assert.Throws<InvalidDataException>(() => engine.Preview(HistoryCleanupTests.Latest));
        // Friend-only descriptor simulates corruption discovered after a formerly complete preview.
        // Public callers cannot construct it; execution must still regenerate and fail closed.
        using var captured = f.Repository.OpenReader(); var preview = new HistoryCleanupPreview(captured.Authority, HistoryCleanupTests.Latest) { Complete = true };
        var error = Assert.Throws<HistoryCleanupExecutionException>(() => engine.Execute(preview)); Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(2, error.Result.Removed); Assert.Equal(1, error.Result.BatchesCommitted); Assert.True(error.Result.Partial);
        using var fresh = f.Repository.OpenReader(); Assert.Equal(current, fresh.GetCurrent(current.Identity.Path)); Assert.NotNull(fresh.GetVersion(newer.Identity)); Assert.True(File.Exists(f.Repository.GetPayloadPath(current.Identity.VersionId)));
    }
}
