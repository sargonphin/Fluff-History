using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class IndexConstructionTests
{
    [Fact]
    public void ScatteredVersionsHaveBalancedLeavesAndPointUpdatesReuseUntouchedBranches()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var baseline = repo.OpenReader();
        using var append = new SegmentAppender(temp.Child(), repo.Identity, baseline.Captured.Root.Epoch, baseline.Captured);
        var value = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source"))));
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? baseline.Read(p);
        var index = new OrderedIndex(IndexKind.Versions, Read, append); FrameAddress? root = null;
        byte[] Key(int i, int sequence) => PathKeys.Version(new(new(FoundationTests.Root, $"f{i:D6}"), sequence, 0, Guid.NewGuid()));
        var order = Enumerable.Range(0, 5_000).ToArray();
        for (var pass = 1; pass <= 2; pass++)
        {
            if (pass == 2) new Random(713).Shuffle(order);
            foreach (var batch in order.Chunk(4096)) root = index.Update(root, batch.Select(i => (Key(i, pass), (FrameAddress?)value)).ToArray());
        }
        var oldLeaves = Pages(index, root!).Where(p => p.Page.Level == 0).ToArray();
        Assert.Equal(10_000, index.Range(root).Count());
        var byteCapacity = Math.Min(Format.MaximumPageEntries, (Format.PageBytes - 4) / (2 + Key(0, 1).Length + Format.AddressBytes));
        Assert.All(oldLeaves, p => Assert.InRange(p.Page.Entries.Count, byteCapacity / 2, byteCapacity));
        var before = append.Statistics.PagesWritten;
        root = index.Update(root, [(Key(2_499, 3), value)]);
        Assert.InRange(append.Statistics.PagesWritten - before, 1, 8);
        var leaves = Pages(index, root!).Where(p => p.Page.Level == 0).Select(p => p.Address).ToHashSet();
        Assert.True(oldLeaves.Count(p => leaves.Contains(p.Address)) >= oldLeaves.Length - 2);
        Assert.Equal(10_001, index.Range(root).Count()); append.FlushAndValidate();
    }

    [Fact]
    public void SortedBulkBuildStreamsVariableKeysAndEmitsOnlyReachablePages()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var baseline = repo.OpenReader();
        using var append = new SegmentAppender(temp.Child(), repo.Identity, baseline.Captured.Root.Epoch, baseline.Captured);
        var value = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source"))));
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? baseline.Read(p);
        var index = new OrderedIndex(IndexKind.Current, Read, append); var consumed = 0;
        IEnumerable<PageEntry> Entries()
        {
            for (var i = 0; i < 2_000; i++)
            {
                consumed++;
                if (i == 1_000) Assert.True(append.Statistics.PagesWritten > 0, "Bulk input must be consumed while emitting pages, not collected first.");
                yield return new(PathKeys.Current(new(FoundationTests.Root, $"f{i:D6}/" + new string('x', 1 + i * 53 % 900))), value);
            }
        }
        var root = index.BuildSorted(Entries());
        Assert.Equal(2_000, consumed); Assert.Equal(2_000, index.Range(root).Count());
        var pages = Pages(index, root!).ToArray();
        Assert.Equal(append.Statistics.PagesWritten, pages.Length);
        Assert.All(pages, p => Assert.Equal(Format.PageBytes, MetadataCodec.EncodePage(p.Page).Length));
        Assert.Null(index.BuildSorted([])); append.FlushAndValidate();
    }

    [Fact]
    public void BalancedIncrementalSplitsRespectVariableKeyByteLimits()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var baseline = repo.OpenReader();
        using var append = new SegmentAppender(temp.Child(), repo.Identity, baseline.Captured.Root.Epoch, baseline.Captured);
        var value = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source"))));
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? baseline.Read(p);
        var index = new OrderedIndex(IndexKind.Versions, Read, append); FrameAddress? root = null;
        var order = Enumerable.Range(0, 700).ToArray();
        for (var pass = 1; pass <= 2; pass++)
        {
            if (pass == 2) new Random(17).Shuffle(order);
            foreach (var batch in order.Chunk(128))
                root = index.Update(root, batch.Select(i =>
                {
                    var path = new HistoryPath(FoundationTests.Root, $"f{i:D6}/" + new string('x', 1 + i * 79 % 1_800));
                    return (PathKeys.Version(new(path, pass, 0, Guid.NewGuid())), (FrameAddress?)value);
                }).ToArray());
        }
        Assert.Equal(1_400, index.Range(root).Count());
        foreach (var page in Pages(index, root!))
            _ = MetadataCodec.DecodePage(MetadataCodec.EncodePage(page.Page), page.Address.Serial);
        append.FlushAndValidate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkBuildRejectsDuplicateOrDescendingKeys(bool descending)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        using var append = new SegmentAppender(temp.Child(), repo.Identity, reader.Captured.Root.Epoch, reader.Captured);
        var pointer = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source"))));
        var index = new OrderedIndex(IndexKind.Current, p => append.ReadNew(p)!, append);
        var a = new PageEntry(PathKeys.Current(new(FoundationTests.Root, "a")), pointer);
        var b = new PageEntry(PathKeys.Current(new(FoundationTests.Root, "b")), pointer);
        Assert.Throws<InvalidDataException>(() => index.BuildSorted(descending ? [b, a] : [a, a]));
    }

    private static IEnumerable<(FrameAddress Address, IndexPage Page)> Pages(OrderedIndex index, FrameAddress root)
    {
        var page = index.Page(root); yield return (root, page);
        if (page.Level > 0)
            foreach (var entry in page.Entries)
                foreach (var child in Pages(index, entry.Value)) yield return child;
    }
}
