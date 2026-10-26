using System.Buffers.Binary;

namespace FluffHistory.Storage;

/// <summary>Bounded physical prefetch and on-demand block decode. Never interprets authority.</summary>
internal sealed class MetadataBlockReader(Stream stream, SegmentHeader segment, IoStatistics statistics,
    Action<int>? requested = null, int logicalReadAhead = 0, MetadataWorkspace? workspace = null, RepositoryCryptography? crypto = null) : IDisposable
{
    private readonly long _length = stream.Length;
    private readonly DecodedBlockCache _decoded = workspace?.DecodedBlocks ?? new(MetadataBlock.MaximumDecodedBytes * (logicalReadAhead == 0 ? 1L : 4L));
    private bool _disposed;
    private readonly Dictionary<long, BlockHeader> _headers = [];
    private readonly Queue<long> _headerOrder = [];
    private byte[]? _window;
    private long _windowOffset;
    private int _windowLength;
    private int _prefetch;

    internal long PhysicalLength => _length;

    private byte[] Direct(long offset, int count)
    {
        Format.Require(offset >= 0 && count >= 0 && offset <= _length - count, "physical read extent");
        var bytes = new byte[count]; stream.Position = offset;
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated metadata block.", error); }
        requested?.Invoke(count); statistics.PhysicalBytesRead += count; statistics.PhysicalReads++; return bytes;
    }
    private byte[] Physical(long offset, int count, bool refill)
    {
        if (_windowLength != 0 && offset >= _windowOffset && offset + count <= _windowOffset + _windowLength)
            return _window!.AsSpan(checked((int)(offset - _windowOffset)), count).ToArray();
        if (!refill || _prefetch == 0) return Direct(offset, count);
        var length = checked((int)Math.Min(Math.Max(count, _prefetch), _length - offset));
        _windowLength = 0; // Failed reads never make a cache image usable.
        var bytes = Direct(offset, length); _window = bytes; _windowOffset = offset; _windowLength = length;
        return bytes.AsSpan(0, count).ToArray();
    }
    internal BlockHeader Header(long offset)
    {
        if (_headers.TryGetValue(offset, out var known)) return known;
        var header = MetadataBlock.ReadHeader((o, n) => Physical(o, n, false), segment, offset, _length, crypto);
        if (_headers.Count == 64) _headers.Remove(_headerOrder.Dequeue());
        _headers.Add(offset, header); _headerOrder.Enqueue(offset); return header;
    }
    private (BlockHeader Header, byte[] Bytes) Decode(long offset, bool cacheChecked = false)
    {
        var h = Header(offset);
        if (!cacheChecked && _decoded.TryGet(segment, h, out var known)) return (h, known);
        if (logicalReadAhead != 0)
            _prefetch = checked((int)Math.Clamp((long)logicalReadAhead * (h.EncodedLength + MetadataBlock.Overhead) / h.DecodedLength,
                h.EncodedLength + MetadataBlock.TrailerBytes, logicalReadAhead + MetadataBlock.Overhead));
        var bytes = MetadataBlock.Decode((o, n) => o == h.Previous ? MetadataBlock.Header(segment, Header(o)) : Physical(o, n, true), segment, h, _length, crypto);
        _decoded.Store(segment, h, bytes); workspace?.DecodedObserved?.Invoke(segment, h);
        statistics.BlocksDecoded++; statistics.DecodedBytes += bytes.Length;
        statistics.PeakDecodedBytes = Math.Max(statistics.PeakDecodedBytes, _decoded.CurrentBytes);
        return (h, bytes);
    }
    internal BlockHeader Read(FrameAddress address, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); workspace?.CancellationToken.ThrowIfCancellationRequested(); var logical = address.Offset; var physical = address.BlockOffset; var remaining = destination; BlockHeader? prior = null;
        var maximum = 2 + Format.MaximumFrameBytes / MetadataBlock.MaximumDecodedBytes;
        while (!remaining.IsEmpty)
        {
            Format.Require(maximum-- > 0, "frame decode block bound");
            var header = Header(physical);
            Format.Require(!header.Sealed && logical >= header.Logical && logical < header.LogicalEnd &&
                (prior is null || header.Logical == prior.LogicalEnd && header.Previous == prior.Physical), "frame/block address/order");
            var count = (int)Math.Min(remaining.Length, header.LogicalEnd - logical);
            if (_decoded.TryGet(segment, header, out var buffered))
            {
                buffered.AsSpan(checked((int)(logical - header.Logical)), count).CopyTo(remaining);
                statistics.DecodedCacheReadBytes += count;
            }
            else
            {
                var (_, decoded) = Decode(physical, true);
                decoded.AsSpan(checked((int)(logical - header.Logical)), count).CopyTo(remaining);
            }
            remaining = remaining[count..]; logical += count; physical = header.End; prior = header;
        }
        return prior ?? throw Format.Invalid("empty logical read");
    }
    internal BlockHeader Last(FrameAddress address)
    {
        var h = Header(address.BlockOffset); var budget = 2 + Format.MaximumFrameBytes / MetadataBlock.MaximumDecodedBytes;
        Format.Require(address.Offset >= h.Logical && address.Offset < h.LogicalEnd && !h.Sealed, "locator logical binding");
        while (h.LogicalEnd < address.End)
        {
            Format.Require(budget-- > 0, "frame block traversal bound"); var next = Header(h.End);
            Format.Require(!next.Sealed && next.Previous == h.Physical && next.Logical == h.LogicalEnd, "block traversal gap/order"); h = next;
        }
        return h;
    }
    internal (long LogicalLength, long Serial) ValidateSeal()
    {
        Format.Require(_length >= Format.SegmentHeaderBytes + MetadataBlock.Overhead + Format.SealBytes, "missing block seal");
        var footer = Direct(_length - MetadataBlock.TrailerBytes, MetadataBlock.TrailerBytes);
        Format.Require(footer.AsSpan(0, 8).SequenceEqual("FHV4END\0"u8), "seal trailer magic");
        var h = Header(BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(8)));
        Format.Require(h.Sealed && h.End == _length, "unaccounted sealed tail"); var (_, bytes) = Decode(h.Physical);
        var address = MetadataCodec.ReadFrameHeader(bytes[..Format.FrameHeaderBytes], segment, h.Logical) with { BlockOffset = h.Physical };
        Format.Require(address.Kind == FrameKind.Seal && address.Length == 16, "segment seal type");
        var r = new BufferReader(MetadataCodec.ValidateFrame(bytes, segment, address));
        Format.Require(r.I64() == h.Logical && r.I64() == address.Serial - 1, "seal extent/serial"); r.End();
        return (h.Logical, address.Serial);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; if (workspace is null) _decoded.Dispose(); _headers.Clear(); _headerOrder.Clear(); _window = null; }
}
