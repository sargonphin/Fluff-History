using System.Buffers.Binary;
using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

// Independent from the full-metadata workspace and its writer/maintenance segment images.
internal sealed record ComparisonReadBudget(int MaximumEntries = 16_384, long MaximumKeyBytes = 4 * 1024 * 1024)
{
    internal void Validate()
    {
        if (MaximumEntries is < 1 or > 65_536 || MaximumKeyBytes is < Format.MaximumKeyBytes or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(ComparisonReadBudget));
    }
}

internal sealed class ComparisonReadStatistics
{
    internal IoStatistics Io { get; } = new();
    internal long Windows, MaximumBufferedEntries, MaximumBufferedKeyBytes, LogicalFrameBytes;
    // Exact ReadExactly ranges with FileStream buffering disabled; not device I/O counts.
    // Excludes bounded authority/control reads and OS/device read-ahead.
    internal long DestinationReadRequests, DestinationBytesRequested;
    internal long IndexAllocatedBytes, ResolutionAllocatedBytes;
}

public sealed partial class RepositoryReader
{
    private readonly HashSet<SegmentReader> _comparisonSessions = [];
    internal ComparisonReadStatistics? LastComparisonStatistics { get; private set; }
    /// <summary>Requested bytes for the most recent projected enumeration; excludes authority/control and OS read-ahead.</summary>
    public long CurrentComparisonBytesRequested => LastComparisonStatistics?.DestinationBytesRequested ?? 0;

    /// <summary>Enumerates successful current size/mtime baselines in v4 logical key order.
    /// Resolves bounded windows by segment/offset, without full restore objects or segment copies.
    /// The captured generation never refreshes; expiry requires restarting with a fresh reader.
    /// Cancellation or corruption may terminate an incomplete enumeration. Not thread safe.</summary>
    public IEnumerable<CurrentFileComparison> EnumerateCurrentComparison(Guid? sourceRootId = null, string folder = "",
        CancellationToken cancellationToken = default) => EnumerateCurrentComparison(new(), sourceRootId, folder, cancellationToken);

    internal IEnumerable<CurrentFileComparison> EnumerateCurrentComparison(ComparisonReadBudget budget,
        Guid? sourceRootId = null, string folder = "", CancellationToken cancellationToken = default)
    {
        budget.Validate(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceRootId is null && folder.Length != 0) throw new ArgumentException("A folder requires a source root.", nameof(folder));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Workspace?.CancellationToken ?? default);
        var token = linked.Token; token.ThrowIfCancellationRequested();
        var lower = sourceRootId is null ? null : PathKeys.Scope(sourceRootId.Value, folder);
        var stats = new ComparisonReadStatistics(); LastComparisonStatistics = stats;
        using var segments = new SegmentReader(_repository.DirectoryPath, _repository.Identity, Captured.Root.Epoch,
            Captured.Address, statistics: stats.Io, comparison: stats, crypto: _repository.Crypto);
        _comparisonSessions.Add(segments);
        try
        {
            byte[] ReadPage(FrameAddress address)
            {
                ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); return segments.Read(address);
            }
            var index = new OrderedIndex(IndexKind.Current, ReadPage);
            using var entries = index.Range(Captured.Root.Current, lower, lower is null ? null : PathKeys.ScopeUpperBound(lower)).GetEnumerator();
            var requests = new ComparisonRequest[budget.MaximumEntries];
            var results = new CurrentFileComparison?[budget.MaximumEntries];
            var frame = new byte[Format.FrameHeaderBytes + Format.MaximumFrameBytes];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            PageEntry? pending = null;
            while (true)
            {
                token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
                var allocation = GC.GetAllocatedBytesForCurrentThread();
                var count = 0; long keyBytes = 0;
                while (count < requests.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (pending is null && !entries.MoveNext()) break;
                    var entry = pending ?? entries.Current; pending = null;
                    if (entry.Key.Length > budget.MaximumKeyBytes - keyBytes) { pending = entry; break; }
                    requests[count] = new(entry, count); keyBytes += entry.Key.Length; count++;
                }
                stats.IndexAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocation;
                if (count == 0) yield break;
                stats.Windows++; stats.MaximumBufferedEntries = Math.Max(stats.MaximumBufferedEntries, count);
                stats.MaximumBufferedKeyBytes = Math.Max(stats.MaximumBufferedKeyBytes, keyBytes);
                allocation = GC.GetAllocatedBytesForCurrentThread();
                Array.Sort(requests, 0, count, ComparisonRequest.PhysicalOrder);
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
                    var request = requests[i]; var address = request.Entry.Value;
                    MetadataCodec.RequireKind(address, FrameKind.Semantic);
                    var body = segments.ReadComparison(address, frame, hash);
                    results[request.Position] = ComparisonVersionDecoder.Decode(body, request.Entry.Key, address, segments, Captured.Root.Sequence);
                }
                stats.ResolutionAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocation;
                // Drop address/key references before yielding; only bounded projected results survive.
                Array.Clear(requests, 0, count);
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
                    var result = results[i]!; results[i] = null; yield return result;
                }
            }
        }
        finally { _comparisonSessions.Remove(segments); }
    }

    private readonly record struct ComparisonRequest(PageEntry Entry, int Position)
    {
        internal static readonly IComparer<ComparisonRequest> PhysicalOrder = Comparer<ComparisonRequest>.Create((a, b) =>
        {
            var segment = a.Entry.Value.SegmentId.CompareTo(b.Entry.Value.SegmentId);
            return segment != 0 ? segment : a.Entry.Value.Offset.CompareTo(b.Entry.Value.Offset);
        });
    }
}

