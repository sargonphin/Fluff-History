using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests;

public sealed class LiveCapacityTests
{
    [Theory]
    [InlineData(2, 0)] [InlineData(3, 1)]
    public void ExternalSpaceImprovementStopsBeforeDeletion(int adequateProbe, long historyScans)
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); f.Seed(content: "newer", day: 1); f.Probe.Free = 0;
        f.Probe.Before = (_, call) => { if (call >= adequateProbe) f.Probe.Free = 900_000_000; };
        var result = f.Scan(f.Engine()); Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(historyScans, result.Capacity.HistoryScans);
        Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(CapacityRetentionOutcome.AlreadySatisfied, result.Capacity.LatestRetentionOutcome);
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
    }

    [Fact]
    public void CapacityChangeMakesNextPayloadImpossibleBeforeContent()
    {
        using var f = new LiveCapacityFixture(); f.File();
        f.Access.BeforeObserve = path => { if (Path.GetFileName(path) == "a") { f.Probe.Capacity = 1000; f.Probe.Free = 1000; } };
        var result = f.Scan(f.Engine()); Assert.Equal(ScanOutcome.CapacityTargetImpossible, result.Outcome);
        Assert.Equal(1000, result.Capacity.LatestObservation!.Capacity); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, result.Capacity.HistoryScans);
    }
    [Theory]
    [InlineData(RetentionPolicy.Forever, false)] [InlineData(RetentionPolicy.OneMonth, false)] [InlineData(RetentionPolicy.TwoYears, false)]
    [InlineData(RetentionPolicy.Forever, true)] [InlineData(RetentionPolicy.OneMonth, true)] [InlineData(RetentionPolicy.TwoYears, true)]
    public void PreservePoliciesBlockBeforeContentAndNeverRemoveHistory(RetentionPolicy policy, bool encrypted)
    {
        using var f = new LiveCapacityFixture(encrypted); var old = f.Seed(); f.File(content: "changed", day: 1);
        f.Probe.Free = 0; f.Access.BeforeOpen = _ => throw new InvalidOperationException("Blocked content opened");
        int drafts = 0; var engine = f.Engine(policy); engine.ConfigureWriter = w => w.PayloadObserve = (stage, _) => { if (stage == PayloadStage.Created) drafts++; };
        var before = RetentionTests.Inventory(f.Repository); var result = f.Scan(engine);
        Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome); Assert.Equal(0, result.SourceSkips); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, drafts);
        Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(0, result.Capacity.RetentionInvocations); Assert.Equal(0, result.Capacity.HistoryScans);
        Assert.Equal(before, RetentionTests.Inventory(f.Repository)); using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void PreserveHasNoReserveAndApprovesPayloadAndPublicationBeforeOneContentStream(bool encrypted)
    {
        using var f = new LiveCapacityFixture(encrypted); f.File();
        using (var writer = f.Repository.OpenWriter(null, streamMetadataReads: true)) f.Probe.Free = writer.EstimatePublicationAllocation(new(Versions: 1), 4096).TotalBytes + 4096;
        f.Access.BeforeOpen = _ => Assert.True(f.Probe.Calls > 0);
        var result = f.Scan(f.Engine(RetentionPolicy.Forever)); Assert.Equal(ScanOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(5, result.SourceBytesRead); Assert.Equal(5, result.PayloadBytesWritten); Assert.Equal(1, result.VersionsCreated);
        Assert.False(result.Capacity.ReclamationAuthorized); Assert.Null(result.Capacity.LatestTarget); Assert.Equal(0, result.Capacity.HistoryScans);
        Assert.Equal(4096, result.Capacity.MaximumEstimate!.UpcomingPayloadBytes); Assert.True(result.Capacity.MaximumEstimate.PublicationBytes > 0);
        Assert.Equal(result.Capacity.MaximumPublicationAllocation, result.Capacity.PublicationComponentMaxima!.TotalBytes);
        Assert.True(result.Capacity.PublicationComponentMaxima.IndexBytes > 0);
        using var reader = f.Repository.OpenReader(); Assert.Equal("26C7827D889F6DA3", reader.GetNewestVersion(new(FoundationTests.Root, "a"))!.Checksum.Value);
    }

    [Fact]
    public void NoChangeUntilSaturationIsOneCheapInitialProbeAndNoWritesOrHistory()
    {
        using var f = new LiveCapacityFixture(); f.Seed(); var inventory = RetentionTests.Inventory(f.Repository);
        f.Access.BeforeOpen = _ => throw new InvalidOperationException("Unchanged content opened"); var result = f.Scan(f.Engine());
        Assert.Equal(1, result.Unchanged); Assert.Equal(1, result.Capacity.Checks); Assert.Equal(1, result.Capacity.RetentionInvocations);
        Assert.Equal(0, result.Capacity.HistoryScans); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, result.BatchesCommitted);
        Assert.Equal(1, result.WriterAcquisitions); Assert.Equal(1, result.CurrentComparisonScans); Assert.Equal(inventory, RetentionTests.Inventory(f.Repository));
    }

    [Fact]
    public void InitialPressureRetainsCurrentAndCapturesBaselineAfterRemoval()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); var current = f.Seed(content: "newer", day: 1);
        f.Probe.Free = 0;
        f.Probe.Before = (repo, _) => { using var reader = repo.OpenReader(); if (reader.GetVersion(old.Identity) is null) f.Probe.Free = 900_000_000; };
        var result = f.Scan(f.Engine()); Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(1, result.Capacity.VersionsRemoved);
        Assert.Equal(1, result.Capacity.RemovalBatches); Assert.Equal(1, result.Capacity.HistoryScans); Assert.Equal(1, result.Unchanged);
        Assert.Equal(result.Authority, result.ComparisonAuthority); Assert.Equal(0, result.HistoricalReaderRefreshes);
        using var fresh = f.Repository.OpenReader(); Assert.Equal(current, fresh.GetCurrent(current.Identity.Path)); Assert.Null(fresh.GetVersion(old.Identity));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapacityInvalidatesMatchingHistoryAndReobservesSourceBeforeOrdinaryCopy(bool mutateSource)
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(current: false); var path = Path.Combine(f.Source, "a");
        f.Probe.Free = f.Probe.Capacity / 20; // B=0 fits; metadata-only reactivation B does not.
        bool changed = false;
        f.Probe.Before = (repo, _) =>
        {
            using var reader = repo.OpenReader();
            if (reader.GetVersion(old.Identity) is null)
            {
                f.Probe.Free = 900_000_000;
                if (mutateSource && !changed) { f.File(content: "longer-after-maintenance", day: 2); changed = true; }
            }
        };
        var result = f.Scan(f.Engine()); Assert.Equal(ScanOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Reactivations); Assert.Equal(1, result.VersionsCreated); Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(1, result.Capacity.VersionsRemoved);
        Assert.Equal(1, result.HistoricalReaderRefreshes); Assert.Equal(1, result.CurrentComparisonScans); Assert.Equal(0, result.ComparisonEntries); Assert.True(result.HistoricalLookups >= 2);
        Assert.Equal(new FileInfo(path).Length, result.SourceBytesRead); Assert.Equal(result.SourceBytesRead, result.PayloadBytesWritten);
        using var fresh = f.Repository.OpenReader(); Assert.Null(fresh.GetVersion(old.Identity)); Assert.NotEqual(old.Identity, fresh.GetCurrent(old.Identity.Path)!.Identity);
    }

    [Fact]
    public void SurvivingHistoryReactivatesWithoutPayloadButWithPublicationHeadroom()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(current: false);
        var result = f.Scan(f.Engine()); Assert.Equal(1, result.Reactivations); Assert.Equal(0, result.ContentStreamsOpened);
        Assert.True(result.Capacity.MaximumUpcomingAllocation > 0); Assert.Equal(0, result.Capacity.MaximumPayloadAllocation);
        using var reader = f.Repository.OpenReader(); Assert.Equal(old.Identity, reader.GetCurrent(old.Identity.Path)!.Identity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PendingReactivationMustPublishSafelyBeforePressureCanReclaim(bool finalProbeFails)
    {
        using var f = new LiveCapacityFixture(); var historical = f.Seed("a", current: false); f.File("b", "new");
        long reactivationHeadroom; using (var writer = f.Repository.OpenWriter(null, streamMetadataReads: true)) reactivationHeadroom = writer.EstimatePublicationAllocation(new(Upserts: 1), 4096).TotalBytes;
        bool pressure = false;
        f.Access.BeforeObserve = path => { if (Path.GetFileName(path) == "b") { pressure = true; f.Probe.Free = 50_000_000 + reactivationHeadroom; } };
        f.Probe.Before = (repo, _) =>
        {
            if (!pressure) return;
            using var reader = repo.OpenReader();
            if (reader.GetCurrent(historical.Identity.Path) is not null) f.Probe.Free = 900_000_000;
            else if (finalProbeFails && f.Probe.Calls >= 4) f.Probe.Free = 0;
        };
        var result = f.Scan(f.Engine());
        Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(0, result.Capacity.HistoryScans);
        using var fresh = f.Repository.OpenReader(); Assert.NotNull(fresh.GetVersion(historical.Identity));
        if (finalProbeFails)
        { Assert.True(result.CapacityBlocked); Assert.Equal(0, result.Reactivations); Assert.Null(fresh.GetCurrent(historical.Identity.Path)); Assert.Equal(0, result.ContentStreamsOpened); }
        else
        { Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(1, result.Reactivations); Assert.Equal(1, result.VersionsCreated); Assert.Equal(historical.Identity, fresh.GetCurrent(historical.Identity.Path)!.Identity); }
    }

    [Theory]
    [InlineData(128, 10, 6, 3, 1, 6)] [InlineData(2, 100, 6, 5, 2, 12)] [InlineData(128, 10, 25, 2, 1, 25)]
    public void ByteCountAndLargeSingleFileBounds(int batch, long target, int size, int files, long peakCount, long peakBytes)
    {
        using var f = new LiveCapacityFixture(); for (var i = 0; i < files; i++) f.File($"f{i}", new string('x', size));
        var result = f.Scan(f.Engine(batch: batch, byteTarget: target));
        Assert.Equal(files, result.VersionsCreated); Assert.Equal(peakCount, result.PeakPendingPayloadCount); Assert.Equal(peakBytes, result.PeakPendingPayloadBytes);
        Assert.Equal((files + (int)peakCount - 1) / (int)peakCount, result.BatchesCommitted);
        Assert.Equal((long)files * size, result.SourceBytesRead); Assert.Equal(result.SourceBytesRead, result.PayloadBytesWritten); Assert.True(result.Capacity.MaximumPayloadAllocation >= size);
    }

    [Fact]
    public void FinalProbeLossStopsWithPendingDraftAndNeverRunsRetentionBeneathIt()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); f.File(content: "changed", day: 1);
        var engine = f.Engine(); engine.ConfigureWriter = w => w.PayloadObserve = (stage, _) => { if (stage == PayloadStage.BeforeFlush) f.Probe.Free = 0; };
        var result = f.Scan(engine); Assert.True(result.CapacityBlocked); Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(0, result.VersionsCreated);
        Assert.Equal(0, result.Capacity.HistoryScans); Assert.Equal(0, result.Capacity.VersionsRemoved);
        // M17 preserves only this fully stable unpublished copy as optional Ready cache.
        var part = Assert.Single(Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories));
        Assert.Equal("changed", File.ReadAllText(part));
        Assert.Single(Directory.GetFiles(f.Repository.DirectoryPath, "*.ready", SearchOption.AllDirectories));
        Assert.Equal(1, result.Reuse.ReadyDraftsPreserved);
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
    }

    [Fact]
    public void PostCommitReserveLossDelegatesAtCleanBoundaryAndPropagatesAuthority()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed("a"); f.File("a", "changed", 1); f.File("b", "next");
        var engine = f.Engine(batch: 1); var authorities = new List<AuthorityIdentity>();
        engine.BatchCommitted = r => { authorities.Add(r.Authority); if (r.VersionsCreated == 1) f.Probe.Free = 0; };
        f.Probe.Before = (repo, _) => { using var reader = repo.OpenReader(); if (reader.GetVersion(old.Identity) is null) f.Probe.Free = 900_000_000; };
        var result = f.Scan(engine); Assert.Equal(2, result.VersionsCreated); Assert.Equal(1, result.Capacity.VersionsRemoved); Assert.Equal(1, result.Capacity.HistoryScans);
        Assert.Equal(2, authorities.Count); Assert.Equal(authorities[0].Generation + 2, authorities[1].Generation);
        Assert.Equal(authorities[^1], result.Authority); Assert.Equal(1, result.WriterAcquisitions); Assert.Equal(1, result.CurrentComparisonScans); Assert.Equal(1, result.HistoricalReaderRefreshes);
    }

    [Theory]
    [InlineData("remove")] [InlineData("reactivate")] [InlineData("configure")]
    public void MetadataOnlyMutationsNeedCapacityBeforePreparation(string operation)
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(current: operation != "reactivate");
        if (operation == "remove") System.IO.File.Delete(Path.Combine(f.Source, "a"));
        var selection = operation == "configure" ? new SourceSelection(new(FoundationTests.Root, f.Source, maximumFileSize: 100), f.Source) : f.Selection;
        f.Probe.Free = 0; var engine = f.Engine(RetentionPolicy.Forever); var before = RetentionTests.Inventory(f.Repository);
        var result = engine.Scan([selection]); Assert.True(result.CapacityBlocked); Assert.True(result.Capacity.MaximumPublicationAllocation > 0); Assert.Equal(0, result.Capacity.MaximumPayloadAllocation);
        Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, result.BatchesCommitted); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
    }

    [Fact]
    public void ConfigurationAndSourceBatchesAdvanceTheSameConfirmedAuthority()
    {
        using var f = new LiveCapacityFixture(); f.File("a"); f.File("b");
        var selected = new SourceSelection(new(FoundationTests.Root, f.Source, maximumFileSize: 100), f.Source);
        using var initial = f.Repository.OpenReader(); var engine = f.Engine(batch: 1);
        var result = engine.Scan([selected]); Assert.Equal(ScanOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.BatchesCommitted); Assert.Equal(2, result.VersionsCreated);
        Assert.Equal(initial.Authority.Generation + 3, result.Authority.Generation);
        Assert.Equal(1, result.CurrentComparisonScans); Assert.Equal(1, result.WriterAcquisitions); Assert.Equal(0, result.Capacity.HistoryScans);
        using var fresh = f.Repository.OpenReader(); Assert.Equal(result.Authority, fresh.Authority); Assert.Equal(selected.Configuration, fresh.GetSourceRoot(FoundationTests.Root));
    }
}
