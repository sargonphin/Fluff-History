using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests.Infrastructure;

internal sealed record FrameTally(long Count, long Bytes);
internal sealed record IndexFootprint(int Height, int Pages, int Leaves, long Entries,
    int MinimumLeafEntries, int MaximumLeafEntries, long UsedBodyBytes, long AllocatedBodyBytes);
internal sealed record MetadataFootprint(long DiskBytes, int Segments, long ActiveEpochBytes, int ActiveEpochSegments,
    long HeaderBytes, long SealBytes, long ReachableFrameBytes, long UnreachableFrameBytes,
    long SupersededPageBytes, Dictionary<string, FrameTally> Physical, Dictionary<string, FrameTally> Reachable,
    Dictionary<string, IndexFootprint> Indexes, long LogicalBytes)
{
    // Physical/Reachable tally persisted/reachable decoded frames. DiskBytes is
    // encoded file length; LogicalBytes is the separately reconciled expansion.
    internal long SemanticBytes => Physical.Where(p => p.Key.StartsWith("Semantic/", StringComparison.Ordinal)).Sum(p => p.Value.Bytes);
    internal long PageBytes => Physical.Where(p => p.Key.StartsWith("Page/", StringComparison.Ordinal)).Sum(p => p.Value.Bytes);
}

/// <summary>Test-only complete segment inventory and current-authority graph walk; no production hooks.</summary>
internal static class MetadataFrameAccounting
{
    private sealed record FrameInfo(string Category, byte Level = 0, int Entries = 0, long UsedBodyBytes = 0);

