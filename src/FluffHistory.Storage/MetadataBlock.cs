using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FluffHistory.Storage;

// Independent immutable physical blocks; frame addresses retain logical offsets.
internal sealed record BlockHeader(long Physical, long Logical, int DecodedLength, int EncodedLength,
    long Previous, bool Sealed, int Encoding, byte[] DecodedHash)
{
    internal long End => checked(Physical + MetadataBlock.Overhead + EncodedLength);
    internal long LogicalEnd => checked(Logical + DecodedLength);
}

internal static class MetadataBlock
{
    internal const int MaximumDecodedBytes = 256 * 1024;
    internal const int HeaderBytes = 160, TrailerBytes = 48, Overhead = HeaderBytes + TrailerBytes;
    internal const long MaximumPhysicalBytes = 4 * Format.MaximumSegmentBytes;

    internal static BlockHeader Write(Stream stream, SegmentHeader segment, long logical, long previous,
        ReadOnlySpan<byte> decoded, bool seal = false, long outputBudget = long.MaxValue, RepositoryCryptography? crypto = null)
    {
        Format.Require(decoded.Length is > 0 and <= MaximumDecodedBytes, "block decoded length");
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, true)) deflate.Write(decoded);
        var encoding = compressed.Length + 32 < decoded.Length ? 1 : 0;
        var encoded = encoding == 1 ? compressed.ToArray() : decoded.ToArray();
        var digest = SHA256.HashData(decoded);
        var h = new BlockHeader(stream.Position, logical, decoded.Length,
            encoded.Length + (crypto is null ? 0 : 32 + RepositoryCryptography.Overhead), previous, seal,
            encoding + (crypto is null ? 0 : 2), crypto is null ? digest : new byte[32]);
        Format.Require(h.End <= MaximumPhysicalBytes, "physical segment limit");
        if (h.End - h.Physical > outputBudget) throw new IOException("Maintenance physical-output budget exceeded.");
        var bytes = Header(segment, h);
        if (crypto is not null)
        {
            var plain = digest.Concat(encoded).ToArray();
            try { encoded = crypto.Protect(plain, "metadata-block", MetadataCodec.EncodeSegment(segment).Concat(bytes).ToArray()); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        stream.Write(bytes); stream.Write(encoded);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(bytes); hash.AppendData(encoded);
        using var footer = new BufferWriter(); footer.Bytes("FHV4END\0"u8); footer.I64(h.Physical); footer.Bytes(hash.GetHashAndReset());
        stream.Write(footer.Finish()); return h;
    }

    internal static byte[] Header(SegmentHeader segment, BlockHeader h)
    {
        using var w = new BufferWriter(); w.Bytes("FHV4BLK\0"u8); w.I32(4); w.I32(h.Encoding);
        w.Guid(segment.RepositoryId); w.Guid(segment.SegmentId); w.I64(h.Logical); w.I32(h.DecodedLength); w.I32(h.EncodedLength);
        w.I64(h.Previous); w.I64(h.Physical); w.I32(h.Sealed ? 1 : 0); w.Bytes(new byte[12]); w.Bytes(h.DecodedHash);
        var bytes = new byte[HeaderBytes]; w.Finish().CopyTo(bytes, 0); SHA256.HashData(bytes.AsSpan(0, 128)).CopyTo(bytes, 128); return bytes;
    }

    internal static BlockHeader ReadHeader(Func<long, int, byte[]> read, SegmentHeader segment, long physical, long length, RepositoryCryptography? crypto = null)
    {
        Format.Require(physical >= Format.SegmentHeaderBytes && physical <= length - Overhead && physical <= MaximumPhysicalBytes - Overhead, "block physical offset");
        var bytes = read(physical, HeaderBytes);
        Format.Require(bytes.AsSpan(0, 8).SequenceEqual("FHV4BLK\0"u8) && SHA256.HashData(bytes.AsSpan(0, 128)).AsSpan().SequenceEqual(bytes.AsSpan(128)), "block magic/header checksum");
        var r = new BufferReader(bytes.AsSpan(8, 120)); Format.Require(r.I32() == 4, "block schema"); var encoding = r.I32();
        Format.Require((crypto is null ? encoding is 0 or 1 : encoding is 2 or 3) && r.Guid() == segment.RepositoryId && r.Guid() == segment.SegmentId, "block encoding/mode/identity");
        var logical = r.I64(); var decoded = r.I32(); var encoded = r.I32(); var previous = r.I64();
        Format.Require(r.I64() == physical, "block self offset"); var flags = r.I32();
        Format.Require(flags is 0 or 1 && r.Take(12).IndexOfAnyExcept((byte)0) < 0, "block flags/reserved"); var digest = r.Take(32).ToArray(); r.End();
        var extra = crypto is null ? 0 : 32 + RepositoryCryptography.Overhead;
        Format.Require((crypto is null || digest.AsSpan().IndexOfAnyExcept((byte)0) < 0) &&
            decoded is > 0 and <= MaximumDecodedBytes && encoded > extra && encoded <= decoded + extra &&
            (encoding % 2 != 0 || encoded == decoded + extra) && logical >= Format.SegmentHeaderBytes && logical <= Format.MaximumSegmentBytes - decoded &&
            (previous == 0 || previous >= Format.SegmentHeaderBytes && previous < physical) && (flags == 0 || decoded == Format.SealBytes), "block bounds");
        var h = new BlockHeader(physical, logical, decoded, encoded, previous, flags == 1, encoding, digest);
        Format.Require(h.End <= length && h.End <= MaximumPhysicalBytes, "truncated block extent"); return h;
    }

    internal static byte[] Decode(Func<long, int, byte[]> read, SegmentHeader segment, BlockHeader h, long physicalLength, RepositoryCryptography? crypto = null)
    {
        if (h.Previous == 0)
            Format.Require(h.Physical == Format.SegmentHeaderBytes && h.Logical == Format.SegmentHeaderBytes, "first block extent");
        else
        {
            var prior = ReadHeader(read, segment, h.Previous, physicalLength, crypto);
            Format.Require(!prior.Sealed && prior.LogicalEnd == h.Logical && (h.Sealed ? prior.End <= h.Physical : prior.End == h.Physical), "block gap/overlap/order");
        }
        var payload = read(h.Physical + HeaderBytes, h.EncodedLength + TrailerBytes);
        var footer = payload.AsSpan(h.EncodedLength);
        Format.Require(footer[..8].SequenceEqual("FHV4END\0"u8) && BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) == h.Physical, "block trailer");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(Header(segment, h)); hash.AppendData(payload.AsSpan(0, h.EncodedLength));
        Format.Require(hash.GetHashAndReset().AsSpan().SequenceEqual(footer[16..]), "encoded block checksum");
        var encodedBytes = payload[..h.EncodedLength]; var digest = h.DecodedHash;
        if (crypto is not null)
        {
            var plain = crypto.Unprotect(encodedBytes, "metadata-block", MetadataCodec.EncodeSegment(segment).Concat(Header(segment, h)).ToArray());
            try { digest = plain[..32]; encodedBytes = plain[32..]; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        byte[] decoded;
        if (h.Encoding % 2 == 0) decoded = encodedBytes;
        else
        {
            decoded = new byte[h.DecodedLength];
            using var input = new ExactDeflateInput(encodedBytes, encodedBytes.Length);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            try { deflate.ReadExactly(decoded); }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated metadata deflate output.", error); }
            Format.Require(deflate.ReadByte() == -1 && input.Position == input.Length, "deflate decoded length/trailing bytes");
        }
        Format.Require(decoded.Length == h.DecodedLength && SHA256.HashData(decoded).AsSpan().SequenceEqual(digest), "decoded block checksum"); return decoded;
    }

    // Withhold the final input byte until the inflater actually asks for it. This
    // exposes trailing input despite DeflateStream read-ahead. A request past EOF
    // throws, so an unfinished deflate stream cannot masquerade as clean EOF.
    private sealed class ExactDeflateInput(byte[] bytes, int count) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => count;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            var remaining = Length - Position; Format.Require(remaining > 0, "truncated deflate input");
            var take = (int)Math.Min(buffer.Length, Math.Max(1, remaining - 1));
            bytes.AsSpan(_position, take).CopyTo(buffer); _position += take; return take;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
