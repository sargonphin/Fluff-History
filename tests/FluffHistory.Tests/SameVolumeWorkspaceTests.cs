using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class SameVolumeWorkspaceTests
{
    [WindowsFact]
    public void NativeSourceRepositoryAndOwnedWorkspaceOnOneVolumeCompleteNormally()
    {
        using var f = new LiveCapacityFixture(); f.File("one.txt"); var work = f.Temp.Child("work");
        var result = new FileHistoryEngine(f.Repository, new(WorkspaceRoot: work, Capacity: new(RetentionPolicy.Forever))).Scan([f.Selection]);
        Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(1, result.VersionsCreated); Assert.Equal(0, result.Capacity.VersionsRemoved);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CapacityPublicationStreamsOldMetadataAndChargesFutureWorkspaceAppend(bool encrypted)
    {
        using var f = new LiveCapacityFixture(encrypted); f.Seed(); f.File(content: "a new longer version", day: 1);
        var work = f.Temp.Child("work"); var inspected = false;
        var engine = new FileHistoryEngine(f.Repository, new(WorkspaceRoot: work, Capacity: new(RetentionPolicy.Forever)), f.Probe, new AllocationProbe());
        engine.ConfigureWriter = writer => writer.Observe = stage =>
        {
            if (stage != PublicationStage.LocalPrepared) return;
            Assert.Empty(Directory.EnumerateFiles(work, "*.cache", SearchOption.AllDirectories)); inspected = true;
        };
        PublicationAllocationEstimate legacy, bounded;
        using (var writer = f.Repository.OpenWriter()) legacy = writer.EstimatePublicationAllocation(new(Versions: 1), 4096);
        using (var writer = f.Repository.OpenWriter(work, streamMetadataReads: true)) bounded = writer.EstimatePublicationAllocation(new(Versions: 1), 4096);
        Assert.Equal(legacy.TransferAndInstallBytes / 2 * 3, bounded.TransferAndInstallBytes);
        Assert.Equal(legacy.TotalBytes + legacy.TransferAndInstallBytes / 2, bounded.TotalBytes);
        f.Probe.Free = bounded.TotalBytes + 4096; // Existing workspace is not subtracted from actual F again.
        var result = engine.Scan([f.Selection]); Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.True(inspected); Assert.Equal(1, result.VersionsCreated);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void FutureScratchHeadroomBlocksBeforeSourceContentAndNeverDeletesUnderForever()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); f.File(content: "a new longer version", day: 1);
        using (var writer = f.Repository.OpenWriter()) f.Probe.Free = writer.EstimatePublicationAllocation(new(Versions: 1), 4096).TotalBytes + 4096;
        var result = f.Scan(f.Engine(RetentionPolicy.Forever)); Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, result.Capacity.VersionsRemoved);
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
    }

    [Fact]
    public void WorkspaceControlFailurePreservesEarlierConfirmedBatches()
    {
        using var f = new LiveCapacityFixture(); for (var i = 0; i < 5; i++) f.File($"file-{i}");
        var work = f.Temp.Child("work");
        var engine = new FileHistoryEngine(f.Repository, new(BatchSize: 2, WorkspaceRoot: work, Capacity: new(RetentionPolicy.Forever)), f.Probe, new AllocationProbe());
        engine.BatchCommitted = progress =>
        {
            if (progress.VersionsCreated != 2) return;
            Directory.Delete(work); File.WriteAllText(work, "Owned fixture blocks later workspace creation.");
        };
        var failure = Assert.Throws<ScanCapacityException>(() => engine.Scan([f.Selection]));
        Assert.Equal(2, failure.Result.VersionsCreated); Assert.Equal(ScanOutcome.ControlError, failure.Result.Outcome);
        using var reader = f.Repository.OpenReader(); Assert.Equal(2, reader.EnumerateRetentionVersions().Count());
    }
}
