using System.ComponentModel;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class LiveCapacityRecoveryTests
{
    [Theory]
    [InlineData("insufficient", ScanOutcome.CapacityBlocked)]
    [InlineData("impossible", ScanOutcome.CapacityTargetImpossible)]
    [InlineData("limit", ScanOutcome.CapacityWorkLimitExceeded)]
    [InlineData("cancel", ScanOutcome.Cancelled)]
    public void CapacityOutcomesRemainSeparateFromSourceSkips(string scenario, ScanOutcome expected)
    {
        using var f = new LiveCapacityFixture(); f.Seed(); f.File("b"); using var cts = new CancellationTokenSource();
        if (scenario == "limit") f.Seed(content: "newer", day: 1);
        f.Probe.Free = scenario == "impossible" ? 1000 : 0; f.Probe.Capacity = scenario == "impossible" ? 1000 : 1_000_000_000;
        if (scenario == "cancel") f.Probe.Before = (_, _) => cts.Cancel();
        var result = f.Scan(f.Engine(retention: scenario == "limit" ? new(MaximumVersions: 1) : null), cts.Token);
        Assert.Equal(expected, result.Outcome); Assert.Equal(0, result.SourceSkips); Assert.Equal(0, result.ContentStreamsOpened);
        Assert.Equal(0, result.VersionsCreated); Assert.Equal(0, result.Capacity.VersionsRemoved);
        Assert.Equal(scenario == "cancel", result.Cancelled);
    }

    [Theory]
    [InlineData("before-replace")] [InlineData("uncertain")]
    public void FullOrUncertainRetentionNeverDeletesUnconfirmedPayload(string fault)
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); f.Seed(content: "newer", day: 1); f.Probe.Free = 0;
        var engine = f.Engine();
        engine.ConfigureWriter = writer => writer.Observe = stage =>
        {
            if (fault == "before-replace" && stage == PublicationStage.BeforeMetadataInstall)
                throw new IOException("No space for tombstone metadata", new Win32Exception(OperatingSystem.IsWindows() ? 112 : 28));
            if (fault == "uncertain" && stage == PublicationStage.AfterReplace) writer.LoseHandleForTest();
        };
        var error = Assert.Throws<ScanCapacityException>(() => f.Scan(engine));
        var retention = Assert.IsType<CapacityRetentionException>(error.InnerException);
        Assert.Equal(ScanOutcome.ControlError, error.Result.Outcome); Assert.Equal(0, error.Result.Capacity.VersionsRemoved); Assert.Equal(0, error.Result.ContentStreamsOpened);
        Assert.True(System.IO.File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId)));
        if (fault == "uncertain") Assert.IsType<PublicationUncertainException>(retention.InnerException);
        using (var reader = f.Repository.OpenReader()) Assert.Equal(fault == "before-replace", reader.GetVersion(old.Identity) is not null);
        using var writer = f.Repository.OpenWriter();
        foreach (var candidate in writer.EnumeratePendingPublications().ToArray())
            Assert.Equal(fault == "before-replace" ? PublicationOutcome.Unpublished : PublicationOutcome.Published, writer.Reconcile(candidate));
        Assert.Equal(fault == "before-replace" ? 0 : 1, writer.CleanupRetiredPayloads().Result.Deleted);
    }

    [Fact]
    public void UncertainSourcePublicationKeepsReconciliationAndDoesNotInventProgress()
    {
        using var f = new LiveCapacityFixture(); f.File(); var before = f.Repository.OpenReader(); var authority = before.Authority; before.Dispose();
        var engine = f.Engine(); engine.ConfigureWriter = writer => writer.Observe = stage => { if (stage == PublicationStage.AfterReplace) writer.LoseHandleForTest(); };
        var error = Assert.Throws<ScanCapacityException>(() => f.Scan(engine)); Assert.IsType<PublicationUncertainException>(error.InnerException);
        Assert.Equal(authority, error.Result.Authority); Assert.Equal(0, error.Result.VersionsCreated); Assert.True(error.Result.ReconciliationMayBeRequired);
        using (var writer = f.Repository.OpenWriter()) foreach (var tx in writer.EnumeratePendingPublications().ToArray()) Assert.Equal(PublicationOutcome.Published, writer.Reconcile(tx));
        var fresh = f.Scan(f.Engine()); Assert.Equal(1, fresh.Unchanged); Assert.Equal(0, fresh.VersionsCreated);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void DestinationDiskFullIsSpecificAndNotASourceSkip(bool diskFull)
    {
        using var f = new LiveCapacityFixture(); f.File(); var engine = f.Engine();
        engine.ConfigureWriter = writer => writer.PayloadObserve = (stage, _) =>
        {
            if (stage == PayloadStage.BeforeWrite)
                throw diskFull ? new IOException("full", new Win32Exception(OperatingSystem.IsWindows() ? 112 : 28)) : new IOException("ordinary control IO");
        };
        if (diskFull)
        { var result = f.Scan(engine); Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome); Assert.Equal(0, result.SourceSkips); Assert.Equal(0, result.VersionsCreated); }
        else
        { var error = Assert.Throws<ScanCapacityException>(() => f.Scan(engine)); Assert.Equal(ScanOutcome.ControlError, error.Result.Outcome); Assert.Equal(0, error.Result.SourceSkips); }
        Assert.Equal(1, f.Scan(f.Engine()).VersionsCreated);
    }

    [Fact]
    public void MetadataOrWorkspaceDiskFullRemainsAControlFailure()
    {
        using var f = new LiveCapacityFixture(); f.File(); var engine = f.Engine();
        engine.ConfigureWriter = writer => writer.Observe = stage =>
        {
            if (stage == PublicationStage.BeforeMetadataInstall)
                throw new IOException("Metadata/workspace allocation failed", new Win32Exception(OperatingSystem.IsWindows() ? 112 : 28));
        };
        var error = Assert.Throws<ScanCapacityException>(() => f.Scan(engine));
        Assert.Equal(ScanOutcome.ControlError, error.Result.Outcome); Assert.Equal(0, error.Result.SourceSkips);
        Assert.Equal(0, error.Result.VersionsCreated); Assert.True(error.Result.ReconciliationMayBeRequired);
    }

    [Fact]
    public void CapacityProbeControlCorruptionStillThrows()
    {
        using var f = new LiveCapacityFixture(); f.File(); f.Probe.Before = (_, _) => throw new InvalidDataException("probe/control unavailable");
        var error = Assert.Throws<ScanCapacityException>(() => f.Scan(f.Engine()));
        Assert.IsType<InvalidDataException>(Assert.IsType<CapacityRetentionException>(error.InnerException).InnerException);
        Assert.Equal(0, error.Result.SourceSkips); Assert.Equal(0, error.Result.ContentStreamsOpened); Assert.False(error.Result.CapacityBlocked);
    }

    [Fact]
    public void CancellationAfterConfirmedRetentionReportsProgressWithoutBaselineOrCopy()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); f.Seed(content: "newer", day: 1); f.Probe.Free = 0;
        using var cts = new CancellationTokenSource(); var engine = f.Engine();
        engine.ConfigureWriter = w => w.PayloadCleanupObserve = (stage, _) => { if (stage == PayloadCleanupStage.Deleted) cts.Cancel(); };
        var result = f.Scan(engine, cts.Token); Assert.True(result.Cancelled); Assert.True(result.Partial); Assert.Equal(1, result.Capacity.VersionsRemoved); Assert.Equal(0, result.VersionsCreated);
        using var fresh = f.Repository.OpenReader(); Assert.Equal(fresh.Authority, result.Authority); Assert.Null(fresh.GetVersion(old.Identity));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EarlierSourceBatchesSurviveCapacityOrCancellationStop(bool cancel)
    {
        using var f = new LiveCapacityFixture(); f.File("a"); f.File("b"); using var cts = new CancellationTokenSource();
        var engine = f.Engine(RetentionPolicy.Forever, batch: 1);
        engine.BatchCommitted = _ => { if (cancel) cts.Cancel(); else f.Probe.Free = 0; };
        var result = f.Scan(engine, cts.Token); Assert.True(result.Partial); Assert.Equal(1, result.VersionsCreated); Assert.Equal(1, result.BatchesCommitted);
        Assert.Equal(cancel ? ScanOutcome.Cancelled : ScanOutcome.CapacityBlocked, result.Outcome); Assert.Equal(0, result.SourceSkips);
        using var reader = f.Repository.OpenReader(); Assert.Equal(result.Authority, reader.Authority); Assert.NotNull(reader.GetCurrent(new(FoundationTests.Root, "a"))); Assert.Null(reader.GetNewestVersion(new(FoundationTests.Root, "b")));
    }

    [Theory]
    [InlineData("live-capacity-retained")] [InlineData("live-capacity-staged")] [InlineData("live-capacity-published")]
    public async Task ProcessDeathNeedsOnlyCommittedTruthAndFreshAssessment(string boundary)
    {
        using var f = new LiveCapacityFixture();
        StoredVersion? old = boundary == "live-capacity-retained" ? f.Seed(current: false) : null;
        if (old is null) f.File();
        await using (var child = WriterProcess.Start(boundary, f.Repository.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        var orphans = Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories);
        if (boundary == "live-capacity-staged") Assert.Single(orphans); else Assert.Empty(orphans);
        using (var reader = f.Repository.OpenReader())
        {
            if (old is not null) Assert.Null(reader.GetVersion(old.Identity));
            Assert.Equal(boundary == "live-capacity-published", reader.GetNewestVersion(new(FoundationTests.Root, "a")) is not null);
        }
        var result = f.Scan(f.Engine()); Assert.Equal(ScanOutcome.Completed, result.Outcome);
        Assert.Equal(boundary == "live-capacity-published" ? 0 : 1, result.VersionsCreated);
        Assert.Equal(orphans, Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories)); // No arbitrary adoption/sweep.
    }
}
