using System.ComponentModel;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

internal sealed class ScriptedCapacityProbe(CapacityObservation first, params CapacityObservation[] following) : IRepositoryCapacityProbe
{
    private readonly CapacityObservation[] _observations = [first, .. following];
    internal int Calls;
    internal Action<int, Repository>? BeforeObserve;
    public CapacityObservation Observe(Repository repository)
    { var index = Calls++; BeforeObserve?.Invoke(index, repository); return _observations[Math.Min(index, _observations.Length - 1)]; }
}

public sealed class CapacityRetentionTests
{
    internal static ScriptedCapacityProbe Pressure() => new(new CapacityObservation(1000, 0));
    internal static CapacityRetentionEngine Engine(Repository repo, ScriptedCapacityProbe? probe = null, int batch = 1, string? workspace = null) =>
        new(repo, new(BatchSize: batch, WorkspaceRoot: workspace), probe ?? Pressure());

    [Theory]
    [InlineData(1, 1)] [InlineData(19, 1)] [InlineData(20, 1)] [InlineData(21, 2)]
    [InlineData(100, 5)] [InlineData(101, 6)] [InlineData(long.MaxValue, 461168601842738791)]
    public void ReserveUsesExactSafeCeilingDivision(long capacity, long reserve)
    { var target = new CapacityTarget(new(capacity, 0), 0); Assert.Equal(reserve, target.Reserve); Assert.Equal(reserve, target.RequiredFree); }

    [Theory]
    [InlineData(0, 0)] [InlineData(-1, 0)] [InlineData(10, -1)] [InlineData(10, 11)]
    public void InvalidObservationsAreRejected(long capacity, long free) => Assert.Throws<ArgumentOutOfRangeException>(() => new CapacityObservation(capacity, free));

