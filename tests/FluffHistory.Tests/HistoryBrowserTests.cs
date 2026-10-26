using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

internal sealed class HistoryFixture : IDisposable
{
    internal readonly TemporaryDirectory Temp = new();
    internal Repository Repository { get; }
    internal static Guid Root => FoundationTests.Root;
    internal static DateTimeOffset Time => FoundationTests.Time;
    internal HistoryFixture(bool encrypted = false) => Repository = encrypted ? Repository.CreateEncrypted(Temp.Child(), EncryptionTests.Password) : Repository.Create(Temp.Child());
    internal StoredVersion Add(string path = "a", string content = "hello", int day = 0, Guid? root = null,
        DateTimeOffset? mtime = null, FileAttributes attributes = FileAttributes.Archive)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content); var id = root ?? Root;
        using var reader = Repository.OpenReader(); using var writer = Repository.OpenWriter();
        using var draft = writer.CreatePayloadDraft(); draft.Write(bytes); draft.CompleteCopy(bytes.Length);
        var prepared = draft.PrepareVersion(new(id, path, bytes.Length, Time, mtime ?? Time.AddMinutes(day), attributes),
            Time.AddDays(day), new(Convert.ToHexString(XxHash64.Hash(bytes))));
        using var mutation = writer.PrepareMutation(new([prepared], sourceRoots: [new(id, "descriptive-only")]), reader.Authority);
        draft.Install(); mutation.Commit(); using var fresh = Repository.OpenReader(); return fresh.GetNewestVersion(new(id, path))!;
    }
    internal void Remove(string path, bool unprotect = false)
    {
        using var reader = Repository.OpenReader(); using var writer = Repository.OpenWriter();
        writer.Apply(new(currentRemovals: [new(Root, path)], sourceRoots: unprotect ? [new(Root, "descriptive-only", false)] : null), reader.Authority);
    }
    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Temp.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Temp.Dispose();
    }
}

