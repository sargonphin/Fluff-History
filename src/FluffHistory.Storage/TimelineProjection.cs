using System.Buffers.Binary;
using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal enum TimelineReadStage { CollectEntry, ResolveEntry, WindowCompleted, SortTicks, DeduplicateTicks, SnapshotCompleted }

internal sealed class TimelineReadStatistics
{
    internal ComparisonReadStatistics Reads { get; } = new();
    internal long MatchingVersions, ValidatedVersions, MaximumTimestampCandidates;
    internal bool CacheHit;
}

public sealed partial class RepositoryReader
{
    private readonly IoStatistics _timelineIo = new();
    internal TimelineReadStatistics? LastTimelineStatistics { get; private set; }
    internal Action<TimelineReadStage>? TimelineObserve { get; set; } // Deterministic lifecycle tests only.
    internal Action<DateTimeOffset>? TimelineProjected { get; set; } // Phase A diagnostic-only collection; no retained production state.

    internal HistoryPage<DateTimeOffset> ReadTimelinePage(ComparisonReadBudget budget, HistoryScope scope, int pageSize = 128,
        HistoryDirection direction = HistoryDirection.NewestFirst, HistoryCursor? cursor = null, CancellationToken cancellationToken = default,
        TimelineTickCollector? collector = null)
    {
        ArgumentNullException.ThrowIfNull(scope); budget.Validate();
        var query = $"time|{scope.SourceRootId:N}|{scope.Folder}|{direction}|{pageSize}";
        CheckPage(pageSize, direction, cursor, query);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Workspace?.CancellationToken ?? default);
        var token = linked.Token;
        void Check() { token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); }
        void Observe(TimelineReadStage stage) { TimelineObserve?.Invoke(stage); Check(); }
        Check();
        var stats = new TimelineReadStatistics(); LastTimelineStatistics = stats; var reads = stats.Reads;
        using var segments = new SegmentReader(_repository.DirectoryPath, _repository.Identity, Captured.Root.Epoch,
            Captured.Address, statistics: reads.Io, comparison: reads, crypto: _repository.Crypto);
        _comparisonSessions.Add(segments); // Reader disposal also closes this operation's direct handles.
        try
        {
            byte[] ReadPage(FrameAddress address) { Check(); return segments.Read(address); }
            var index = new OrderedIndex(IndexKind.Versions, ReadPage);
            var lower = PathKeys.Scope(scope.SourceRootId, scope.Folder);
            using var entries = index.Range(Captured.Root.Versions, lower, ScopeUpper(scope, lower)).GetEnumerator();
            var requests = new PageEntry[budget.MaximumEntries];
            var frame = new byte[Format.FrameHeaderBytes + Format.MaximumFrameBytes];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var selected = new SortedSet<DateTimeOffset>();
            PageEntry? pending = null;
            while (true)
            {
                Check(); var allocation = GC.GetAllocatedBytesForCurrentThread();
                var count = 0; long keyBytes = 0;
                while (count < requests.Length)
                {
                    Check();
                    if (pending is null && !entries.MoveNext()) break;
                    var entry = pending ?? entries.Current; pending = null;
                    if (entry.Key.Length > budget.MaximumKeyBytes - keyBytes) { pending = entry; break; }
                    requests[count++] = entry; keyBytes += entry.Key.Length;
                    stats.MatchingVersions++; Observe(TimelineReadStage.CollectEntry);
                }
                reads.IndexAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocation;
                if (count == 0) break;
                reads.Windows++; reads.MaximumBufferedEntries = Math.Max(reads.MaximumBufferedEntries, count);
                reads.MaximumBufferedKeyBytes = Math.Max(reads.MaximumBufferedKeyBytes, keyBytes);
                allocation = GC.GetAllocatedBytesForCurrentThread();
                Array.Sort(requests, 0, count, TimelinePhysicalOrder);
                for (var i = 0; i < count; i++)
                {
                    Observe(TimelineReadStage.ResolveEntry);
                    var entry = requests[i]; var address = entry.Value;
                    MetadataCodec.RequireKind(address, FrameKind.Semantic);
                    var body = segments.ReadComparison(address, frame, hash);
                    var time = TimelineVersionDecoder.Decode(body, entry.Key, address, segments, Captured.Root.Sequence);
                    stats.ValidatedVersions++;
                    TimelineProjected?.Invoke(time);
                    collector?.Add(time.UtcTicks);
                    if (cursor?.Time is { } after && (direction == HistoryDirection.OldestFirst ? time <= after : time >= after)) continue;
                    selected.Add(time);
                    if (selected.Count > pageSize + 1) selected.Remove(direction == HistoryDirection.OldestFirst ? selected.Max : selected.Min);
                    stats.MaximumTimestampCandidates = Math.Max(stats.MaximumTimestampCandidates, selected.Count);
                }
                collector?.EndWindow(Observe);
                reads.ResolutionAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocation;
                Array.Clear(requests, 0, count);
                Observe(TimelineReadStage.WindowCompleted);
            }
            collector?.Complete(Observe);
            Check();
            var ordered = direction == HistoryDirection.OldestFirst ? selected : selected.Reverse();
            var times = ordered.Take(pageSize).ToArray();
            return new(Array.AsReadOnly(times), selected.Count > pageSize ? new(Authority, query, time: times[^1]) : null);
        }
        finally
        {
            _comparisonSessions.Remove(segments);
            _timelineIo.PagesRead += reads.Io.PagesRead; _timelineIo.SemanticReads += reads.Io.SemanticReads;
            _timelineIo.SegmentOpens += reads.Io.SegmentOpens; _timelineIo.BytesRead += reads.Io.BytesRead;
        }
    }

    private static readonly IComparer<PageEntry> TimelinePhysicalOrder = Comparer<PageEntry>.Create((a, b) =>
    {
        var segment = a.Value.SegmentId.CompareTo(b.Value.SegmentId);
        return segment != 0 ? segment : a.Value.Offset.CompareTo(b.Value.Offset);
    });
}

