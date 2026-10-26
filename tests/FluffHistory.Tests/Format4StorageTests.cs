using System.Buffers.Binary;
using System.Security.Cryptography;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class Format4StorageTests
{
    [Theory]
    [InlineData(8, 3)] [InlineData(12, 9)] [InlineData(56, 0)] [InlineData(56, int.MaxValue)]
    [InlineData(60, int.MaxValue)] [InlineData(80, 2)] [InlineData(84, 1)]
    public void RehashedInvalidBlockFieldsFailBeforeAllocation(int offset, int value)
    {
        var h = Header(); using var file = new MemoryStream(); file.Write(MetadataCodec.EncodeSegment(h));
        MetadataBlock.Write(file, h, 160, 0, new byte[1234]); var bytes = file.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(160 + offset), value);
        SHA256.HashData(bytes.AsSpan(160, 128)).CopyTo(bytes, 160 + 128);
        byte[] Read(long at, int count) => bytes.AsSpan((int)at, count).ToArray();
        Assert.Throws<InvalidDataException>(() => MetadataBlock.ReadHeader(Read, h, 160, bytes.Length));
    }

    [Theory]
    [InlineData("gap")] [InlineData("overlap")] [InlineData("identity")]
    [InlineData("forward")] [InlineData("self")] [InlineData("truncated")]
    [InlineData("data")] [InlineData("decoded-hash")] [InlineData("footer")]
    public void BlockMappingAndIntegrityFailClosed(string problem)
    {
        var segment = Header(); using var file = new MemoryStream(); file.Write(MetadataCodec.EncodeSegment(segment));
        var first = MetadataBlock.Write(file, segment, 160, 0, new byte[1000]);
        var next = MetadataBlock.Write(file, segment, first.LogicalEnd, first.Physical, new byte[500]);
        var bytes = file.ToArray(); var at = (int)next.Physical;
        switch (problem)
        {
            case "gap": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at + 48), next.Logical + 1); break;
            case "overlap": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at + 48), next.Logical - 1); break;
            case "identity": bytes[at + 16] ^= 1; break;
            case "forward": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at + 64), next.Physical); break;
            case "self": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at + 72), next.Physical + 1); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "data": bytes[at + MetadataBlock.HeaderBytes] ^= 1; break;
            case "decoded-hash": bytes[at + 96] ^= 1; break;
            case "footer": bytes[^1] ^= 1; break;
        }
        if (problem is "gap" or "overlap" or "identity" or "forward" or "self" or "decoded-hash")
        {
            SHA256.HashData(bytes.AsSpan(at, 128)).CopyTo(bytes, at + 128);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(bytes.AsSpan(at, MetadataBlock.HeaderBytes + next.EncodedLength)); hash.GetHashAndReset().CopyTo(bytes, bytes.Length - 32);
        }
        byte[] Read(long offset, int count) => bytes.AsSpan((int)offset, count).ToArray();
        Assert.Throws<InvalidDataException>(() => MetadataBlock.Decode(Read, segment,
            MetadataBlock.ReadHeader(Read, segment, next.Physical, bytes.Length), bytes.Length));
    }

    [Fact]
    public void SparseMutationFetchesRangesAndPreservesEveryCommittedPhysicalByte()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        var prior = repo.ReadAuthority();
        using (var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, prior.Root.Epoch, prior))
        {
            // Valid unreachable frames enlarge the active segment without adding history.
            for (var i = 0; i < 128; i++)
                append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null,
                    Source: new(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(24000))))));
            var root = prior.Root with { Generation = prior.Root.Generation + 1, PreviousGeneration = prior.Root.Generation,
                TransactionId = Guid.NewGuid(), PreviousTransactionId = prior.Root.TransactionId, PreviousRootHash = prior.Address.Sha256 };
            var pointer = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "large.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, pointer)));
            AuthorityFiles.Replace(repo.DirectoryPath, "large.tmp");
        }
        var prefixes = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg").ToDictionary(p => p, File.ReadAllBytes);
        Assert.True(prefixes.Values.Sum(v => v.Length) > 3_000_000);
        using var writer2 = repo.OpenWriter(temp.Child("work")); using var reader = repo.OpenReader();
        var estimate = writer2.EstimatePublicationAllocation(new(Versions: 1), 4096);
        using var prepared = writer2.PrepareMutation(new([FoundationTests.Prepare(writer2, "one-file")]), reader.Authority);
        Assert.InRange(prepared.Workspace.DestinationBytesFetched, 1, 512 * 1024);
        var appended = prepared.Transfers.Sum(t => t.FinalLength - t.OriginalLength);
        Assert.InRange(appended, 1, 128 * 1024); Assert.True(2 * appended <= estimate.TransferAndInstallBytes);
        prepared.Commit();
        foreach (var (path, bytes) in prefixes) Assert.Equal(bytes, File.ReadAllBytes(path)[..bytes.Length]);
        using var fresh = repo.OpenReader(); var version = fresh.GetCurrent(new(FoundationTests.Root, "one-file"))!;
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(repo.GetPayloadPath(version.Identity.VersionId)));
    }

    [Fact]
    public void TornUnpublishedTailIsQuarantinedWithoutAdoptionOrTruncation()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        using var captured = repo.OpenReader(); var path = SegmentReader.SegmentPath(repo.DirectoryPath, captured.Captured.Address.SegmentId);
        using (var tail = new FileStream(path, FileMode.Append)) tail.Write("FHV4BLK\0torn unpublished bytes"u8);
        var prefix = File.ReadAllBytes(path); using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer, "second");
        Assert.Equal(prefix, File.ReadAllBytes(path)[..prefix.Length]); Assert.Single(captured.EnumerateCurrent());
        using var fresh = repo.OpenReader(); Assert.Equal(2, fresh.EnumerateCurrent().Count());
        Assert.True(fresh.Captured.Address.SegmentOrdinal > captured.Captured.Address.SegmentOrdinal);
    }

    [Fact]
    public void DisposableDecodedCacheStillValidatesEveryRequestedFrame()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) { FoundationTests.Add(repo, writer); FoundationTests.Add(repo, writer, "second"); }
        FrameAddress address; using (var reader = repo.OpenReader())
            address = reader.CurrentIndex.Find(reader.Captured.Root.Current, PathKeys.Current(new(FoundationTests.Root, "foo.txt")))!;
        using var working = repo.OpenComparisonReader(temp.Child("work")); _ = working.Read(address);
        Assert.Empty(Directory.GetFiles(working.Workspace!.CreatedDirectory!, "*.decoded", SearchOption.AllDirectories));
        using (var source = File.OpenRead(SegmentReader.SegmentPath(repo.DirectoryPath, address.SegmentId)))
        {
            var bytes = new byte[160]; source.ReadExactly(bytes); var header = MetadataCodec.DecodeSegment(bytes);
            using var blocks = new MetadataBlockReader(source, header, new()); var block = blocks.Header(address.BlockOffset);
            Assert.True(working.Workspace.DecodedBlocks.TryGet(header, block, out var decoded));
            // Test-only RAM corruption preserves the old assertion: cache hits still validate each frame.
            // Process-memory protection is outside the disk-confidentiality claim.
            decoded[checked((int)(address.Offset - block.Logical + Format.FrameHeaderBytes))] ^= 1;
        }
        Assert.Throws<InvalidDataException>(() => working.Read(address));
    }

    [Fact]
    public void ReadingAnOlderFlushedFrameDoesNotFlushUnrelatedPendingBytes()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var prior = repo.ReadAuthority();
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, prior.Root.Epoch, prior);
        var body = MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(Guid.NewGuid(), "first")));
        var first = append.Append(FrameKind.Semantic, body); append.FlushAndValidate();
        var before = append.Statistics.BlocksWritten;
        append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(Guid.NewGuid(), "second"))));
        Assert.Equal(body, append.ReadNew(first)); Assert.Equal(before, append.Statistics.BlocksWritten);
        append.FlushAndValidate(); Assert.Equal(before + 1, append.Statistics.BlocksWritten);
    }

    private static SegmentHeader Header() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
}
