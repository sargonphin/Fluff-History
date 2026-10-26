using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class RetentionRecoveryTests
{
    [Theory]
    [InlineData("failure")] [InlineData("uncertain")] [InlineData("cleanup-control")]
    public void FailureAfterUsefulProgressReportsOnlyConfirmedBatchesAndNeverRetries(string fault)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 5; i++) f.Add(day: i);
        var engine = new RetentionEngine(f.Repository, new(BatchSize: 2)); var preview = RetentionTests.Preview(f.Repository);
        int publications = 0;
        engine.WriterAcquired = writer =>
        {
            writer.Observe = stage =>
            {
                if (stage == PublicationStage.BeforeReplace && ++publications == 2 && fault == "failure") throw new IOException("Injected second batch failure");
                if (stage == PublicationStage.AfterReplace && publications == 2 && fault == "uncertain") writer.LoseHandleForTest();
            };
            writer.PayloadCleanupObserve = (stage, _) => { if (fault == "cleanup-control" && publications == 2 && stage == PayloadCleanupStage.BeforeDelete) throw new InvalidDataException("Injected cleanup control failure"); };
        };
        var error = Assert.Throws<RetentionExecutionException>(() => engine.Execute(preview)); var result = error.Result;
        Assert.True(result.Partial); Assert.False(result.Complete); Assert.Equal(fault == "cleanup-control" ? 4 : 2, result.Removed);
        Assert.Equal(fault == "cleanup-control" ? 2 : 1, result.BatchesCommitted); Assert.Equal(2, result.Cleanup.Deleted);
        if (fault == "uncertain") Assert.IsType<PublicationUncertainException>(error.InnerException);
        if (fault == "cleanup-control") Assert.IsType<VersionRemovalCommittedException>(error.InnerException);
        using (var writer = f.Repository.OpenWriter())
        {
            foreach (var tx in writer.EnumeratePendingPublications().ToArray()) Assert.Equal(fault == "failure" ? PublicationOutcome.Unpublished : PublicationOutcome.Published, writer.Reconcile(tx));
            var cleanup = writer.CleanupRetiredPayloads().Result;
            Assert.Equal(fault == "failure" ? 0 : 2, cleanup.Deleted);
        }
        engine.WriterAcquired = null; var rest = engine.Execute(RetentionTests.Preview(f.Repository)); Assert.Equal(fault == "failure" ? 2 : 0, rest.Removed);
    }

    [Fact]
    public async Task ProcessDeathBetweenRetentionBatchesLeavesCommittedProgressAndFreshExecutionFinishes()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 6; i++) f.Add(day: i);
        await using (var child = WriterProcess.Start("retention-between-batches", f.Repository.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        var preview = RetentionTests.Preview(f.Repository); Assert.Equal(4, preview.Examined); Assert.Equal(3, preview.Eligible);
        using (var writer = f.Repository.OpenWriter()) { Assert.Empty(writer.EnumeratePendingPublications()); Assert.Equal(2, writer.CleanupRetiredPayloads().Result.AlreadyAbsent); }
        var result = new RetentionEngine(f.Repository, new(BatchSize: 2)).Execute(preview); Assert.Equal(3, result.Removed); Assert.Equal(2, result.BatchesCommitted);
        using var reader = f.Repository.OpenReader(); Assert.Single(reader.EnumerateVersions(new(FoundationTests.Root, "a")));
    }

    [Fact]
    public void UnresolvedPublicationIsRefusedBeforeRetentionScanOrCleanup()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var preview = RetentionTests.Preview(f.Repository);
        using (var writer = f.Repository.OpenWriter())
        {
            writer.Observe = stage => { if (stage == PublicationStage.CandidateFlushed) throw new IOException("Injected"); };
            using var reader = f.Repository.OpenReader(); Assert.Throws<IOException>(() => writer.Apply(new(sourceRoots: [new(Guid.NewGuid(), "other")]), reader.Authority));
        }
        var before = RetentionTests.Inventory(f.Repository);
        Assert.Throws<IOException>(() => new RetentionEngine(f.Repository).Execute(preview)); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }
}