public sealed class HistoryBrowserTests
{
    [Fact]
    public void ExactLookupNeverSubstitutesUuidPathOrderOrAnotherRepository()
    {
        using var f = new HistoryFixture(); var a = f.Add(); f.Add(content: "new"); using var r = f.Repository.OpenReader();
        Assert.Equal(a, r.GetVersion(a.Identity));
        Assert.Null(r.GetVersion(a.Identity with { VersionId = Guid.NewGuid() }));
        Assert.Null(r.GetVersion(a.Identity with { Sequence = 999 }));
        Assert.Null(r.GetVersion(a.Identity with { Ordinal = 1 }));
        Assert.Null(r.GetVersion(a.Identity with { Path = new(HistoryFixture.Root, "other") }));
        using var other = new HistoryFixture(); using var o = other.Repository.OpenReader(); Assert.Null(o.GetVersion(a.Identity));
        using var heldWriter = f.Repository.OpenWriter(); using var payload = r.OpenVersionPayload(a.Identity)!;
        Assert.False(payload.Content.CanWrite); Assert.Equal(a, payload.Version); Assert.Equal(5, payload.Content.Length);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void PathPagesHaveNoGapsAndReadOnlyBoundedFrames(int size)
    {
        using var f = new HistoryFixture(); var expected = Enumerable.Range(0, 7).Select(i => f.Add(day: i)).ToArray(); f.Add("unrelated");
        foreach (var direction in Enum.GetValues<HistoryDirection>())
        {
            using var r = f.Repository.OpenReader(); HistoryCursor? cursor = null; var found = new List<StoredVersion>();
            do
            {
                var before = r.HistoryStatistics.SemanticFramesResolved;
                var page = r.GetPathHistoryPage(new(HistoryFixture.Root, "A"), size, direction, cursor); cursor = page.Continuation;
                Assert.InRange(page.Items.Count, 1, size); Assert.InRange(r.HistoryStatistics.SemanticFramesResolved - before, 1, size);
                found.AddRange(page.Items);
            } while (cursor is not null);
            Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), found);
        }
    }

    [Fact]
    public void CursorsRejectDifferentQueriesAndFreshAuthoritiesButOldCaptureStaysConsistent()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); f.Add("folder/a");
        using var r = f.Repository.OpenReader(); var cursor = r.GetPathHistoryPage(new(HistoryFixture.Root, "a"), 1).Continuation!;
        Assert.Throws<ArgumentException>(() => r.GetPathHistoryPage(new(HistoryFixture.Root, "b"), 1, cursor: cursor));
        Assert.Throws<ArgumentException>(() => r.GetPathHistoryPage(new(HistoryFixture.Root, "a"), 2, cursor: cursor));
        Assert.Throws<ArgumentException>(() => r.GetScopeHistoryPage(new(HistoryFixture.Root), pageSize: 1, cursor: cursor));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.GetPathHistoryPage(new(HistoryFixture.Root, "a"), 0));
        f.Add("new"); using var fresh = f.Repository.OpenReader();
        Assert.Throws<ArgumentException>(() => fresh.GetPathHistoryPage(new(HistoryFixture.Root, "a"), 1, cursor: cursor));
        Assert.Single(r.GetPathHistoryPage(new(HistoryFixture.Root, "a"), 1, cursor: cursor).Items);
        var scopeCursor = r.GetScopeHistoryPage(new(HistoryFixture.Root), pageSize: 1).Continuation!;
        Assert.Throws<ArgumentException>(() => r.GetScopeHistoryPage(new(HistoryFixture.Root, "folder"), pageSize: 1, cursor: scopeCursor));
        Assert.Throws<ArgumentException>(() => r.GetScopeHistoryPage(new(HistoryFixture.Root), new(HistoryFixture.Time), 1, scopeCursor));
    }

    [Fact]
    public void ScopeTimeAndTimelineUseRecordedTimesDespiteRollbackDeletionAndUnprotection()
    {
        using var f = new HistoryFixture(); var laterTime = f.Add("Foo/Mixed.txt", day: 5); f.Add("Foo/Mixed.txt", "rolled", 2);
        var tied = f.Add("Foo/Mixed.txt", "tie", 5); var latest = f.Add("Foo/Mixed.txt", "latest", 1);
        f.Add("Foo2/outside", day: 9); f.Add("Foo/too-new", day: 10); f.Add("Other/own", day: 7);
        var other = Guid.NewGuid(); f.Add("Foo/foreign", root: other);
        f.Remove("Foo/Mixed.txt");
        using var r = f.Repository.OpenReader(); var scope = new HistoryScope(HistoryFixture.Root, "foo");
        Assert.Equal(latest, r.GetScopeHistoryPage(scope).Items.Single(i => i.Version.Identity.Path == latest.Identity.Path).Version);
        var selected = Assert.Single(r.GetScopeHistoryPage(scope, new(HistoryFixture.Time.AddDays(5))).Items).Version;
        Assert.Equal(tied, selected); Assert.NotEqual(laterTime.Identity, selected.Identity); Assert.Equal("Foo/Mixed.txt", selected.Metadata.RelativePath);
        Assert.Empty(r.GetScopeHistoryPage(scope, new(HistoryFixture.Time.AddTicks(-1))).Items);
        var times = r.GetTimelinePage(scope, direction: HistoryDirection.OldestFirst).Items;
        Assert.Equal(new[] { 1, 2, 5, 10 }.Select(d => HistoryFixture.Time.AddDays(d)), times);
        var rootTimes = r.GetTimelinePage(new(HistoryFixture.Root), direction: HistoryDirection.OldestFirst).Items;
        Assert.Equal(6, rootTimes.Count); Assert.Equal(4, r.GetScopeHistoryPage(new(HistoryFixture.Root)).Items.Count);
        // Unprotection does not filter surviving histories or add a time.
        using (var w = f.Repository.OpenWriter())
        {
            using var now = f.Repository.OpenReader(); var removals = now.EnumerateCurrent(HistoryFixture.Root).Select(v => v.Identity.Path).ToArray();
            w.Apply(new(currentRemovals: removals, sourceRoots: [new(HistoryFixture.Root, "new description", false)]), now.Authority);
        }
        using var after = f.Repository.OpenReader(); Assert.Equal(rootTimes, after.GetTimelinePage(new(HistoryFixture.Root), direction: HistoryDirection.OldestFirst).Items);
        Assert.Equal(4, after.GetScopeHistoryPage(new(HistoryFixture.Root)).Items.Count);
    }

    [Fact]
    public void TimelinePagingCoalescesAndOrdersDistinctTimesAndRejectsWrongCursors()
    {
        using var f = new HistoryFixture(); foreach (var day in new[] { 4, 1, 4, 2, 7, 2 }) f.Add(day: day);
        using var r = f.Repository.OpenReader();
        foreach (var direction in Enum.GetValues<HistoryDirection>())
        {
            HistoryCursor? cursor = null; var times = new List<DateTimeOffset>();
            do { var page = r.GetTimelinePage(new(HistoryFixture.Root), 1, direction, cursor); times.AddRange(page.Items); cursor = page.Continuation; } while (cursor is not null);
            var expected = new[] { 1, 2, 4, 7 }.Select(d => HistoryFixture.Time.AddDays(d));
            Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), times);
        }
        var c = r.GetTimelinePage(new(HistoryFixture.Root), 1).Continuation!;
        Assert.Throws<ArgumentException>(() => r.GetTimelinePage(new(HistoryFixture.Root, "sub"), 1, cursor: c));
        Assert.Throws<ArgumentException>(() => r.GetTimelinePage(new(HistoryFixture.Root), 1, HistoryDirection.OldestFirst, c));
    }

    [Fact]
    public void ScopePagesSkipIneligiblePathsAndPreserveConflictsAcrossEveryPageBoundary()
    {
        using var f = new HistoryFixture(); var parent = f.Add("Foo", day: 0); f.Add("Foo.txt"); var child = f.Add("Foo/Bar.txt", day: 1);
        for (var i = 0; i < 12; i++) { f.Add($"new{i}", day: 10); if (i % 3 == 0) f.Add($"new{i}", day: 2); }
        using var r = f.Repository.OpenReader(); var root = new HistoryScope(HistoryFixture.Root); HistoryCursor? cursor = null;
        var all = new List<ScopeHistoryItem>();
        do { var page = r.GetScopeHistoryPage(root, new(HistoryFixture.Time.AddDays(2)), 1, cursor); all.AddRange(page.Items); cursor = page.Continuation; } while (cursor is not null);
        Assert.Equal(7, all.Count); Assert.Equal(all.Select(i => i.Version.Identity.Path.CanonicalPath).Order(StringComparer.Ordinal), all.Select(i => i.Version.Identity.Path.CanonicalPath));
        Assert.Equal(child.Identity.Path, all.Single(i => i.Version.Identity == parent.Identity).DescendantConflict!.Descendant);
        Assert.Equal(parent.Identity.Path, all.Single(i => i.Version.Identity == child.Identity).AncestorConflict!.File);
        Assert.False(Assert.Single(r.GetScopeHistoryPage(new(HistoryFixture.Root, "foo")).Items).HasConflict);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void IndexKeySemanticBindingStillFailsExactAndPagedQueries(bool wrongPath)
    {
        using var f = new HistoryFixture(); var version = f.Add();
        using (var r = f.Repository.OpenReader())
        using (var append = new SegmentAppender(f.Repository.DirectoryPath, f.Repository.Identity, r.Captured.Root.Epoch, r.Captured))
        {
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? r.Read(p);
            var index = new OrderedIndex(IndexKind.Versions, Read, append);
            var invalidIdentity = wrongPath ? version.Identity with { Path = new(HistoryFixture.Root, "wrong") } : version.Identity with { Sequence = 2 };
            var key = PathKeys.Version(invalidIdentity);
            var pointer = r.VersionIndex.Find(r.Captured.Root.Versions, PathKeys.Version(version.Identity))!;
            var root = r.Captured.Root with { Generation = r.Captured.Root.Generation + 1, PreviousGeneration = r.Captured.Root.Generation,
                PreviousTransactionId = r.Captured.Root.TransactionId, PreviousRootHash = r.Captured.Address.Sha256,
                TransactionId = Guid.NewGuid(), Versions = index.Update(r.Captured.Root.Versions, [(key, pointer)]) };
            var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            AuthorityFiles.WriteNew(Path.Combine(f.Repository.DirectoryPath, "test.tmp"), AuthorityFiles.StateBytes(new(f.Repository.Identity, root, address)));
            AuthorityFiles.Replace(f.Repository.DirectoryPath, "test.tmp");
        }
        using var bad = f.Repository.OpenReader();
        Assert.Throws<InvalidDataException>(() => bad.GetVersion(wrongPath ? version.Identity with { Path = new(HistoryFixture.Root, "wrong") } : version.Identity with { Sequence = 2 }));
        Assert.Throws<InvalidDataException>(() => bad.GetScopeHistoryPage(new(HistoryFixture.Root)));
    }

    [Fact]
    public void CompactionInvalidatesFreshReaderCursorsAndUnpinnedOldCaptureExpiresExplicitly()
    {
        using var f = new HistoryFixture(); var first = f.Add(); f.Add(day: 1);
        using var old = f.Repository.OpenReader(); var cursor = old.GetPathHistoryPage(first.Identity.Path, 1).Continuation!;
        old.Segments.Dispose(); // Simulate the existing bounded reader evicting its old segment handle.
        using (var writer = f.Repository.OpenWriter()) { writer.CompactMetadata(); writer.CleanupRetiredMetadata(); }
        using var fresh = f.Repository.OpenReader();
        Assert.Throws<ArgumentException>(() => fresh.GetPathHistoryPage(first.Identity.Path, 1, cursor: cursor));
        Assert.Throws<ReaderGenerationExpiredException>(() => old.GetVersion(first.Identity));
        Assert.Equal(first, fresh.GetVersion(first.Identity));
    }

    [Fact]
    public void CorruptRequiredFrameFailsPathPageAndExactPayloadResolution()
    {
        using var f = new HistoryFixture(); var version = f.Add(); f.Add(day: 1); FrameAddress pointer;
        using (var r = f.Repository.OpenReader()) pointer = r.VersionIndex.Find(r.Captured.Root.Versions, PathKeys.Version(version.Identity))!;
        using (var stream = new FileStream(SegmentReader.SegmentPath(f.Repository.DirectoryPath, pointer.SegmentId), FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = pointer.BlockOffset + MetadataBlock.HeaderBytes; var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1));
        }
        using var bad = f.Repository.OpenReader();
        Assert.Throws<InvalidDataException>(() => bad.GetPathHistoryPage(version.Identity.Path));
        Assert.Throws<InvalidDataException>(() => bad.OpenVersionPayload(version.Identity));
    }

    [Fact]
    public void EqualStoredTimeAndSequenceUsesOrdinalTieBreakInRepresentableVersionKeys()
    {
        using var f = new HistoryFixture(); var first = f.Add(); VersionIdentity? expected = null;
        using (var r = f.Repository.OpenReader())
        using (var append = new SegmentAppender(f.Repository.DirectoryPath, f.Repository.Identity, r.Captured.Root.Epoch, r.Captured))
        {
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? r.Read(p);
            var index = new OrderedIndex(IndexKind.Versions, Read, append); var root = r.Captured.Root; var versions = root.Versions; var journal = root.Journal;
            // Reader contract fixture: v2 keys encode ordinal independently of sequence.
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                expected = first.Identity with { Sequence = root.Sequence + 1, Ordinal = ordinal, VersionId = Guid.NewGuid() };
                var version = new StoredVersion(expected, first.Metadata, first.StoredUtc, first.Checksum);
                journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, journal, Version: version)));
                versions = index.Update(versions, [(PathKeys.Version(expected), journal)]);
            }
            root = root with { Generation = root.Generation + 1, Sequence = root.Sequence + 1, PreviousGeneration = root.Generation,
                PreviousTransactionId = root.TransactionId, PreviousRootHash = r.Captured.Address.Sha256, TransactionId = Guid.NewGuid(), Versions = versions, Journal = journal };
            var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            AuthorityFiles.WriteNew(Path.Combine(f.Repository.DirectoryPath, "test.tmp"), AuthorityFiles.StateBytes(new(f.Repository.Identity, root, address)));
            AuthorityFiles.Replace(f.Repository.DirectoryPath, "test.tmp");
        }
        using var fresh = f.Repository.OpenReader();
        Assert.Equal(expected, Assert.Single(fresh.GetScopeHistoryPage(new(HistoryFixture.Root), new(first.StoredUtc)).Items).Version.Identity);
    }

    [Fact]
    public void EmptyQueriesStillHonorCancellationAndExplicitPageBounds()
    {
        using var f = new HistoryFixture(); using var reader = f.Repository.OpenReader(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var scope = new HistoryScope(HistoryFixture.Root);
        Assert.Throws<OperationCanceledException>(() => reader.GetScopeHistoryPage(scope, cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => reader.GetTimelinePage(scope, cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => reader.GetPathHistoryPage(new(HistoryFixture.Root, "a"), cancellationToken: cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetScopeHistoryPage(scope, pageSize: 4097));
    }

    [Fact]
    public void MaximumRootUuidUsesUnboundedUpperRangeWithoutLeakingOtherRoots()
    {
        using var f = new HistoryFixture(); f.Add("other"); var maximum = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        StoredVersion selected;
        // The legacy writer's filtered-current helper cannot construct the all-FF root.
        // Seed this representable v2 edge through test-only production codecs/indexes.
        using (var r = f.Repository.OpenReader())
        using (var append = new SegmentAppender(f.Repository.DirectoryPath, f.Repository.Identity, r.Captured.Root.Epoch, r.Captured))
        {
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? r.Read(p);
            var versions = new OrderedIndex(IndexKind.Versions, Read, append); var sources = new OrderedIndex(IndexKind.Sources, Read, append);
            var current = new OrderedIndex(IndexKind.Current, Read, append); var root = r.Captured.Root;
            var source = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, root.Journal, Source: new(maximum, "maximum"))));
            var sourceRoot = sources.Update(root.Sources, [(PathKeys.Source(maximum), source)]);
            selected = new(new(new(maximum, "folder/selected"), root.Sequence + 1, 0, Guid.NewGuid()),
                new(maximum, "folder/selected", 0, HistoryFixture.Time, HistoryFixture.Time, 0), HistoryFixture.Time, new("EF46DB3751D8E999"));
            var pointer = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, source, Version: selected)));
            var versionRoot = versions.Update(root.Versions, [(PathKeys.Version(selected.Identity), pointer)]);
            var currentRoot = current.Update(root.Current, [(PathKeys.Current(selected.Identity.Path), pointer)]);
            var journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, pointer, Path: selected.Identity.Path, VersionAddress: pointer)));
            root = root with { Generation = root.Generation + 1, Sequence = root.Sequence + 1, PreviousGeneration = root.Generation,
                PreviousTransactionId = root.TransactionId, PreviousRootHash = r.Captured.Address.Sha256, TransactionId = Guid.NewGuid(),
                Versions = versionRoot, Current = currentRoot, Sources = sourceRoot, Journal = journal };
            var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            AuthorityFiles.WriteNew(Path.Combine(f.Repository.DirectoryPath, "test.tmp"), AuthorityFiles.StateBytes(new(f.Repository.Identity, root, address)));
            AuthorityFiles.Replace(f.Repository.DirectoryPath, "test.tmp");
        }
        using var reader = f.Repository.OpenReader();
        Assert.Equal(selected, Assert.Single(reader.GetScopeHistoryPage(new(maximum)).Items).Version);
        Assert.Single(reader.GetScopeHistoryPage(new(maximum, "folder")).Items);
        Assert.Equal(new[] { selected.StoredUtc }, reader.GetTimelinePage(new(maximum)).Items);
    }
}