    internal static MetadataFootprint Capture(Repository repository)
    {
        using var reader = repository.OpenReader();
        var frames = new Dictionary<FrameAddress, FrameInfo>();
        long diskBytes = 0, activeBytes = 0, logicalBytes = 0;
        var segmentCount = 0; var activeSegments = 0;
        foreach (var path in Directory.GetFiles(Path.Combine(repository.DirectoryPath, "metadata"), "*.seg"))
        {
            // These controlled fixtures have no abandoned tail or unknown segments. Account for
            // every physical byte, and reject rather than silently ignore unparseable data.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var headerBytes = new byte[Format.SegmentHeaderBytes]; stream.ReadExactly(headerBytes);
            var header = MetadataCodec.DecodeSegment(headerBytes);
            Assert.Equal(repository.Identity.RepositoryId, header.RepositoryId);
            diskBytes += stream.Length; segmentCount++;
            if (header.Epoch == reader.Captured.Root.Epoch) { activeBytes += stream.Length; activeSegments++; }
            // Inventory logical frames separately from physical block envelopes.
            // Diagnostic-only full expansion is bounded by one 64 MiB segment.
            using var logical = new MemoryStream(); logical.Write(headerBytes);
            var blocks = new List<BlockHeader>(); long accountedPhysical = Format.SegmentHeaderBytes;
            byte[] ReadPhysical(long at, int count) { stream.Position = at; var bytes = new byte[count]; stream.ReadExactly(bytes); return bytes; }
            for (long at = Format.SegmentHeaderBytes; at < stream.Length;)
            {
                var block = MetadataBlock.ReadHeader(ReadPhysical, header, at, stream.Length);
                Assert.Equal(logical.Length, block.Logical);
                logical.Write(MetadataBlock.Decode(ReadPhysical, header, block, stream.Length)); blocks.Add(block);
                accountedPhysical += MetadataBlock.Overhead + block.EncodedLength; at = block.End;
            }
            Assert.Equal(stream.Length, accountedPhysical); logicalBytes += logical.Length;
            logical.Position = Format.SegmentHeaderBytes; var blockIndex = 0;
            while (logical.Position < logical.Length)
            {
                var offset = logical.Position;
                while (blocks[blockIndex].LogicalEnd <= offset) blockIndex++;
                var frameHeader = new byte[Format.FrameHeaderBytes]; logical.ReadExactly(frameHeader);
                var address = MetadataCodec.ReadFrameHeader(frameHeader, header, offset) with { BlockOffset = blocks[blockIndex].Physical };
                var bytes = new byte[Format.FrameHeaderBytes + address.Length];
                frameHeader.CopyTo(bytes, 0); logical.ReadExactly(bytes.AsSpan(Format.FrameHeaderBytes));
                var body = MetadataCodec.ValidateFrame(bytes, header, address);
                FrameInfo info;
                switch (address.Kind)
                {
                    case FrameKind.Page:
                        var page = MetadataCodec.DecodePage(body, address.Serial);
                        info = new("Page/" + page.Kind, page.Level, page.Entries.Count,
                            4 + page.Entries.Sum(e => 2L + e.Key.Length + Format.AddressBytes));
                        break;
                    case FrameKind.Semantic:
                        info = new("Semantic/" + MetadataCodec.DecodeSemantic(body).Kind); break;
                    case FrameKind.Root: _ = MetadataCodec.DecodeRoot(body); info = new("Root"); break;
                    case FrameKind.Retired: _ = MetadataCodec.DecodeRetired(body); info = new("Retired"); break;
                    case FrameKind.Seal:
                        Assert.Equal(logical.Length, logical.Position);
                        SegmentReader.ValidateSeal(stream, header); info = new("Seal"); break;
                    default: throw new InvalidDataException("Unexpected diagnostic frame kind.");
                }
                frames.Add(address, info);
            }
            Assert.Equal(logical.Length, logical.Position);
        }

        var reachable = new HashSet<FrameAddress>();
        var pending = new Stack<FrameAddress>(); pending.Push(reader.Captured.Address);
        while (pending.TryPop(out var address))
        {
            if (!reachable.Add(address)) continue;
            Assert.True(frames.ContainsKey(address), "Every live address must name a physically present validated frame.");
            var body = reader.Read(address);
            switch (address.Kind)
            {
                case FrameKind.Root:
                    var root = MetadataCodec.DecodeRoot(body);
                    Push(root.Journal); Push(root.Current); Push(root.Versions); Push(root.Sources); Push(root.Retired);
                    break;
                case FrameKind.Page:
                    foreach (var entry in MetadataCodec.DecodePage(body, address.Serial).Entries) Push(entry.Value);
                    break;
                case FrameKind.Semantic:
                    var record = reader.Semantic(address); Push(record.Previous); Push(record.VersionAddress); break;
                case FrameKind.Retired: Push(MetadataCodec.DecodeRetired(body).Previous); break;
                default: throw new InvalidDataException("Unexpected live graph frame kind.");
            }
        }
        void Push(FrameAddress? address) { if (address is not null) pending.Push(address); }

        var physical = Tally(frames.Keys); var live = Tally(reachable);
        var liveBytes = reachable.Sum(Size);
        var seals = physical.GetValueOrDefault("Seal")?.Bytes ?? 0;
        var headerTotal = (long)segmentCount * Format.SegmentHeaderBytes;
        var unreach = frames.Keys.Where(a => a.Kind != FrameKind.Seal && !reachable.Contains(a)).Sum(Size);
        Assert.Equal(logicalBytes, headerTotal + seals + liveBytes + unreach);
        var supersededPages = frames.Keys.Where(a => a.Kind == FrameKind.Page && !reachable.Contains(a)).Sum(Size);
        var indexes = Enum.GetValues<IndexKind>().ToDictionary(k => k.ToString(), k =>
        {
            var pages = reachable.Where(a => frames[a].Category == "Page/" + k).Select(a => frames[a]).ToArray();
            var leaves = pages.Where(p => p.Level == 0).ToArray();
            return new IndexFootprint(pages.Length == 0 ? 0 : pages.Max(p => p.Level) + 1,
                pages.Length, leaves.Length, leaves.Sum(p => (long)p.Entries),
                leaves.Length == 0 ? 0 : leaves.Min(p => p.Entries), leaves.Length == 0 ? 0 : leaves.Max(p => p.Entries),
                pages.Sum(p => p.UsedBodyBytes), (long)pages.Length * Format.PageBytes);
        });
        return new(diskBytes, segmentCount, activeBytes, activeSegments, headerTotal, seals, liveBytes, unreach,
            supersededPages, physical, live, indexes, logicalBytes);

        Dictionary<string, FrameTally> Tally(IEnumerable<FrameAddress> addresses) =>
            addresses.GroupBy(a => frames[a].Category).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new FrameTally(g.LongCount(), g.Sum(Size)));
    }

    internal static long Size(FrameAddress address) => Format.FrameHeaderBytes + (long)address.Length;
}
