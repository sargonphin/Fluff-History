using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class ScalingTests(ITestOutputHelper output)
{
    [Fact]
    public void TwoHundredFiftyThousandPackedKeysHaveBoundedBulkAndSparseWrites()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var baseline = repo.OpenReader();
        using var append = new SegmentAppender(temp.Child(), repo.Identity, baseline.Captured.Root.Epoch, baseline.Captured);
        var leafValue = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source")))); // Index-only fixture: no payload/copy implementation is involved.
        long reads = 0;
        byte[] Read(FrameAddress p) { reads++; return append.ReadNew(p) ?? baseline.Read(p); }
        var current = new OrderedIndex(IndexKind.Current, Read, append); var history = new OrderedIndex(IndexKind.Versions, Read, append);
        FrameAddress? currentRoot = null, historyRoot = null;
        for (var start = 0; start < 250000; start += 4096)
        {
            var count = Math.Min(4096, 250000 - start);
            var paths = Enumerable.Range(start, count).Select(i => new HistoryPath(FoundationTests.Root, $"f{i:D6}")).ToArray();
            currentRoot = current.Update(currentRoot, paths.Select(p => (PathKeys.Current(p), (FrameAddress?)leafValue)).ToArray());
            historyRoot = history.Update(historyRoot, paths.Select(p => (PathKeys.Version(new(p, 1, 0, Guid.NewGuid())), (FrameAddress?)leafValue)).ToArray());
        }
        append.FlushAndValidate(); var bulkPages = append.Statistics.PagesWritten; var bulkBytes = append.Statistics.BytesWritten;
        Assert.InRange(bulkPages, 3900, 5000);
        var target = new HistoryPath(FoundationTests.Root, "f125001"); var next = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentRemoved, leafValue, Path: target)));
        currentRoot = current.Update(currentRoot, [(PathKeys.Current(target), next)]);
        historyRoot = history.Update(historyRoot, [(PathKeys.Version(new(target, 2, 0, Guid.NewGuid())), next)]);
        Assert.InRange(append.Statistics.PagesWritten - bulkPages, 2, 8);
        reads = 0; var freshCurrent = new OrderedIndex(IndexKind.Current, Read); Assert.Equal(next, freshCurrent.Find(currentRoot, PathKeys.Current(target))); Assert.InRange(reads, 1, 4);
        reads = 0; var freshHistory = new OrderedIndex(IndexKind.Versions, Read); var prefix = PathKeys.Current(target);
        Assert.Equal(2, freshHistory.Range(historyRoot, prefix, PathKeys.UpperBound(prefix)).Count()); Assert.InRange(reads, 1, 5);
        Assert.Equal(250000, current.Range(currentRoot).Count()); append.FlushAndValidate();
        var segments = Directory.GetFiles(Path.Combine(temp.Child(), "metadata"), "*.seg"); Assert.InRange(segments.Length, 2, 4);
        output.WriteLine($"250k keys in both indexes: bulk pages={bulkPages}, bulk frame bytes={bulkBytes}, sparse pages={append.Statistics.PagesWritten - bulkPages}, segment files={segments.Length}, managed live bytes={GC.GetTotalMemory(false)}.");
    }
    [Fact]
    public void ForcedRotationAndManySmallMutationsKeepDirectQueriesExact()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var w = repo.OpenWriter())
        {
            w.RotationBytes = 131072;
            for (var i = 0; i < 20; i++) FoundationTests.Add(repo, w, $"f{i:D3}");
        }
        using (var r = Repository.Open(temp.Child()).OpenReader())
        {
            Assert.Equal(0, r.Segments.Statistics.SemanticReads);
            Assert.NotNull(r.GetCurrent(new(FoundationTests.Root, "f019"))); Assert.Equal(1, r.Segments.Statistics.SemanticReads);
            Assert.Equal(20, r.EnumerateCurrent().Count()); Assert.Single(r.EnumerateVersions(new(FoundationTests.Root, "f000")));
        }
        using (var w = repo.OpenWriter()) { w.CompactMetadata(); w.CleanupRetiredMetadata(); }
        using var fresh = repo.OpenReader(); Assert.Equal(20, fresh.EnumerateCurrent().Count());
    }
    [Fact]
    public void ManyDefaultSizeMutationsShareSegmentAndCompactionReducesObsoleteBytes()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using var w = repo.OpenWriter(); for (var i = 0; i < 30; i++) FoundationTests.Add(repo, w);
        var segments = Path.Combine(temp.Child(), "metadata"); Assert.Single(Directory.GetFiles(segments)); var before = Directory.GetFiles(segments).Sum(p => new FileInfo(p).Length);
        w.CompactMetadata(); w.CleanupRetiredMetadata(); var after = Directory.GetFiles(segments).Sum(p => new FileInfo(p).Length); Assert.True(after < before / 3);
        using var r = repo.OpenReader(); Assert.Equal(30, r.EnumerateVersions(new(FoundationTests.Root, "foo.txt")).Count()); Assert.Single(r.EnumerateCurrent());
    }
}
