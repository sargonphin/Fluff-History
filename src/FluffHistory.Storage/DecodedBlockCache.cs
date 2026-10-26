using System.Security.Cryptography;

namespace FluffHistory.Storage;

/// <summary>Owner-local plaintext RAM only. A hit reuses validated data, never repository authority.
/// Returned arrays are borrowed until the next cache operation; callers must not retain them.</summary>
internal sealed class DecodedBlockCache(long maximumBytes) : IDisposable
{
    private sealed record Entry(SegmentHeader Segment, BlockHeader Header, byte[] Bytes);
    private readonly Dictionary<(SegmentHeader Segment, long Physical), LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = [];
    private bool _disposed;
    internal long CurrentBytes { get; private set; }
    internal long PeakBytes { get; private set; }
    internal long Hits { get; private set; }
    internal long Misses { get; private set; }
    internal long Evictions { get; private set; }
    internal long MaximumBytes { get; } = maximumBytes >= MetadataBlock.MaximumDecodedBytes
        ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    internal bool TryGet(SegmentHeader segment, BlockHeader header, out byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue((segment, header.Physical), out var node)) { Misses++; bytes = []; return false; }
        var prior = node.Value.Header;
        Format.Require(prior.Logical == header.Logical && prior.DecodedLength == header.DecodedLength && prior.EncodedLength == header.EncodedLength &&
            prior.Previous == header.Previous && prior.Sealed == header.Sealed && prior.Encoding == header.Encoding &&
            prior.DecodedHash.AsSpan().SequenceEqual(header.DecodedHash), "decoded RAM cache binding");
        _recency.Remove(node); _recency.AddLast(node); Hits++; bytes = node.Value.Bytes; return true;
    }
    // Takes ownership of the decoded array; eviction/disposal zero it. No reader keeps a second copy.
    internal void Store(SegmentHeader segment, BlockHeader header, byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Format.Require(bytes.Length == header.DecodedLength && bytes.Length is > 0 and <= MetadataBlock.MaximumDecodedBytes, "decoded RAM cache extent");
        var key = (segment, header.Physical);
        if (_entries.ContainsKey(key)) throw new InvalidOperationException("Decoded cache entry already exists.");
        while (bytes.Length > MaximumBytes - CurrentBytes)
        {
            var oldest = _recency.First!; _recency.RemoveFirst(); _entries.Remove((oldest.Value.Segment, oldest.Value.Header.Physical));
            CurrentBytes -= oldest.Value.Bytes.Length; CryptographicOperations.ZeroMemory(oldest.Value.Bytes); Evictions++;
        }
        var node = _recency.AddLast(new Entry(segment, header with { DecodedHash = header.DecodedHash.ToArray() }, bytes));
        _entries.Add(key, node); CurrentBytes += bytes.Length; PeakBytes = Math.Max(PeakBytes, CurrentBytes);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var entry in _recency) CryptographicOperations.ZeroMemory(entry.Bytes);
        _entries.Clear(); _recency.Clear(); CurrentBytes = 0;
    }
}