// Reads the unchanged v4 VersionAdded encoding. Unneeded restore fields are still validated.
internal static class ComparisonVersionDecoder
{
    internal static CurrentFileComparison Decode(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key,
        FrameAddress address, SegmentReader segments, long sequence)
    {
        try
        {
            var r = new BufferReader(body);
            Format.Require(r.Byte() == (byte)SemanticKind.VersionAdded, "version reference type");
            var previous = r.Address(); MetadataCodec.RequireKind(previous, FrameKind.Semantic);
            if (previous is not null)
            { segments.CheckAddress(previous); Format.Require(previous.Serial < address.Serial, "semantic forward/cycle"); }
            var id = r.Guid(); var seq = r.I64(); var ordinal = r.I32(); ValidateUtc(r.I64());
            var root = r.Guid(); var name = r.Text(8192); var path = new HistoryPath(root, name);
            var length = r.I64(); ValidateUtc(r.I64()); var lastWrite = ValidateUtc(r.I64()); var attributes = r.I32();
            Format.Require(id != Guid.Empty && seq > 0 && seq <= sequence && ordinal is >= 0 and < MutationBatch.MaximumChanges &&
                length >= 0 && (attributes & ~(int)LogicalFileMetadata.SupportedAttributes) == 0, "version fields");
            _ = r.Take(8); r.End(); // Every 8-byte checksum value is valid; no checksum object needed.
            Format.Require(MatchesKey(path, key), "version path binding");
            return new(new(path, seq, ordinal, id), length, lastWrite);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        { throw new InvalidDataException("Malformed v4 comparison metadata value.", e); }
    }

    private static DateTimeOffset ValidateUtc(long ticks)
    {
        Format.Require(ticks > 0 && ticks <= DateTimeOffset.MaxValue.Ticks, "UTC ticks");
        return new(ticks, TimeSpan.Zero);
    }
    private static bool MatchesKey(HistoryPath path, ReadOnlySpan<byte> key)
    {
        if (key.Length != 18 + 2 * path.CanonicalPath.Length || key[^1] != 0 || key[^2] != 0) return false;
        if (new Guid(key[..16], bigEndian: true) != path.SourceRootId) return false;
        for (var i = 0; i < path.CanonicalPath.Length; i++)
            if (BinaryPrimitives.ReadUInt16BigEndian(key.Slice(16 + i * 2, 2)) != path.CanonicalPath[i]) return false;
        return true;
    }
}

internal static class ComparisonFrameValidator
{
    internal static ReadOnlySpan<byte> Validate(ReadOnlySpan<byte> bytes, SegmentHeader segment, FrameAddress expected, IncrementalHash hash)
    {
        Format.Require(bytes.Length == Format.FrameHeaderBytes + expected.Length && bytes[..8].SequenceEqual("FBV2FRM\0"u8), "frame magic/length");
        var r = new BufferReader(bytes.Slice(8, Format.FrameHeaderBytes - 8));
        Format.Require(r.I32() == (int)expected.Kind && r.I32() == expected.Length && r.I64() == expected.Serial &&
            r.Guid() == segment.RepositoryId && r.Guid() == segment.SegmentId && r.I64() == expected.Offset &&
            Convert.ToHexString(r.Take(32)) == expected.Sha256, "frame pointer binding");
        r.End();
        hash.AppendData(bytes[..64]); hash.AppendData(bytes[Format.FrameHeaderBytes..]);
        Span<byte> digest = stackalloc byte[32]; hash.GetHashAndReset(digest);
        Format.Require(digest.SequenceEqual(bytes.Slice(64, 32)), "frame checksum");
        return bytes[Format.FrameHeaderBytes..];
    }
}