    [Fact]
    public void NegativeUpcomingOverflowAndExactBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapacityTarget(new CapacityObservation(100, 50), -1));
        Assert.Throws<OverflowException>(() => new CapacityTarget(new CapacityObservation(long.MaxValue, 0), long.MaxValue));
        Assert.True(new CapacityTarget(new CapacityObservation(100, 25), 20).Satisfied); Assert.False(new CapacityTarget(new CapacityObservation(100, 24), 20).Satisfied);
        Assert.Equal(1, new CapacityTarget(new CapacityObservation(100, 24), 20).Deficit);
    }

    [Fact]
    public void AmpleSpaceNeverReadsHistoryCreatesScratchAcquiresWriterOrCleansPendingPayloads()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        using (var writer = f.Repository.OpenWriter()) using (var reader = f.Repository.OpenReader()) writer.RemoveVersions([old.Identity], reader.Authority, false);
        // A malformed Versions index must not be visited on the O(1)-history fast path.
        RetentionProjectionTests.Corrupt(f.Repository, (r, _) => r.Captured.Root with { Versions = r.Captured.Root.Current });
        var before = RetentionTests.Inventory(f.Repository); var probe = new ScriptedCapacityProbe(new CapacityObservation(100, 25));
        var engine = Engine(f.Repository, probe, workspace: f.Temp.Child("not-created"));
        engine.WriterAcquired = _ => throw new InvalidOperationException("Writer must not be acquired.");
        var result = engine.EnsureCapacity(20); Assert.Equal(CapacityRetentionOutcome.AlreadySatisfied, result.Outcome); Assert.True(result.Satisfied);
        Assert.Equal(1, probe.Calls); Assert.Equal(0, result.HistoryScans); Assert.Equal(0, result.Sort.RunsCreated); Assert.Equal(0, result.Cleanup.Inspected);
        Assert.Equal(before, RetentionTests.Inventory(f.Repository)); Assert.False(Directory.Exists(f.Temp.Child("not-created")));
        Assert.True(File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId))); Assert.Equal(result.StartingAuthority, result.FinalConfirmedAuthority);
    }

    [Fact]
    public void ImpossibleTargetAndProbeFailureCannotDestroyHistory()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var before = RetentionTests.Inventory(f.Repository);
        var impossible = Engine(f.Repository, new(new CapacityObservation(100, 100))).EnsureCapacity(96);
        Assert.Equal(CapacityRetentionOutcome.ImpossibleTarget, impossible.Outcome); Assert.Equal(0, impossible.Removed); Assert.Equal(0, impossible.HistoryScans);
        var probe = Pressure(); probe.BeforeObserve = (_, _) => throw new IOException("Cannot observe quota/free space.");
        var error = Assert.Throws<CapacityRetentionException>(() => Engine(f.Repository, probe).EnsureCapacity(0));
        Assert.Equal(CapacityRetentionOutcome.ControlError, error.Result.Outcome); Assert.Null(error.Result.LatestObservation); Assert.Equal(0, error.Result.Removed);
        var overflow = Assert.Throws<CapacityRetentionException>(() => Engine(f.Repository, new(new CapacityObservation(long.MaxValue, 0))).EnsureCapacity(long.MaxValue));
        Assert.IsType<OverflowException>(overflow.InnerException); Assert.Equal(long.MaxValue, overflow.Result.LatestObservation!.Capacity); Assert.Null(overflow.Result.Latest);
        Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FreshOwnedProbeCanSatisfyOrInvalidateTargetBeforeAnyScan(bool impossible)
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var before = RetentionTests.Inventory(f.Repository);
        var probe = new ScriptedCapacityProbe(new CapacityObservation(1000, 0), impossible ? new CapacityObservation(10, 0) : new CapacityObservation(1000, 70));
        var result = Engine(f.Repository, probe).EnsureCapacity(20);
        Assert.Equal(impossible ? CapacityRetentionOutcome.ImpossibleTarget : CapacityRetentionOutcome.AlreadySatisfied, result.Outcome);
        Assert.Equal(2, result.CapacityObservations); Assert.Equal(0, result.HistoryScans); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [Theory]
    [InlineData(5, false)] [InlineData(99, false)] [InlineData(5, true)] [InlineData(99, true)]
    public void OnlyActualFeedbackStopsRetentionRegardlessOfLogicalRemovedLength(long freeAfter, bool encrypted)
    {
        using var f = new HistoryFixture(encrypted); var old = f.Add(); var current = f.Add(day: -100);
        var probe = new ScriptedCapacityProbe(new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 0), new(100, freeAfter));
        var result = Engine(f.Repository, probe).EnsureCapacity(0);
        Assert.Equal(CapacityRetentionOutcome.SatisfiedAfterRetention, result.Outcome); Assert.Equal(1, result.Removed); Assert.Equal(5, result.RemovedLogicalBytes);
        Assert.Equal(freeAfter, result.LatestObservation!.Free); Assert.Equal(4, result.CapacityObservations); Assert.Equal(1, result.HistoryScans);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(old.Identity)); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
    }

    [Theory]
    [InlineData("decrease")] [InlineData("grow-capacity")] [InlineData("impossible")]
    public void FeedbackCanDecreaseAndCapacityChangesRecomputeTarget(string mode)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 5; i++) f.Add(day: i);
        var changed = mode == "impossible" ? new CapacityObservation(10, 0) : mode == "grow-capacity" ? new CapacityObservation(2000, 90) : new CapacityObservation(1000, 1);
        var probe = new ScriptedCapacityProbe(new CapacityObservation(1000, 10), new CapacityObservation(1000, 10), new CapacityObservation(1000, 10), changed, new CapacityObservation(2000, 120));
        var result = Engine(f.Repository, probe).EnsureCapacity(20);
        Assert.Equal(mode == "impossible" ? CapacityRetentionOutcome.ImpossibleTarget : CapacityRetentionOutcome.SatisfiedAfterRetention, result.Outcome);
        Assert.Equal(mode == "impossible" ? 1 : 2, result.Removed); Assert.Equal(mode == "impossible" ? 1 : 100, result.Latest!.Reserve);
        Assert.Equal(mode == "impossible" ? 21 : 120, result.Latest.RequiredFree); Assert.Equal(result.Removed, result.BatchesCommitted);
    }

    [Fact]
    public void PostSortExternalFreeIncreaseStopsBeforeRemoval()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var before = RetentionTests.Inventory(f.Repository);
        var probe = new ScriptedCapacityProbe(new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 5));
        var result = Engine(f.Repository, probe).EnsureCapacity(0); Assert.Equal(CapacityRetentionOutcome.AlreadySatisfied, result.Outcome);
        Assert.Equal(1, result.HistoryScans); Assert.Equal(0, result.Removed); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [Fact]
    public void ActualGainSmallerThanLogicalBytesRequiresAnotherBatch()
    {
        using var f = new HistoryFixture(); for (int i = 0; i < 4; i++) f.Add(day: i);
        var result = Engine(f.Repository, new(new CapacityObservation(100, 10), new CapacityObservation(100, 10), new CapacityObservation(100, 10), new CapacityObservation(100, 11), new CapacityObservation(100, 15))).EnsureCapacity(10);
        Assert.Equal(CapacityRetentionOutcome.SatisfiedAfterRetention, result.Outcome); Assert.Equal(2, result.Removed);
        Assert.Equal(10, result.RemovedLogicalBytes); Assert.Equal(5, result.LatestObservation!.Free - result.InitialObservation!.Free);
    }

    [Fact]
    public void OldestAcrossRootsIgnoresPathMtimePublicationAndProtectsExactCurrent()
    {
        using var f = new HistoryFixture(); var repo = f.Repository; var other = Guid.Parse("00000001-0000-0000-0000-000000000001");
        var early = RetentionTests.Add(repo, "a", FoundationTests.Time.AddDays(10));
        var rollback = RetentionTests.Add(repo, "z", FoundationTests.Time.AddDays(-5), other);
        var currentA = RetentionTests.Add(repo, "a", FoundationTests.Time.AddDays(-100));
        var currentZ = RetentionTests.Add(repo, "z", FoundationTests.Time.AddDays(-100), other);
        var deleted = RetentionTests.Add(repo, "deleted", FoundationTests.Time.AddDays(-10)); RetentionTests.RemoveCurrent(repo, deleted);
        var probe = new ScriptedCapacityProbe(new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 0), new CapacityObservation(100, 5));
        var result = Engine(repo, probe).EnsureCapacity(0); Assert.Equal(2, result.Removed); Assert.Equal(2, result.Protected); Assert.Equal(3, result.Eligible);
        using var reader = repo.OpenReader(); Assert.Null(reader.GetVersion(deleted.Identity)); Assert.Null(reader.GetVersion(rollback.Identity)); Assert.NotNull(reader.GetVersion(early.Identity));
        Assert.Equal(currentA, reader.GetCurrent(currentA.Identity.Path)); Assert.Equal(currentZ, reader.GetCurrent(currentZ.Identity.Path));
    }

    [Fact]
    public void ExhaustedEligibleHistoryReturnsInsufficientAndUnprotectedFinalVersionCanDisappear()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Remove("a", unprotect: true);
        var result = Engine(f.Repository).EnsureCapacity(0); Assert.Equal(CapacityRetentionOutcome.InsufficientHistory, result.Outcome); Assert.False(result.Satisfied);
        Assert.Equal(1, result.Removed); Assert.Equal(0, result.Protected); Assert.Equal(50, result.Latest!.Deficit);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(old.Identity));
    }

    [Fact]
    public void CancellationAndBatchLimitsPreserveConfirmedProgressAndFreshInvocationContinues()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 6; i++) f.Add(day: i);
        var initial = RetentionTests.Inventory(f.Repository); var engine = Engine(f.Repository, batch: 2);
        Assert.Equal(CapacityRetentionOutcome.Cancelled, engine.EnsureCapacity(0, new(true)).Outcome); Assert.Equal(initial, RetentionTests.Inventory(f.Repository));
        using var cts = new CancellationTokenSource(); engine.Observe = (stage, _) => { if (stage == CapacityExecutionStage.BatchCommitted) cts.Cancel(); };
        var partial = engine.EnsureCapacity(0, cts.Token); Assert.Equal(CapacityRetentionOutcome.Cancelled, partial.Outcome); Assert.Equal(2, partial.Removed); Assert.True(partial.Partial); Assert.Equal(4, partial.CapacityObservations);
        var limited = new CapacityRetentionEngine(f.Repository, new(BatchSize: 1, MaximumBatches: 1), Pressure()).EnsureCapacity(0);
        Assert.Equal(CapacityRetentionOutcome.WorkLimitExceeded, limited.Outcome); Assert.Equal(1, limited.Removed);
        var rest = Engine(f.Repository).EnsureCapacity(0); Assert.Equal(2, rest.Removed); Assert.Equal(CapacityRetentionOutcome.InsufficientHistory, rest.Outcome);
    }

    [Fact]
    public void OrdinaryDeleteFailureAndAbsentPayloadAreReportedWithoutFabricatedSpace()
    {
        using var f = new HistoryFixture(); var first = f.Add(); var second = f.Add(day: 1); f.Add(day: 2); File.Delete(f.Repository.GetPayloadPath(first.Identity.VersionId));
        var engine = Engine(f.Repository); engine.WriterAcquired = writer => writer.PayloadCleanupObserve = (stage, _) =>
        { if (stage == PayloadCleanupStage.DeleteAttempt) throw new IOException("Injected ordinary disk error", new Win32Exception(112)); };
        var result = engine.EnsureCapacity(0); Assert.Equal(2, result.Removed); Assert.Equal(1, result.Cleanup.AlreadyAbsent); Assert.Equal(1, result.Cleanup.Failed); Assert.Equal(0, result.Cleanup.Deleted);
        Assert.Equal(CapacityRetentionOutcome.InsufficientHistory, result.Outcome); Assert.Equal(0, result.LatestObservation!.Free); Assert.True(File.Exists(f.Repository.GetPayloadPath(second.Identity.VersionId)));
        using var recovery = f.Repository.OpenWriter(); Assert.Equal(1, recovery.CleanupRetiredPayloads().Result.Deleted);
    }

    [WindowsFact]
    public void HeldPayloadDefersDeletionAndCannotSatisfyTargetUntilActualObservationDoes()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        using (var reader = f.Repository.OpenReader()) using (var held = reader.OpenVersionPayload(old.Identity))
        {
            var result = Engine(f.Repository).EnsureCapacity(0); Assert.Equal(1, result.Removed); Assert.Equal(1, result.Cleanup.Deferred); Assert.False(result.Satisfied);
            Assert.Equal(CapacityRetentionOutcome.InsufficientHistory, result.Outcome);
        }
        using var writer = f.Repository.OpenWriter(); Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deleted);
    }

    [WindowsFact]
    public void NativeProbeIsRepositoryBoundSupportsExtendedPathsAndDoesNotMutate()
    {
        using var f = new HistoryFixture(); f.Add(); var before = RetentionTests.Inventory(f.Repository); using var writer = f.Repository.OpenWriter();
        var regular = f.Repository.ObserveCapacity(); Assert.True(regular.Capacity > 0); Assert.InRange(regular.Free, 0, regular.Capacity);
        var extended = Repository.Open(@"\\?\" + f.Repository.DirectoryPath).ObserveCapacity(); Assert.Equal(regular.Capacity, extended.Capacity);
        Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [LinuxFact]
    public void NativeProbeRefusesAnUnsupportedQuotaBlindFallback()
    { using var f = new HistoryFixture(); Assert.Throws<PlatformNotSupportedException>(() => f.Repository.ObserveCapacity()); }

    [Fact]
    public void PublicWorkAndStateBoundsRejectUnsafeRequests()
    {
        using var f = new HistoryFixture();
        foreach (var options in new[] { new CapacityRetentionOptions(BatchSize: 0), new CapacityRetentionOptions(BatchSize: 4097),
            new CapacityRetentionOptions(MaximumCandidateBytes: 8511), new CapacityRetentionOptions(MaximumCandidateBytes: 4 * 1024 * 1024 + 1),
            new CapacityRetentionOptions(MaximumVersions: 0), new CapacityRetentionOptions(MaximumBatches: 0),
            new CapacityRetentionOptions(SortChunkEntries: 0), new CapacityRetentionOptions(SortChunkEntries: 16385),
            new CapacityRetentionOptions(SortChunkBytes: 8511), new CapacityRetentionOptions(SortChunkBytes: 4 * 1024 * 1024 + 1),
            new CapacityRetentionOptions(MaximumScratchBytes: 0), new CapacityRetentionOptions(MaximumScratchBytesWritten: 0) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new CapacityRetentionEngine(f.Repository, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine(f.Repository).EnsureCapacity(-1));
        var before = RetentionTests.Inventory(f.Repository);
        var error = Assert.Throws<CapacityRetentionException>(() => Engine(f.Repository, workspace: f.Repository.DirectoryPath).EnsureCapacity(0));
        Assert.IsType<ArgumentException>(error.InnerException); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }
}
