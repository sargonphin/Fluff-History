using System.Buffers.Binary;
using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal static class MetadataCodec
{
    internal static byte[] EncodeSegment(SegmentHeader h)
    {
        using var w = new BufferWriter(); w.Bytes("FBV2SEG\0"u8); w.I32(RepositoryIdentity.FormatVersion); w.I32(Format.SegmentHeaderBytes);
        w.Guid(h.RepositoryId); w.Guid(h.SegmentId); w.Guid(h.Epoch); w.I64(h.Ordinal); w.Guid(h.PreviousId); w.I64(h.PreviousOrdinal);
        var bytes = new byte[Format.SegmentHeaderBytes]; w.Finish().CopyTo(bytes, 0);
        SHA256.HashData(bytes.AsSpan(0, 128)).CopyTo(bytes, 128); return bytes;
    }
    internal static SegmentHeader DecodeSegment(byte[] bytes) => Parse(() =>
    {
        Format.Require(bytes.Length == Format.SegmentHeaderBytes && bytes.AsSpan(0, 8).SequenceEqual("FBV2SEG\0"u8), "segment magic/length");
        Format.Require(SHA256.HashData(bytes.AsSpan(0, 128)).AsSpan().SequenceEqual(bytes.AsSpan(128)), "segment checksum");
        var r = new BufferReader(bytes.AsSpan(8, 120));
        if (r.I32() != RepositoryIdentity.FormatVersion) throw new NotSupportedException("Unsupported repository segment schema; expected 4.");
        Format.Require(r.I32() == Format.SegmentHeaderBytes, "segment header size");
        var h = new SegmentHeader(r.Guid(), r.Guid(), r.Guid(), r.I64(), r.Guid(), r.I64());
        Format.Require(h.RepositoryId != Guid.Empty && h.SegmentId != Guid.Empty && h.Epoch != Guid.Empty && h.Ordinal > 0 &&
            ((h.PreviousId == Guid.Empty && h.PreviousOrdinal == 0 && h.Ordinal == 1) || (h.PreviousId != Guid.Empty && h.PreviousOrdinal == h.Ordinal - 1)), "segment identities/order");
        Format.Require(r.Take(r.Remaining).IndexOfAnyExcept((byte)0) < 0, "segment reserved bytes"); return h;
    });

    internal static (byte[] Bytes, FrameAddress Address) EncodeFrame(SegmentHeader segment, long offset, long serial, FrameKind kind, byte[] body,
        long blockOffset = Format.SegmentHeaderBytes)
    {
        Format.Require(body.Length <= Format.MaximumFrameBytes && Enum.IsDefined(kind) && serial > 0, "frame fields");
        using var w = new BufferWriter(); w.Bytes("FBV2FRM\0"u8); w.I32((int)kind); w.I32(body.Length); w.I64(serial);
        w.Guid(segment.RepositoryId); w.Guid(segment.SegmentId); w.I64(offset);
        var bytes = new byte[Format.FrameHeaderBytes + body.Length]; w.Finish().CopyTo(bytes, 0); body.CopyTo(bytes, Format.FrameHeaderBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(bytes, 0, 64); hash.AppendData(body);
        var digest = hash.GetHashAndReset(); digest.CopyTo(bytes, 64);
        var address = new FrameAddress(segment.SegmentId, segment.Ordinal, offset, body.Length, kind, serial, Convert.ToHexString(digest), blockOffset);
        address.Validate(); return (bytes, address);
    }
    internal static FrameAddress ReadFrameHeader(byte[] header, SegmentHeader segment, long offset)
    {
        Format.Require(header.Length == Format.FrameHeaderBytes && header.AsSpan(0, 8).SequenceEqual("FBV2FRM\0"u8), "frame magic/header");
        var r = new BufferReader(header.AsSpan(8)); var kind = (FrameKind)r.I32(); var length = r.I32(); var serial = r.I64();
        Format.Require(r.Guid() == segment.RepositoryId && r.Guid() == segment.SegmentId && r.I64() == offset, "frame identity/offset");
        var address = new FrameAddress(segment.SegmentId, segment.Ordinal, offset, length, kind, serial, Convert.ToHexString(r.Take(32)));
        address.Validate(); r.End(); return address;
    }
    internal static byte[] ValidateFrame(byte[] bytes, SegmentHeader segment, FrameAddress expected)
    {
        Format.Require(bytes.Length == Format.FrameHeaderBytes + expected.Length, "frame truncation");
        var found = ReadFrameHeader(bytes[..Format.FrameHeaderBytes], segment, expected.Offset) with { BlockOffset = expected.BlockOffset };
        Format.Require(found == expected, "frame pointer binding");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(bytes, 0, 64); hash.AppendData(bytes, Format.FrameHeaderBytes, expected.Length);
        Format.Require(Convert.ToHexString(hash.GetHashAndReset()) == expected.Sha256, "frame checksum");
        return bytes[Format.FrameHeaderBytes..];
    }

    internal static byte[] EncodeRoot(MetadataRoot root)
    {
        using var w = new BufferWriter(); w.I64(root.Generation); w.I64(root.Sequence); w.Guid(root.TransactionId); w.Guid(root.Epoch);
        w.I64(root.PreviousGeneration); w.Guid(root.PreviousTransactionId); w.Bytes(Convert.FromHexString(root.PreviousRootHash));
        w.Address(root.Journal); w.Address(root.Current); w.Address(root.Versions); w.Address(root.Sources); w.Address(root.Retired); return w.Finish();
    }
    internal static MetadataRoot DecodeRoot(byte[] bytes) => Parse(() =>
    {
        var r = new BufferReader(bytes); var root = new MetadataRoot(r.I64(), r.I64(), r.Guid(), r.Guid(), r.I64(), r.Guid(), Convert.ToHexString(r.Take(32)),
            r.Address(), r.Address(), r.Address(), r.Address(), r.Address()); r.End();
        Format.Require(root.Generation >= 0 && root.Sequence >= 0 && root.Sequence <= root.Generation && root.TransactionId != Guid.Empty && root.Epoch != Guid.Empty &&
            (root.Generation == 0 ? root.Sequence == 0 && root.PreviousGeneration == -1 && root.PreviousTransactionId == Guid.Empty && root.PreviousRootHash == new string('0', 64) &&
                root.Journal is null && root.Current is null && root.Versions is null && root.Sources is null && root.Retired is null :
                root.PreviousGeneration == root.Generation - 1 && root.PreviousTransactionId != Guid.Empty && root.PreviousTransactionId != root.TransactionId), "root generation/predecessor");
        RequireKind(root.Journal, FrameKind.Semantic); RequireKind(root.Current, FrameKind.Page); RequireKind(root.Versions, FrameKind.Page);
        RequireKind(root.Sources, FrameKind.Page); RequireKind(root.Retired, FrameKind.Retired); return root;
    });
    internal static void RequireKind(FrameAddress? address, FrameKind kind) => Format.Require(address is null || address.Kind == kind, "pointer object type");

    internal static byte[] EncodeSemantic(SemanticRecord record)
    {
        using var w = new BufferWriter(); w.Byte((byte)record.Kind); w.Address(record.Previous);
        switch (record.Kind)
        {
            case SemanticKind.VersionAdded:
                var v = record.Version!; w.Guid(v.Identity.VersionId); w.I64(v.Identity.Sequence); w.I32(v.Identity.Ordinal); w.I64(v.StoredUtc.UtcTicks);
                var m = v.Metadata; w.Guid(m.SourceRootId); w.Text(m.RelativePath); w.I64(m.Length); w.I64(m.CreationTimeUtc.UtcTicks); w.I64(m.LastWriteTimeUtc.UtcTicks); w.I32((int)m.Attributes);
                w.Bytes(Convert.FromHexString(v.Checksum.Value)); break;
            case SemanticKind.CurrentUpsert: WritePath(w, record.Path!); w.RequiredAddress(record.VersionAddress!); break;
            case SemanticKind.CurrentRemoved: WritePath(w, record.Path!); break;
            case SemanticKind.VersionRemoved:
                var removed = record.RemovedVersion!; var key = PathKeys.Version(removed);
                _ = PathKeys.DecodeVersion(key, removed.VersionId);
                w.U16((ushort)key.Length); w.Bytes(key); w.Guid(removed.VersionId); break;
            case SemanticKind.SourceRoot:
                var s = record.Source!; w.Guid(s.SourceRootId); w.Text(s.OriginalPath); w.Byte(s.IsProtected ? (byte)1 : (byte)0); w.I64(s.MaximumFileSize);
                w.U16((ushort)s.ExcludedDirectories.Count); foreach (var e in s.ExcludedDirectories) w.Text(e); break;
            default: throw Format.Invalid("semantic type");
        }
        return w.Finish();
    }
    internal static SemanticRecord DecodeSemantic(byte[] bytes) => Parse(() =>
    {
        var r = new BufferReader(bytes); var kind = (SemanticKind)r.Byte(); var previous = r.Address(); RequireKind(previous, FrameKind.Semantic);
        SemanticRecord result;
        switch (kind)
        {
            case SemanticKind.VersionAdded:
                var id = r.Guid(); var seq = r.I64(); var ordinal = r.I32(); var stored = Utc(r.I64());
                var m = new LogicalFileMetadata(r.Guid(), r.Text(8192), r.I64(), Utc(r.I64()), Utc(r.I64()), (FileAttributes)r.I32());
                var v = new StoredVersion(new VersionIdentity(new HistoryPath(m.SourceRootId, m.RelativePath), seq, ordinal, id), m, stored, new XxHash64Checksum(Convert.ToHexString(r.Take(8))));
                result = new(kind, previous, Version: v); break;
            case SemanticKind.CurrentUpsert:
                var path = ReadPath(ref r); var address = r.RequiredAddress(); RequireKind(address, FrameKind.Semantic);
                result = new(kind, previous, Path: path, VersionAddress: address); break;
            case SemanticKind.CurrentRemoved: result = new(kind, previous, Path: ReadPath(ref r)); break;
            case SemanticKind.VersionRemoved:
                var keyLength = r.U16();
                Format.Require(keyLength is >= 32 and <= Format.MaximumKeyBytes && (keyLength & 1) == 0, "removed version key length");
                var removedKey = r.Take(keyLength).ToArray();
                result = new(kind, previous, RemovedVersion: PathKeys.DecodeVersion(removedKey, r.Guid())); break;
            case SemanticKind.SourceRoot:
                var rootId = r.Guid(); var original = r.Text(131040); var active = r.Byte(); Format.Require(active <= 1, "source protection flag"); var maximum = r.I64();
                var count = r.U16(); Format.Require(count <= 256, "exclusion count"); var exclusions = new string[count];
                for (var i = 0; i < count; i++) exclusions[i] = r.Text(8192);
                var source = new SourceRootConfiguration(rootId, original, active == 1, maximum, exclusions);
                Format.Require(source.ExcludedDirectories.SequenceEqual(exclusions), "canonical exclusion order/casing");
                result = new(kind, previous, Source: source); break;
            default: throw Format.Invalid("semantic type");
        }
        r.End(); return result;
    });
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    private static void WritePath(BufferWriter w, HistoryPath path) { var key = PathKeys.Current(path); w.U16((ushort)key.Length); w.Bytes(key); }
    private static HistoryPath ReadPath(ref BufferReader r) { var n = r.U16(); return PathKeys.DecodePath(r.Take(n)); }

    internal static byte[] EncodePage(IndexPage page)
    {
        using var w = new BufferWriter(); w.Byte((byte)page.Kind); w.Byte(page.Level); w.U16((ushort)page.Entries.Count);
        foreach (var e in page.Entries) { w.U16((ushort)e.Key.Length); w.Bytes(e.Key); w.RequiredAddress(e.Value); }
        var content = w.Finish(); Format.Require(content.Length <= Format.PageBytes, "page capacity");
        var bytes = new byte[Format.PageBytes]; content.CopyTo(bytes, 0); return bytes;
    }
    internal static IndexPage DecodePage(byte[] bytes, long pageSerial) => Parse<IndexPage>(() =>
    {
        Format.Require(bytes.Length == Format.PageBytes, "page size"); var r = new BufferReader(bytes);
        var kind = (IndexKind)r.Byte(); var level = r.Byte(); var count = r.U16();
        Format.Require(Enum.IsDefined(kind) && level < Format.MaximumDepth && count is > 0 and <= Format.MaximumPageEntries, "page shape");
        var entries = new PageEntry[count]; byte[]? previous = null;
        for (var i = 0; i < count; i++)
        {
            var n = r.U16(); Format.Require(n <= Format.MaximumKeyBytes, "key limit"); var key = r.Take(n).ToArray(); PathKeys.Validate(key, kind);
            var pointer = r.RequiredAddress(); Format.Require(pointer.Serial < pageSerial, "page forward/cyclic pointer");
            RequireKind(pointer, level == 0 ? FrameKind.Semantic : FrameKind.Page);
            Format.Require(previous is null || previous.AsSpan().SequenceCompareTo(key) < 0, "unsorted/duplicate page keys");
            entries[i] = new(key, pointer); previous = key;
        }
        Format.Require(r.Take(r.Remaining).IndexOfAnyExcept((byte)0) < 0, "page padding"); return new(kind, level, entries);
    });
    internal static byte[] EncodeRetired(RetiredSegment segment)
    {
        using var w = new BufferWriter(); w.Guid(segment.Id); w.Guid(segment.Epoch); w.I64(segment.Ordinal); w.Address(segment.Previous); return w.Finish();
    }
    internal static RetiredSegment DecodeRetired(byte[] bytes) => Parse(() =>
    {
        var r = new BufferReader(bytes); var result = new RetiredSegment(r.Guid(), r.Guid(), r.I64(), r.Address()); r.End();
        Format.Require(result.Id != Guid.Empty && result.Epoch != Guid.Empty && result.Ordinal > 0, "retired segment identity"); RequireKind(result.Previous, FrameKind.Retired); return result;
    });
    internal static T Parse<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (Exception e) when (e is ArgumentException or OverflowException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Malformed v4 metadata value.", e); }
    }
}
