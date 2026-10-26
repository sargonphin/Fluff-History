using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class LiveAllocationTests
{
    [Theory]
    [InlineData(0, 4096, 0)] [InlineData(1, 4096, 4096)] [InlineData(4096, 4096, 4096)]
    [InlineData(4097, 4096, 8192)] [InlineData(long.MaxValue, 1, long.MaxValue)]
    public void AllocationRoundingIsCheckedIntegerMath(long bytes, long unit, long expected) => Assert.Equal(expected, AllocationGeometry.RoundUp(bytes, unit));

    [Fact]
    public void InvalidAndUnrepresentableAllocationsFailBeforeIO()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AllocationGeometry(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AllocationGeometry.RoundUp(-1, 4096));
        Assert.Throws<OverflowException>(() => AllocationGeometry.RoundUp(long.MaxValue, 4096));
        using var f = new LiveCapacityFixture(); f.File();
        f.Access.Metadata = (_, observed) => observed.Directory ? observed : observed with { Length = long.MaxValue };
        f.Access.BeforeOpen = _ => throw new InvalidOperationException("Unrepresentable estimate must precede content.");
        int drafts = 0; var engine = f.Engine(); engine.ConfigureWriter = w => w.PayloadObserve = (stage, _) => { if (stage == PayloadStage.Created) drafts++; };
        var error = Assert.Throws<ScanCapacityException>(() => f.Scan(engine)); Assert.IsType<OverflowException>(error.InnerException);
        Assert.Equal(0, f.Access.Streams); Assert.Equal(0, drafts);
    }

    [Theory]
    [InlineData(1)] [InlineData(16)] [InlineData(128)]
    public void DerivedPublicationBoundCoversActualWritesAndIsMonotone(int count)
    {
        using var f = new LiveCapacityFixture();
        using var writer = f.Repository.OpenWriter(); using var reader = f.Repository.OpenReader();
        var before = RetentionTests.Inventory(f.Repository);
        var one = writer.EstimatePublicationAllocation(new(Versions: 1), 4096);
        var estimate = writer.EstimatePublicationAllocation(new(Versions: count), 4096);
        Assert.True(estimate.TotalBytes >= one.TotalBytes); Assert.True(estimate.MaximumPages >= one.MaximumPages); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
        var versions = Enumerable.Range(0, count).Select(i =>
        {
            var name = string.Join('/', new[] { new string('a', 251) + $"{i:D4}" }.Concat(Enumerable.Repeat(new string('b', 255), 6)).Append(new string('c', 254)).Append("d"));
            Assert.Equal(HistoryPath.MaximumPathLength, name.Length);
            var v = writer.PrepareVersion(new(FoundationTests.Root, name, 0, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive), FoundationTests.Time, new("EF46DB3751D8E999"));
            var p = writer.GetPreparedPayloadPath(v); Directory.CreateDirectory(Path.GetDirectoryName(p)!); System.IO.File.WriteAllBytes(p, []); return v;
        }).ToArray();
        writer.RotationBytes = 65536; writer.Apply(new(versions), reader.Authority);
        Assert.True(writer.LastWriteStatistics!.BytesWritten <= estimate.TransferAndInstallBytes); Assert.True(writer.LastWriteStatistics.PagesWritten <= estimate.MaximumPages);
        Assert.True(estimate.RotationBytes > 0); Assert.True(estimate.ControlBytes > 0); Assert.True(estimate.NamespaceAllowanceBytes > 0);
    }

    [Fact]
    public void MetadataOnlyIndexCombinationsAndBoundsAreExplicit()
    {
        using var f = new LiveCapacityFixture(); f.Seed();
        using var writer = f.Repository.OpenWriter();
        var empty = writer.EstimatePublicationAllocation(new(), 4096); Assert.Equal(0, empty.TotalBytes);
        foreach (var plan in new[] { new PublicationAllocationRequest(Upserts: 1), new PublicationAllocationRequest(Removals: 1), new PublicationAllocationRequest(Sources: 1), new PublicationAllocationRequest(1, 1, 1, 1), new PublicationAllocationRequest(Versions: MutationBatch.MaximumChanges) })
        { var estimate = writer.EstimatePublicationAllocation(plan, 4096); Assert.True(estimate.SemanticBytes > 0); Assert.True(estimate.IndexBytes > 0); Assert.True(estimate.TotalBytes > estimate.SemanticBytes); }
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.EstimatePublicationAllocation(new(Versions: -1), 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.EstimatePublicationAllocation(new(Versions: 4096, Upserts: 1), 4096));
        Assert.Throws<OverflowException>(() => writer.EstimatePublicationAllocation(new(Versions: 1), long.MaxValue));
    }

    [WindowsFact]
    public void NativeGeometryAcceptsSameVolumeWithoutWritesAndStillBlocksInsufficientCapacity()
    {
        using var f = new LiveCapacityFixture(); var before = RetentionTests.Inventory(f.Repository);
        Assert.True(f.Repository.ObserveAllocationGeometry(f.Temp.Child("absent-workspace")).AllocationUnit > 0);
        Assert.Equal(before, RetentionTests.Inventory(f.Repository)); Assert.False(Directory.Exists(f.Temp.Child("absent-workspace")));
        f.File(); f.Probe.Free = 0; before = RetentionTests.Inventory(f.Repository);
        var engine = new FileHistoryEngine(f.Repository, new(WorkspaceRoot: f.Temp.Child("absent-workspace"), Capacity: new(RetentionPolicy.UntilSaturation)), f.Access, f.Probe);
        var result = f.Scan(engine); Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome);
        Assert.True(f.Probe.Calls > 0); Assert.Equal(0, f.Access.Streams); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [Fact]
    public void OwnedCapacityReusesWriterAndRefusesVulnerableDraftsPreparedVersionsOrForeignRepository()
    {
        using var f = new LiveCapacityFixture(); using var other = new LiveCapacityFixture();
        if (!OperatingSystem.IsWindows()) Assert.Throws<PlatformNotSupportedException>(() => f.Repository.ObserveAllocationGeometry());
        using var writer = f.Repository.OpenWriter(); var engine = new CapacityRetentionEngine(f.Repository, probe: f.Probe);
        var adequate = engine.EnsureCapacity(writer, 0); Assert.Equal(CapacityRetentionOutcome.AlreadySatisfied, adequate.Outcome);
        using (var draft = writer.CreatePayloadDraft()) Assert.Throws<InvalidOperationException>(() => engine.EnsureCapacity(writer, 0));
        using (var foreign = other.Repository.OpenWriter()) Assert.Throws<ArgumentException>(() => engine.EnsureCapacity(foreign, 0));
        writer.PrepareVersion(new(FoundationTests.Root, "prepared", 0, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive), FoundationTests.Time, new("EF46DB3751D8E999"));
        Assert.Throws<InvalidOperationException>(() => engine.EnsureCapacity(writer, 0));
        using var owned = writer.OpenOwnedReader(); Assert.Equal(adequate.FinalConfirmedAuthority, owned.Authority);
    }
}
