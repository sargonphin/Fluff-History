using System.Buffers.Binary;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class TimelineProjectionTests
{
    // Independent full-object reference: no production timeline decoder or grouping helper.
    internal static DateTimeOffset[] Reference(RepositoryReader reader, HistoryScope scope)
    {
        var lower = PathKeys.Scope(scope.SourceRootId, scope.Folder);
        var upper = lower.All(b => b == 255) ? null : PathKeys.UpperBound(lower);
        return reader.VersionIndex.Range(reader.Captured.Root.Versions, lower, upper)
            .Select(e => reader.Version(e.Value, versionKey: e.Key).StoredUtc).Distinct().Order().ToArray();
    }

    [Fact]
    public void EveryWindowAndPageSizeMatchesIndependentReferenceWithoutWorkspaceOrWrites()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 257, rotation: 65_536,
            storedTime: (pass, i) => FoundationTests.Time.AddDays(pass == 1 && i % 3 == 0 ? -1 : i % 5));
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var scope = new HistoryScope(FoundationTests.Root);
        using var reference = repo.OpenReader(); var expected = Reference(reference, scope);
        foreach (var size in new[] { 1, 2, 7 })
        foreach (var budget in new[] { new ComparisonReadBudget(1), new ComparisonReadBudget(3), new ComparisonReadBudget() })
        foreach (var direction in Enum.GetValues<HistoryDirection>())
        {
            using var r = repo.OpenComparisonReader(temp.Child("unused-workspace"));
            HistoryCursor? cursor = null; var actual = new List<DateTimeOffset>();
            do
            {
                var beforeFrames = r.HistoryStatistics.SemanticFramesResolved;
                var page = r.ReadTimelinePage(budget, scope, size, direction, cursor); actual.AddRange(page.Items); cursor = page.Continuation;
                var s = r.LastTimelineStatistics!; Assert.Equal(514, s.MatchingVersions); Assert.Equal(514, s.ValidatedVersions);
                Assert.Equal(beforeFrames + 514, r.HistoryStatistics.SemanticFramesResolved);
                Assert.Equal(514, s.Reads.Io.SemanticReads); Assert.InRange(s.Reads.Io.PeakHandles, 1, 8);
                Assert.InRange(s.Reads.MaximumBufferedEntries, 1, budget.MaximumEntries);
                Assert.Equal((514 + budget.MaximumEntries - 1) / budget.MaximumEntries, s.Reads.Windows);
                Assert.InRange(s.Reads.MaximumBufferedKeyBytes, 1, budget.MaximumKeyBytes);
                Assert.InRange(s.MaximumTimestampCandidates, 0, size + 1);
                Assert.Null(r.Workspace!.CreatedDirectory);
            } while (cursor is not null);
            Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), actual);
        }
        Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
    }

    [Fact]
    public void KeyByteBoundForcesMultiplePartialWindowsWithoutLosingPendingEntry()
    {
        using var f = new HistoryFixture(); var prefix = string.Join('/', Enumerable.Repeat(new string('a', 200), 9));
        for (var i = 0; i < 7; i++) f.Add(prefix + "/leaf" + i, day: i);
        using var r = f.Repository.OpenReader(); var scope = new HistoryScope(FoundationTests.Root);
        var page = r.ReadTimelinePage(new(3, Format.MaximumKeyBytes), scope, direction: HistoryDirection.OldestFirst);
        Assert.Equal(Reference(r, scope), page.Items); var s = r.LastTimelineStatistics!;
        Assert.Equal(7, s.Reads.Windows); Assert.Equal(1, s.Reads.MaximumBufferedEntries);
        Assert.InRange(s.Reads.MaximumBufferedKeyBytes, 3500, Format.MaximumKeyBytes);
    }

    [Fact]
    public void ScopedDeletedUnprotectedAndEmptyHistoriesMatchReferenceAndCursorBindings()
    {
        using var f = new HistoryFixture(); f.Add("Foo/Mixed", day: 5); f.Add("Foo/Mixed", day: 1);
        f.Add("Foo/Nested/a", day: 5); f.Add("Foo/Nested/b", day: 3); f.Add("Foo2/sibling", day: 9);
        f.Add("Foo/\u00e9/\U00010428", day: 2);
        f.Add("Foo/foreign", day: 8, root: Guid.NewGuid()); f.Remove("Foo/Mixed");
        using (var now = f.Repository.OpenReader())
        using (var w = f.Repository.OpenWriter())
            w.Apply(new(currentRemovals: now.EnumerateCurrent(FoundationTests.Root).Select(v => v.Identity.Path),
                sourceRoots: [new(FoundationTests.Root, "unprotected", false)]), now.Authority);
        using var r = f.Repository.OpenReader();
        foreach (var folder in new[] { "", "foo", "foo/nested", "empty" })
        {
            var scope = new HistoryScope(FoundationTests.Root, folder);
            Assert.Equal(Reference(r, scope), r.ReadTimelinePage(new(2), scope, direction: HistoryDirection.OldestFirst).Items);
        }
        var c = r.ReadTimelinePage(new(2), new(FoundationTests.Root), 1).Continuation!;
        Assert.Throws<ArgumentException>(() => r.ReadTimelinePage(new(2), new(FoundationTests.Root), 2, cursor: c));
        f.Add("new"); using var fresh = f.Repository.OpenReader();
        Assert.Throws<ArgumentException>(() => fresh.GetTimelinePage(new(FoundationTests.Root), 1, cursor: c));
        using var other = new HistoryFixture(); using var otherReader = other.Repository.OpenReader();
        Assert.Throws<ArgumentException>(() => otherReader.GetTimelinePage(new(FoundationTests.Root), 1, cursor: c));
    }

    [Theory]
    [InlineData("CollectEntry")]
    [InlineData("ResolveEntry")]
    [InlineData("WindowCompleted")]
    public void CancellationAtEachStageReleasesAllHandles(string stageName)
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 5; i++) f.Add(day: i);
        var stage = Enum.Parse<TimelineReadStage>(stageName);
        using var cts = new CancellationTokenSource(); using var r = f.Repository.OpenReader(); var seen = 0;
        r.TimelineObserve = s => { if (s == stage && ++seen == (stage == TimelineReadStage.WindowCompleted ? 1 : 2)) cts.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => r.ReadTimelinePage(new(2), new(FoundationTests.Root), cancellationToken: cts.Token));
        AssertExclusive(f.Repository);
    }

    [Fact]
    public void PreCancellationWorkspaceCancellationAndReaderDisposalReleaseHandles()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1);
        using (var r = f.Repository.OpenReader())
        {
            Assert.Throws<OperationCanceledException>(() => r.GetTimelinePage(new(FoundationTests.Root), cancellationToken: new(true)));
            Assert.Null(r.LastTimelineStatistics);
            r.TimelineObserve = s => { if (s == TimelineReadStage.ResolveEntry) r.Dispose(); };
            Assert.Throws<ObjectDisposedException>(() => r.GetTimelinePage(new(FoundationTests.Root)));
        }
        AssertExclusive(f.Repository);
        using var cts = new CancellationTokenSource();
        using (var r = f.Repository.OpenComparisonReader(cancellationToken: cts.Token))
        {
            r.TimelineObserve = s => { if (s == TimelineReadStage.CollectEntry) cts.Cancel(); };
            Assert.Throws<OperationCanceledException>(() => r.GetTimelinePage(new(FoundationTests.Root)));
            Assert.Null(r.Workspace!.CreatedDirectory);
        }
        AssertExclusive(f.Repository);
    }

    [Fact]
    public void RetiredCaptureExpiresWithoutRefreshAndFreshCursorIsRejected()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1);
        using var old = f.Repository.OpenReader(); var c = old.GetTimelinePage(new(FoundationTests.Root), 1).Continuation!;
        old.Segments.Dispose();
        using (var w = f.Repository.OpenWriter()) { w.CompactMetadata(); w.CleanupRetiredMetadata(); }
        using var fresh = f.Repository.OpenReader();
        Assert.Throws<ArgumentException>(() => fresh.GetTimelinePage(new(FoundationTests.Root), 1, cursor: c));
        Assert.Throws<ReaderGenerationExpiredException>(() => old.GetTimelinePage(new(FoundationTests.Root), 1, cursor: c));
        Assert.Equal(2, fresh.GetTimelinePage(new(FoundationTests.Root)).Items.Count);
    }

    [Fact]
    public void RetirementBetweenWindowsNeverReturnsMixedAuthorityOrPartialPage()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 513, rotation: 65_536);
        using var reader = repo.OpenComparisonReader(); var retired = false;
        reader.TimelineObserve = stage =>
        {
            if (stage != TimelineReadStage.WindowCompleted || retired) return;
            retired = true;
            using var writer = repo.OpenWriter(); writer.CompactMetadata();
            Assert.True(writer.CleanupRetiredMetadata().DeletedSegments > 0);
        };
        Assert.Throws<ReaderGenerationExpiredException>(() => reader.ReadTimelinePage(new(2), new(FoundationTests.Root)));
        Assert.True(retired); Assert.Null(reader.Workspace!.CreatedDirectory); AssertExclusive(repo);
    }

    [Fact]
    public void MissingRequiredCurrentGenerationSegmentIsCorruption()
    {
        using var f = new HistoryFixture(); f.Add(); using var reader = f.Repository.OpenReader();
        File.Delete(SegmentReader.SegmentPath(f.Repository.DirectoryPath, reader.Captured.Address.SegmentId));
        Assert.Throws<InvalidDataException>(() => reader.GetTimelinePage(new(FoundationTests.Root)));
    }

    [Theory]
    [InlineData("kind")] [InlineData("previous-kind")] [InlineData("previous-address")] [InlineData("previous-forward")]
    [InlineData("id")] [InlineData("sequence-zero")] [InlineData("sequence-future")] [InlineData("ordinal")]
    [InlineData("stored-zero")] [InlineData("stored-overflow")] [InlineData("creation")] [InlineData("mtime")]
    [InlineData("length")] [InlineData("attributes")] [InlineData("utf8")] [InlineData("root")] [InlineData("path")]
    [InlineData("checksum")] [InlineData("trailing")] [InlineData("key-path")] [InlineData("key-sequence")] [InlineData("key-ordinal")]
    public void RehashedMalformedVersionsAreRejectedEvenOutsideCursorBoundary(string field)
    {
        using var f = new HistoryFixture(); var v = f.Add(); f.Add(day: 2);
        using (var r = f.Repository.OpenReader())
        {
            var previous = field.StartsWith("previous-", StringComparison.Ordinal) ? r.Captured.Root.Journal : null;
            if (field == "previous-kind") previous = r.Captured.Address;
            if (field == "previous-forward") previous = previous! with { Serial = r.Captured.Address.Serial + 1 };
            var body = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, previous, Version: v));
            var key = PathKeys.Version(v.Identity);
            switch (field)
            {
                case "previous-address": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(26), 0); break;
                case "kind": body[0] = (byte)SemanticKind.CurrentUpsert; break;
                case "id": Array.Clear(body, 2, 16); break;
                case "sequence-zero": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(18), 0); break;
                case "sequence-future": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(18), r.Captured.Root.Sequence + 1); break;
                case "ordinal": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(26), 4096); break;
                case "stored-zero": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(30), 0); break;
                case "stored-overflow": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(30), long.MaxValue); break;
                case "creation": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 28), 0); break;
                case "mtime": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 20), long.MaxValue); break;
                case "length": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 36), -1); break;
                case "attributes": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 12), -1); break;
                case "utf8": body[58] = 255; break;
                case "root": Array.Clear(body, 38, 16); break;
                case "path": body[58] = (byte)'?'; break;
                case "checksum": body = body[..^1]; break;
                case "trailing": body = [.. body, 42]; break;
                case "key-path": key[17] ^= 1; break;
                case "key-sequence": BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(key.Length - 12), 2); break;
                case "key-ordinal": BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(key.Length - 4), 1); break;
            }
            using var append = new SegmentAppender(f.Repository.DirectoryPath, f.Repository.Identity, r.Captured.Root.Epoch, r.Captured);
            var pointer = append.Append(FrameKind.Semantic, body); append.Dispose();
            RewriteVersions(f.Repository, r, key, pointer);
        }
        using var bad = f.Repository.OpenReader();
        var cursor = new HistoryCursor(bad.Authority, $"time|{FoundationTests.Root:N}||OldestFirst|1", time: FoundationTests.Time.AddDays(10));
        Assert.Throws<InvalidDataException>(() => bad.GetTimelinePage(new(FoundationTests.Root), 1, HistoryDirection.OldestFirst, cursor));
        AssertExclusive(f.Repository);
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(24)]
    [InlineData(40)] [InlineData(56)] [InlineData(64)] [InlineData(96)]
    public void TimelineRejectsFrameHashAndAllHeaderBindings(int offset)
    {
        using var f = new HistoryFixture(); var v = f.Add();
        using (var r = f.Repository.OpenReader())
        {
            var prior = r.Captured.Address; var header = r.Segments.Header(prior.SegmentId, prior.SegmentOrdinal);
            var body = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, null, Version: v));
            FrameAddress p;
            using (var stream = new FileStream(SegmentReader.SegmentPath(f.Repository.DirectoryPath, prior.SegmentId), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var last = new MetadataBlockReader(stream, header, new()).Last(prior); stream.Position = stream.Length;
                var encoded = MetadataCodec.EncodeFrame(header, prior.End, prior.Serial + 1, FrameKind.Semantic, body, stream.Position);
                p = encoded.Address; encoded.Bytes[offset] ^= 1;
                MetadataBlock.Write(stream, header, prior.End, last.Physical, encoded.Bytes); stream.Flush(true);
            }
            RewriteVersions(f.Repository, r, PathKeys.Version(v.Identity), p);
        }
        using var bad = f.Repository.OpenReader();
        Assert.Throws<InvalidDataException>(() => bad.GetTimelinePage(new(FoundationTests.Root)));
        AssertExclusive(f.Repository);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TimelineValidatesClosedSegmentHeaderAndSeal(bool seal)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 257, rotation: 65_536);
        FrameAddress p; using (var r = repo.OpenReader()) p = r.VersionIndex.Range(r.Captured.Root.Versions).First().Value;
        using (var stream = new FileStream(SegmentReader.SegmentPath(repo.DirectoryPath, p.SegmentId), FileMode.Open, FileAccess.ReadWrite))
        { stream.Position = seal ? stream.Length - 1 : 128; var b = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(b ^ 1)); }
        using var bad = repo.OpenReader(); Assert.Throws<InvalidDataException>(() => bad.GetTimelinePage(new(FoundationTests.Root)));
    }

    private static void RewriteVersions(Repository repo, RepositoryReader reader, byte[] key, FrameAddress pointer)
    {
        var baseline = reader.Captured with { Address = pointer };
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, baseline);
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
        var versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(null, [(key, pointer)]);
        var root = reader.Captured.Root with { Generation = reader.Captured.Root.Generation + 1, PreviousGeneration = reader.Captured.Root.Generation,
            PreviousTransactionId = reader.Captured.Root.TransactionId, PreviousRootHash = reader.Captured.Address.Sha256,
            TransactionId = Guid.NewGuid(), Versions = versions };
        var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
        AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, address)));
        AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
    }
    private static Dictionary<string, (long, DateTime)> Inventory(Repository repo) => Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
    private static void AssertExclusive(Repository repo)
    {
        foreach (var p in Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg"))
            using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
}
