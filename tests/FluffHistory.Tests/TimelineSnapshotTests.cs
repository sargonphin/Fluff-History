using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class TimelineSnapshotTests
{
    [Fact]
    public void OneSurvivingVersionHasNoContinuationAndReusesItsExactSnapshot()
    {
        using var f = new HistoryFixture(); var version = f.Add("é/𐐨");
        using var r = f.Repository.OpenReader(); var scope = new HistoryScope(FoundationTests.Root);
        foreach (var direction in Enum.GetValues<HistoryDirection>())
        foreach (var size in new[] { 1, RepositoryReader.MaximumHistoryPageSize })
        {
            var page = r.GetTimelinePage(scope, size, direction);
            Assert.Equal(new[] { version.StoredUtc }, page.Items); Assert.Null(page.Continuation);
        }
        Assert.Equal(1, r.TimelineCacheStatistics.Builds); Assert.Equal(3, r.TimelineCacheStatistics.Hits);
        Assert.Equal(1, r.HistoryStatistics.SemanticFramesResolved);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadCachedTimelinePage(new(0), scope));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadCachedTimelinePage(new(1_048_577), scope));
    }

    [Theory]
    [InlineData(HistoryDirection.OldestFirst)] [InlineData(HistoryDirection.NewestFirst)]
    public void StrictBinarySearchBoundariesAlsoMatchReferenceBetweenStoredTimes(HistoryDirection direction)
    {
        using var f = new HistoryFixture(); foreach (var day in new[] { 0, 4, 8 }) f.Add(day: day);
        using var r = f.Repository.OpenReader(); using var oracle = f.Repository.OpenReader(); var scope = new HistoryScope(FoundationTests.Root);
        var reference = TimelineProjectionTests.Reference(oracle, scope);
        foreach (var day in new[] { -1, 0, 2, 4, 8, 10 })
        {
            var boundary = FoundationTests.Time.AddDays(day);
            var query = $"time|{FoundationTests.Root:N}||{direction}|1";
            var cursor = new HistoryCursor(r.Authority, query, time: boundary);
            var expected = reference.Where(t => direction == HistoryDirection.OldestFirst ? t > boundary : t < boundary);
            if (direction == HistoryDirection.NewestFirst) expected = expected.Reverse();
            var page = r.GetTimelinePage(scope, 1, direction, cursor);
            Assert.Equal(expected.Take(1), page.Items); Assert.Equal(expected.Count() > 1, page.Continuation is not null);
        }
        Assert.Equal(1, r.TimelineCacheStatistics.Builds); Assert.Equal(5, r.TimelineCacheStatistics.Hits);
    }

    [Fact]
    public void MaximumRootAndFrozenNonAsciiScopeCacheNeverIncludeAnotherRoot()
    {
        using var f = new HistoryFixture(); var original = f.Add("other", day: 9);
        var maximum = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        RetentionProjectionTests.Corrupt(f.Repository, (r, append) =>
        {
            var version = new StoredVersion(original.Identity with { Path = new(maximum, "é/𐐨"), VersionId = Guid.NewGuid() },
                new(maximum, "é/𐐨", 0, FoundationTests.Time, FoundationTests.Time, 0), FoundationTests.Time, new("EF46DB3751D8E999"));
            var pointer = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, r.Captured.Root.Journal, Version: version)));
            byte[] Read(FrameAddress a) => append.ReadNew(a) ?? r.Read(a);
            var versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(r.Captured.Root.Versions, [(PathKeys.Version(version.Identity), pointer)]);
            return r.Captured.Root with { Versions = versions, Journal = pointer };
        });
        using var reader = f.Repository.OpenReader(); using var oracle = f.Repository.OpenReader();
        foreach (var folder in new[] { "", "é", "É" })
        {
            var scope = new HistoryScope(maximum, folder); var expected = TimelineProjectionTests.Reference(oracle, scope);
            Assert.Equal(expected, reader.GetTimelinePage(scope).Items); Assert.Equal(expected, reader.GetTimelinePage(scope).Items);
            Assert.DoesNotContain(original.StoredUtc, expected);
        }
        Assert.Equal(2, reader.TimelineCacheStatistics.Builds);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(257, false)] [InlineData(257, true)]
    public void AllCachedPagesMatchIndependentExactReferenceAndTraverseOnlyOnce(int paths, bool unique)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        if (paths != 0) MetadataDiagnosticFixture.Populate(repo, paths,
            storedTime: (p, i) => FoundationTests.Time.AddTicks(unique ? p * paths + i + 1 : (p + i) % 7 + 1));
        var before = TimelineResponsivenessMeasurementTests.Inventory(repo);
        using var oracle = repo.OpenReader(); var scope = new HistoryScope(FoundationTests.Root);
        var expected = TimelineProjectionTests.Reference(oracle, scope); using var reader = repo.OpenReader();
        foreach (var direction in Enum.GetValues<HistoryDirection>())
        foreach (var size in new[] { 1, 3, RepositoryReader.MaximumHistoryPageSize })
        {
            HistoryCursor? cursor = null; var actual = new List<DateTimeOffset>();
            do
            {
                var page = reader.GetTimelinePage(scope, size, direction, cursor); actual.AddRange(page.Items); cursor = page.Continuation;
            } while (cursor is not null);
            Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), actual);
        }
        Assert.Equal(1, reader.TimelineCacheStatistics.Builds);
        Assert.Equal(expected.Length, reader.TimelineCacheStatistics.RetainedTimestamps);
        Assert.Equal(8L * expected.Length, reader.TimelineCacheStatistics.RetainedTimestampBytes);
        Assert.Equal(0, reader.LastTimelineStatistics!.ValidatedVersions);
        Assert.Equal(before, TimelineResponsivenessMeasurementTests.Inventory(repo));
    }

    [Fact]
    public void SingleActiveCanonicalScopeReplacesAndParentReturnRebuilds()
    {
        using var f = new HistoryFixture(); f.Add("Foo/a", day: 5); f.Add("Foo/Nested/b", day: -1);
        f.Add("Foo2/sibling", day: 9); f.Add("Foo/é/𐐨", day: 2); f.Remove("Foo/a");
        using (var w = f.Repository.OpenWriter()) using (var r = f.Repository.OpenReader())
            w.Apply(new(currentRemovals: r.EnumerateCurrent().Select(v => v.Identity.Path),
                sourceRoots: [new(FoundationTests.Root, "unprotected", false)]), r.Authority);
        using var reader = f.Repository.OpenReader(); using var oracle = f.Repository.OpenReader();
        foreach (var folder in new[] { "", "Foo", "fOO", "Foo/Nested", "Foo2", "empty", "" })
        {
            var scope = new HistoryScope(FoundationTests.Root, folder);
            Assert.Equal(TimelineProjectionTests.Reference(oracle, scope), reader.GetTimelinePage(scope, direction: HistoryDirection.OldestFirst).Items);
        }
        Assert.Equal(6, reader.TimelineCacheStatistics.Builds);
        Assert.Equal(1, reader.TimelineCacheStatistics.Hits);
        Assert.Equal(5, reader.TimelineCacheStatistics.ScopeChanges);
        Assert.Equal(4, reader.TimelineCacheStatistics.RetainedTimestamps);
    }

    [Theory]
    [InlineData(HistoryDirection.OldestFirst)] [InlineData(HistoryDirection.NewestFirst)]
    public void OverflowUsesTheSameTraversalForAnExactFallbackWithoutPartialCache(HistoryDirection direction)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 37, storedTime: (p, i) => FoundationTests.Time.AddTicks(p * 37 + i + 1));
        using var reader = repo.OpenReader(); using var oracle = repo.OpenReader(); var scope = new HistoryScope(FoundationTests.Root);
        var expected = TimelineProjectionTests.Reference(oracle, scope); HistoryCursor? cursor = null; var actual = new List<DateTimeOffset>();
        do
        {
            var page = reader.ReadCachedTimelinePage(new(8), scope, 3, direction, cursor); actual.AddRange(page.Items); cursor = page.Continuation;
            Assert.Equal(74, reader.LastTimelineStatistics!.ValidatedVersions);
            Assert.Equal(74, reader.LastTimelineStatistics.MatchingVersions);
            Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps);
            Assert.Equal(0, reader.TimelineCacheStatistics.Builds);
        } while (cursor is not null);
        Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), actual);
        Assert.Equal(25, reader.TimelineCacheStatistics.Overflows);
        Assert.Equal(25 * 74, reader.HistoryStatistics.SemanticFramesResolved);
    }

    [Theory]
    [InlineData("CollectEntry")] [InlineData("ResolveEntry")] [InlineData("SortTicks")]
    [InlineData("DeduplicateTicks")] [InlineData("SnapshotCompleted")]
    public void CancellationNeverInstallsPartialSnapshotAndRetryBuildsExactly(string stage)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 7; i++) f.Add(day: i);
        using var reader = f.Repository.OpenReader(); using var cancel = new CancellationTokenSource();
        reader.TimelineObserve = s => { if (s.ToString() == stage) cancel.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => reader.GetTimelinePage(new(FoundationTests.Root), cancellationToken: cancel.Token));
        Assert.Equal(0, reader.TimelineCacheStatistics.Builds); Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps);
        Exclusive(f.Repository); reader.TimelineObserve = null;
        using var oracle = f.Repository.OpenReader();
        Assert.Equal(TimelineProjectionTests.Reference(oracle, new(FoundationTests.Root)), reader.GetTimelinePage(new(FoundationTests.Root), direction: HistoryDirection.OldestFirst).Items);
        Assert.Equal(1, reader.TimelineCacheStatistics.Builds);
    }

    [Fact]
    public void DisposalDuringConstructionAndAfterCompletionReleasesSnapshotAndHandles()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1);
        using (var reader = f.Repository.OpenReader())
        {
            reader.TimelineObserve = s => { if (s == TimelineReadStage.ResolveEntry) reader.Dispose(); };
            Assert.Throws<ObjectDisposedException>(() => reader.GetTimelinePage(new(FoundationTests.Root)));
            Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps);
        }
        using (var reader = f.Repository.OpenReader())
        {
            _ = reader.GetTimelinePage(new(FoundationTests.Root)); Assert.Equal(2, reader.TimelineCacheStatistics.RetainedTimestamps);
            reader.Dispose(); Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps);
            Assert.Throws<ObjectDisposedException>(() => reader.GetTimelinePage(new(FoundationTests.Root)));
        }
        Exclusive(f.Repository);
    }

    [Fact]
    public void ColdCorruptionFailureReleasesResourcesAndNeverPublishesCache()
    {
        using var f = new HistoryFixture(); var version = f.Add(); f.Add(day: 1); FrameAddress address;
        using (var r = f.Repository.OpenReader()) address = r.VersionIndex.Find(r.Captured.Root.Versions, PathKeys.Version(version.Identity))!;
        var file = SegmentReader.SegmentPath(f.Repository.DirectoryPath, address.SegmentId);
        byte original;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
        { stream.Position = address.BlockOffset + MetadataBlock.HeaderBytes; original = (byte)stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(original ^ 1)); }
        using var reader = f.Repository.OpenReader();
        Assert.Throws<InvalidDataException>(() => reader.GetTimelinePage(new(FoundationTests.Root)));
        Assert.Equal(0, reader.TimelineCacheStatistics.Builds); Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps); Exclusive(f.Repository);
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite)) { stream.Position = address.BlockOffset + MetadataBlock.HeaderBytes; stream.WriteByte(original); }
        Assert.Equal(2, reader.GetTimelinePage(new(FoundationTests.Root)).Items.Count);
    }

    [Fact]
    public void RetirementDuringConstructionReturnsNoPageOrSnapshot()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 83, rotation: 65_536);
        using var reader = repo.OpenReader(); var retired = false;
        reader.TimelineObserve = stage =>
        {
            if (stage != TimelineReadStage.WindowCompleted || retired) return;
            retired = true; using var writer = repo.OpenWriter(); writer.CompactMetadata(); writer.CleanupRetiredMetadata();
        };
        Assert.Throws<ReaderGenerationExpiredException>(() => reader.ReadCachedTimelinePage(new(), new(FoundationTests.Root), readBudget: new(2)));
        Assert.True(retired); Assert.Equal(0, reader.TimelineCacheStatistics.Builds);
        Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps); Exclusive(repo);
    }

    [Fact]
    public void CompletedSnapshotReusesValidatedValuesButFreshColdBuildStillDetectsLaterDamage()
    {
        using var f = new HistoryFixture(); var version = f.Add(); f.Add(day: 1); var scope = new HistoryScope(FoundationTests.Root);
        using var reader = f.Repository.OpenReader();
        var first = reader.GetTimelinePage(scope).Items.ToArray(); var prior = reader.HistoryStatistics;
        FrameAddress address;
        using (var r = f.Repository.OpenReader()) address = r.VersionIndex.Find(r.Captured.Root.Versions, PathKeys.Version(version.Identity))!;
        using (var stream = new FileStream(SegmentReader.SegmentPath(f.Repository.DirectoryPath, address.SegmentId), FileMode.Open, FileAccess.ReadWrite))
        { stream.Position = address.BlockOffset + MetadataBlock.HeaderBytes; var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1)); }
        Assert.Equal(first, reader.GetTimelinePage(scope).Items); Assert.Equal(prior, reader.HistoryStatistics);
        using var fresh = f.Repository.OpenReader(); Assert.Throws<InvalidDataException>(() => fresh.GetTimelinePage(scope));
        Assert.Equal(0, fresh.TimelineCacheStatistics.Builds); Exclusive(f.Repository);
    }

    [Fact]
    public void CachedRequestsHonorOpeningCancellationAndValidatedControlExpiry()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var scope = new HistoryScope(FoundationTests.Root);
        using var cancel = new CancellationTokenSource(); using var reader = f.Repository.OpenComparisonReader(cancellationToken: cancel.Token);
        _ = reader.GetTimelinePage(scope); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.GetTimelinePage(scope)); Assert.Null(reader.Workspace!.CreatedDirectory);
        using var r = f.Repository.OpenReader(); _ = r.GetTimelinePage(scope);
        var state = Path.Combine(f.Repository.DirectoryPath, "state.json"); File.WriteAllText(state, "damaged");
        Assert.Throws<InvalidDataException>(() => r.GetTimelinePage(scope)); Assert.Equal(0, r.TimelineCacheStatistics.RetainedTimestamps);
    }

    [Theory]
    [InlineData("backup")] [InlineData("fixed-age")] [InlineData("manual")]
    [InlineData("capacity")] [InlineData("rebuild")] [InlineData("compaction")]
    public void EveryAuthorityChangeNeedsFreshTruthAndRejectsOldCursor(string change)
    {
        using var f = new HistoryFixture(); f.Add(day: 0); f.Add(day: 3); f.Add("deleted", day: 2); f.Remove("deleted");
        var scope = new HistoryScope(FoundationTests.Root); using var old = f.Repository.OpenReader();
        using var oracleA = f.Repository.OpenReader(); var expectedA = TimelineProjectionTests.Reference(oracleA, scope);
        var cursor = old.GetTimelinePage(scope, 1).Continuation!;
        if (change == "backup") f.Add("new", day: 7);
        else if (change == "fixed-age") { var e = new RetentionEngine(f.Repository); Assert.True(e.Execute(e.Preview(RetentionPolicy.OneMonth, RetentionTests.Evaluation)).Complete); }
        else if (change == "manual") { var e = new HistoryCleanupEngine(f.Repository); Assert.True(e.Execute(e.Preview(new(HistoryCleanupMode.KeepOnlyMostRecent))).Complete); }
        else if (change == "capacity") Assert.Equal(1, CapacityRetentionTests.Engine(f.Repository,
            new(new CapacityObservation(100, 0), new(100, 0), new(100, 0), new(100, 5))).EnsureCapacity(0).Removed);
        else using (var w = f.Repository.OpenWriter()) { if (change == "rebuild") w.RebuildIndexes(); else w.CompactMetadata(); }
        using var fresh = f.Repository.OpenReader(); using var oracleB = f.Repository.OpenReader();
        Assert.NotEqual(old.Authority, fresh.Authority);
        Assert.Throws<ArgumentException>(() => fresh.GetTimelinePage(scope, 1, cursor: cursor));
        Assert.Equal(TimelineProjectionTests.Reference(oracleB, scope), fresh.GetTimelinePage(scope, direction: HistoryDirection.OldestFirst).Items);
        Assert.DoesNotContain(RetentionTests.Evaluation, fresh.GetTimelinePage(scope).Items);
        Assert.Equal(1, fresh.TimelineCacheStatistics.Builds);
        if (change == "compaction") Assert.Throws<ReaderGenerationExpiredException>(() => old.GetTimelinePage(scope));
        else Assert.Equal(expectedA, old.GetTimelinePage(scope, direction: HistoryDirection.OldestFirst).Items);
    }

    [Fact]
    public void TimestampDisappearsOnlyWhenItsFinalSurvivingSupportIsRemoved()
    {
        using var f = new HistoryFixture(); var a = f.Add("a", day: 1); var b = f.Add("b", day: 1); f.Add("anchor", day: 3);
        f.Remove("a"); f.Remove("b"); var scope = new HistoryScope(FoundationTests.Root);
        using var old = f.Repository.OpenReader(); Assert.Equal(2, old.GetTimelinePage(scope).Items.Count);
        foreach (var v in new[] { a, b })
        {
            using (var w = f.Repository.OpenWriter()) using (var r = f.Repository.OpenReader()) w.RemoveVersions([v.Identity], r.Authority);
            using var fresh = f.Repository.OpenReader(); using var oracle = f.Repository.OpenReader();
            Assert.Equal(TimelineProjectionTests.Reference(oracle, scope), fresh.GetTimelinePage(scope, direction: HistoryDirection.OldestFirst).Items);
            Assert.Equal(v == a ? 2 : 1, fresh.TimelineCacheStatistics.RetainedTimestamps);
        }
        Assert.Equal(2, old.GetTimelinePage(scope).Items.Count);
    }

    private static void Exclusive(Repository repo)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg"))
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
}
