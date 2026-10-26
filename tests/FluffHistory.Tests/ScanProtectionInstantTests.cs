using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ScanProtectionInstantTests
{
    [Fact]
    public void ThreeHundredFilesIn128ChangeBatchesHaveOneProtectionTimeAndOneTimelinePoint()
    {
        using var f = new LiveCapacityFixture();
        for (var i = 0; i < 300; i++) f.File($"file-{i:000}.txt");
        var reads = 0; var engine = new FileHistoryEngine(f.Repository, new(BatchSize: 128));
        engine.Clock = () => FoundationTests.Time.AddHours(reads++);
        var result = engine.Scan([f.Selection]);
        Assert.Equal(300, result.VersionsCreated); Assert.Equal(3, result.BatchesCommitted); Assert.Equal(1, reads);
        using var reader = f.Repository.OpenReader();
        var versions = reader.EnumerateRetentionVersions().ToArray(); Assert.Equal(300, versions.Length);
        Assert.All(versions, v => Assert.Equal(FoundationTests.Time, v.StoredUtc));
        Assert.Equal(FoundationTests.Time, Assert.Single(reader.GetTimelinePage(new HistoryScope(FoundationTests.Root), 10).Items));
    }

    [Fact]
    public void ByteBoundFlushesReuseOneClockSample()
    {
        using var f = new LiveCapacityFixture(); for (var i = 0; i < 5; i++) f.File($"file-{i}", "12345");
        var reads = 0; var engine = new FileHistoryEngine(f.Repository, new(MaximumPendingPayloadBytes: 5));
        engine.Clock = () => FoundationTests.Time.AddHours(reads++);
        var result = engine.Scan([f.Selection]); Assert.Equal(5, result.VersionsCreated); Assert.Equal(5, result.BatchesCommitted); Assert.Equal(1, reads);
        using var reader = f.Repository.OpenReader(); Assert.Single(reader.EnumerateRetentionVersions().Select(v => v.StoredUtc).Distinct());
    }

    [Fact]
    public void SourceBoundaryFlushesShareTheScanProtectionInstant()
    {
        using var f = new LiveCapacityFixture(); f.File(); var second = f.Temp.Child("second-source"); Directory.CreateDirectory(second); File.WriteAllText(Path.Combine(second, "b.txt"), "second");
        var selection = new SourceSelection(new(Guid.NewGuid(), second), second); var reads = 0;
        var engine = new FileHistoryEngine(f.Repository) { Clock = () => FoundationTests.Time.AddHours(reads++) };
        var result = engine.Scan([f.Selection, selection]); Assert.Equal(2, result.VersionsCreated); Assert.True(result.BatchesCommitted >= 2); Assert.Equal(1, reads);
        using var reader = f.Repository.OpenReader(); Assert.Single(reader.EnumerateRetentionVersions().Select(v => v.StoredUtc).Distinct());
    }

    [Fact]
    public void SeparateScansHaveSeparateInstantsIncludingClockRollback()
    {
        using var f = new LiveCapacityFixture(); f.File("a", "first"); var reads = 0;
        var newer = FoundationTests.Time.AddDays(2); var older = FoundationTests.Time.AddDays(1);
        var engine = new FileHistoryEngine(f.Repository) { Clock = () => reads++ == 0 ? newer : older };
        engine.Scan([f.Selection]); f.File("a", "second, changed", day: 1); engine.Scan([f.Selection]);
        Assert.Equal(2, reads); using var reader = f.Repository.OpenReader();
        var history = reader.EnumerateVersions(new(FoundationTests.Root, "a")).ToArray(); Assert.Equal(2, history.Length);
        Assert.Equal(older, reader.GetCurrent(new(FoundationTests.Root, "a"))!.StoredUtc);
        Assert.Equal(new[] { newer, older }, reader.GetTimelinePage(new HistoryScope(FoundationTests.Root), 10).Items);
    }

    [Fact]
    public void CancellationAfterTwoBatchesRetainsOneTimelinePointAndExactProgress()
    {
        using var f = new LiveCapacityFixture(); for (var i = 0; i < 300; i++) f.File($"file-{i:000}");
        using var cancel = new CancellationTokenSource(); var reads = 0;
        var engine = new FileHistoryEngine(f.Repository, new(BatchSize: 128)) { Clock = () => FoundationTests.Time.AddHours(reads++) };
        engine.BatchCommitted = progress => { if (progress.VersionsCreated == 256) cancel.Cancel(); };
        var result = engine.Scan([f.Selection], cancel.Token); Assert.Equal(ScanOutcome.Cancelled, result.Outcome); Assert.Equal(256, result.VersionsCreated); Assert.Equal(2, result.BatchesCommitted); Assert.Equal(1, reads);
        using var reader = f.Repository.OpenReader(); Assert.Equal(256, reader.EnumerateRetentionVersions().Count()); Assert.Single(reader.GetTimelinePage(new HistoryScope(FoundationTests.Root), 10).Items);
    }

    [Fact]
    public void NoChangeRemovalAndReactivationDoNotInventProtectionTimes()
    {
        using var f = new LiveCapacityFixture(); f.File(); var reads = 0;
        var engine = new FileHistoryEngine(f.Repository) { Clock = () => FoundationTests.Time.AddHours(reads++) };
        engine.Scan([f.Selection]); var noChange = engine.Scan([f.Selection]); Assert.Equal(0, noChange.VersionsCreated); Assert.Equal(0, noChange.BatchesCommitted);
        var inactive = new SourceSelection(new(FoundationTests.Root, f.Source, isProtected: false), f.Source);
        Assert.Equal(0, engine.Scan([inactive]).VersionsCreated); Assert.Equal(0, engine.Scan([f.Selection]).VersionsCreated);
        using var reader = f.Repository.OpenReader(); Assert.Single(reader.EnumerateRetentionVersions()); Assert.Equal(FoundationTests.Time, Assert.Single(reader.GetTimelinePage(new HistoryScope(FoundationTests.Root), 10).Items)); Assert.Equal(4, reads);
    }
}
