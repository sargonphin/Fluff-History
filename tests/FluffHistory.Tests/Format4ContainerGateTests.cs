using System.IO.Compression;
using System.Security.Cryptography;
using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests;

public sealed class Format4ContainerGateTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PartialAppendDoesNotRewriteCommittedBlock(bool random)
    {
        var segment = new SegmentHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
        using var file = new MemoryStream(); file.Write(MetadataCodec.EncodeSegment(segment));
        var firstBytes = random ? RandomNumberGenerator.GetBytes(12345) : new byte[12345];
        var first = MetadataBlock.Write(file, segment, 160, 0, firstBytes); var committed = file.ToArray();
        var second = MetadataBlock.Write(file, segment, first.LogicalEnd, first.Physical, new byte[512]);
        Assert.Equal(committed, file.ToArray()[..committed.Length]);
        byte[] Read(long offset, int count) => file.ToArray().AsSpan((int)offset, count).ToArray();
        Assert.Equal(firstBytes, MetadataBlock.Decode(Read, segment, MetadataBlock.ReadHeader(Read, segment, first.Physical, file.Length), file.Length));
        Assert.Equal(new byte[512], MetadataBlock.Decode(Read, segment, second, file.Length));
        Assert.Equal(random ? 0 : 1, first.Encoding);
    }

    [Theory]
    [InlineData("trailing")] [InlineData("truncated")]
    public void RehashedMalformedDeflateIsRejected(string problem)
    {
        var segment = new SegmentHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
        var decoded = new byte[5000]; using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, true)) deflate.Write(decoded);
        var encoded = problem == "trailing" ? compressed.ToArray().Concat(new byte[] { 0 }).ToArray() : compressed.ToArray()[..^1];
        var h = new BlockHeader(160, 160, decoded.Length, encoded.Length, 0, false, 1, SHA256.HashData(decoded));
        var bytes = MetadataBlock.Header(segment, h); using var data = new MemoryStream(); data.Write(new byte[160]); data.Write(bytes); data.Write(encoded);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(bytes); hash.AppendData(encoded);
        using var footer = new BufferWriter(); footer.Bytes("FHV4END\0"u8); footer.I64(160); footer.Bytes(hash.GetHashAndReset()); data.Write(footer.Finish());
        byte[] Read(long offset, int count) => data.ToArray().AsSpan((int)offset, count).ToArray();
        Assert.Throws<InvalidDataException>(() => MetadataBlock.Decode(Read, segment, h, data.Length));
    }
}