// Deliberately narrow projection of the unchanged VersionAdded encoding. Every field is validated,
// including the complete version key. Timeline returns StoredUtc; retention also uses projected
// identity/length fields. Neither path builds a restore metadata object graph.
internal static class TimelineVersionDecoder
{
    internal readonly record struct Fields(HistoryPath Path, Guid Id, long Sequence, int Ordinal, DateTimeOffset StoredUtc, long Length);
    internal static DateTimeOffset Decode(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key,
        FrameAddress address, SegmentReader segments, long sequence) => DecodeFields(body, key, address, segments, sequence).StoredUtc;

    internal static Fields DecodeFields(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key,
        FrameAddress address, SegmentReader segments, long sequence)
    {
        try
        {
            var r = new BufferReader(body);
            Format.Require(r.Byte() == (byte)SemanticKind.VersionAdded, "version reference type");
            var previous = r.Address(); MetadataCodec.RequireKind(previous, FrameKind.Semantic);
            if (previous is not null)
            { segments.CheckAddress(previous); Format.Require(previous.Serial < address.Serial, "semantic forward/cycle"); }
            var id = r.Guid(); var seq = r.I64(); var ordinal = r.I32(); var stored = ValidateUtc(r.I64());
            var root = r.Guid(); var path = new HistoryPath(root, r.Text(8192));
            var length = r.I64(); ValidateUtc(r.I64()); ValidateUtc(r.I64()); var attributes = r.I32();
            Format.Require(id != Guid.Empty && seq > 0 && seq <= sequence && ordinal is >= 0 and < MutationBatch.MaximumChanges &&
                length >= 0 && (attributes & ~(int)LogicalFileMetadata.SupportedAttributes) == 0, "version fields");
            _ = r.Take(8); r.End();
            Format.Require(MatchesKey(path, seq, ordinal, key), "version key binding");
            return new(path, id, seq, ordinal, stored, length);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        { throw new InvalidDataException("Malformed v4 timeline metadata value.", e); }
    }

    private static DateTimeOffset ValidateUtc(long ticks)
    {
        Format.Require(ticks > 0 && ticks <= DateTimeOffset.MaxValue.Ticks, "UTC ticks");
        return new(ticks, TimeSpan.Zero);
    }
    private static bool MatchesKey(HistoryPath path, long sequence, int ordinal, ReadOnlySpan<byte> key)
    {
        var pathLength = 18 + 2 * path.CanonicalPath.Length;
        if (key.Length != pathLength + 12 || key[pathLength - 1] != 0 || key[pathLength - 2] != 0) return false;
        if (new Guid(key[..16], bigEndian: true) != path.SourceRootId ||
            BinaryPrimitives.ReadInt64BigEndian(key[pathLength..]) != sequence ||
            BinaryPrimitives.ReadInt32BigEndian(key[(pathLength + 8)..]) != ordinal) return false;
        for (var i = 0; i < path.CanonicalPath.Length; i++)
            if (BinaryPrimitives.ReadUInt16BigEndian(key.Slice(16 + i * 2, 2)) != path.CanonicalPath[i]) return false;
        return true;
    }
}
