using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal static class Format
{
    internal const int SegmentHeaderBytes = 160;
    internal const int FrameHeaderBytes = 96;
    internal const int AddressBytes = 88;
    internal const int PageBytes = 16384;
    internal const int MaximumPageEntries = 128;
    internal const int MaximumDepth = 16;
    internal const int MaximumFrameBytes = 1048576;
    internal const long MaximumSegmentBytes = 67108864;
    internal const int SealBytes = FrameHeaderBytes + 16;
    internal const int MaximumKeyBytes = 16 + HistoryPath.MaximumPathLength * 2 + 2 + 12;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly UnicodeEncoding Utf16 = new(true, false, true);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static InvalidDataException Invalid(string message) => new("Invalid v4 metadata: " + message);
    internal static void Require(bool condition, string message) { if (!condition) throw Invalid(message); }
    internal static bool ValidHash(string hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal enum FrameKind { Semantic = 1, Page = 2, Root = 3, Seal = 4, Retired = 5 }
internal enum IndexKind : byte { Current = 1, Versions = 2, Sources = 3 }
internal enum SemanticKind : byte { VersionAdded = 1, CurrentUpsert = 2, CurrentRemoved = 3, SourceRoot = 4, VersionRemoved = 5 }

internal sealed record FrameAddress(Guid SegmentId, long SegmentOrdinal, long Offset, int Length, FrameKind Kind, long Serial, string Sha256,
    long BlockOffset = Format.SegmentHeaderBytes)
{
    internal long End => checked(Offset + Format.FrameHeaderBytes + Length);
    internal void Validate()
    {
        Format.Require(SegmentId != Guid.Empty && SegmentOrdinal > 0 && Offset >= Format.SegmentHeaderBytes &&
            Length >= 0 && Length <= Format.MaximumFrameBytes && Enum.IsDefined(Kind) && Serial > 0 &&
            Offset <= Format.MaximumSegmentBytes - Format.FrameHeaderBytes - Length && Format.ValidHash(Sha256) &&
            BlockOffset >= Format.SegmentHeaderBytes && BlockOffset <= MetadataBlock.MaximumPhysicalBytes - MetadataBlock.Overhead, "frame address");
    }
}

internal sealed record SegmentHeader(Guid RepositoryId, Guid SegmentId, Guid Epoch, long Ordinal, Guid PreviousId, long PreviousOrdinal);
internal sealed record MetadataRoot(long Generation, long Sequence, Guid TransactionId, Guid Epoch,
    long PreviousGeneration, Guid PreviousTransactionId, string PreviousRootHash,
    FrameAddress? Journal, FrameAddress? Current, FrameAddress? Versions, FrameAddress? Sources, FrameAddress? Retired);
internal sealed record Authority(RepositoryIdentity Repository, MetadataRoot Root, FrameAddress Address)
{
    internal AuthorityIdentity Identity => new(Repository.RepositoryId, Root.Generation, Root.Sequence, Root.TransactionId);
}
internal sealed record SemanticRecord(SemanticKind Kind, FrameAddress? Previous, StoredVersion? Version = null,
    HistoryPath? Path = null, FrameAddress? VersionAddress = null, SourceRootConfiguration? Source = null,
    VersionIdentity? RemovedVersion = null);
internal sealed record RetiredSegment(Guid Id, Guid Epoch, long Ordinal, FrameAddress? Previous);
internal sealed record PageEntry(byte[] Key, FrameAddress Value);
internal sealed record IndexPage(IndexKind Kind, byte Level, IReadOnlyList<PageEntry> Entries);

internal sealed class BufferWriter : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;
    internal BufferWriter() => _writer = new BinaryWriter(_stream, Format.Utf8, true);
    internal void Byte(byte value) => _writer.Write(value);
    internal void U16(ushort value) => _writer.Write(value);
    internal void I32(int value) => _writer.Write(value);
    internal void I64(long value) => _writer.Write(value);
    internal void Guid(Guid value) => _writer.Write(value.ToByteArray(bigEndian: true));
    internal void Bytes(ReadOnlySpan<byte> bytes) => _writer.Write(bytes);
    internal void Text(string value) { var bytes = Format.Utf8.GetBytes(value); I32(bytes.Length); Bytes(bytes); }
    internal void Address(FrameAddress? address)
    {
        Byte(address is null ? (byte)0 : (byte)1);
        if (address is not null) RequiredAddress(address);
    }
    internal void RequiredAddress(FrameAddress address)
    {
        address.Validate(); Guid(address.SegmentId); I64(address.SegmentOrdinal); I64(address.Offset);
        I32(address.Length); I32((int)address.Kind); I64(address.Serial); Bytes(Convert.FromHexString(address.Sha256)); I64(address.BlockOffset);
    }
    internal byte[] Finish() => _stream.ToArray();
    public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
}

internal ref struct BufferReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> _remaining = bytes;
    internal int Remaining => _remaining.Length;
    internal ReadOnlySpan<byte> Take(int length)
    {
        Format.Require(length >= 0 && length <= _remaining.Length, "truncated/oversized field");
        var value = _remaining[..length]; _remaining = _remaining[length..]; return value;
    }
    internal byte Byte() => Take(1)[0];
    internal ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    internal int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    internal long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    internal Guid Guid() => new(Take(16), bigEndian: true);
    internal string Text(int maximumBytes)
    {
        var length = I32(); Format.Require(length >= 0 && length <= maximumBytes, "string limit");
        return Format.Utf8.GetString(Take(length));
    }
    internal FrameAddress? Address() => Byte() switch { 0 => null, 1 => RequiredAddress(), _ => throw Format.Invalid("nullable pointer flag") };
    internal FrameAddress RequiredAddress()
    {
        var value = new FrameAddress(Guid(), I64(), I64(), I32(), (FrameKind)I32(), I64(), Convert.ToHexString(Take(32)), I64());
        value.Validate(); return value;
    }
    internal void End() => Format.Require(_remaining.IsEmpty, "trailing bytes");
}
