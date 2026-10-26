using System.Diagnostics;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal sealed record TimelineCacheBudget(int MaximumCollectedTicks = 1_048_576)
{
    internal void Validate()
    {
        if (MaximumCollectedTicks is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(MaximumCollectedTicks));
    }
}

internal sealed class TimelineCacheStatistics
{
    internal long Builds, Hits, Misses, ScopeChanges, Overflows, EpochChecks;
    internal long RetainedTimestamps, MaximumTemporaryTimestampBytes;
    internal long LastBuildTemporaryTimestampBytes, LastBuildCollectedTicks;
    internal double LastBuildSortMilliseconds, LastBuildDeduplicateMilliseconds;
    internal long RetainedTimestampBytes => RetainedTimestamps * sizeof(long);
}

public sealed partial class RepositoryReader
{
    private sealed record TimelineSnapshot(AuthorityIdentity Authority, HistoryScope Scope, long[] Ticks);
    private TimelineSnapshot? _timelineSnapshot;
    private HistoryScope? _timelineScope;
    internal TimelineCacheStatistics TimelineCacheStatistics { get; } = new();

    internal HistoryPage<DateTimeOffset> ReadCachedTimelinePage(TimelineCacheBudget budget, HistoryScope scope, int pageSize = 128,
        HistoryDirection direction = HistoryDirection.NewestFirst, HistoryCursor? cursor = null, CancellationToken cancellationToken = default,
        ComparisonReadBudget? readBudget = null)
    {
        ArgumentNullException.ThrowIfNull(scope); budget.Validate(); readBudget ??= new(); readBudget.Validate();
        var query = $"time|{scope.SourceRootId:N}|{scope.Folder}|{direction}|{pageSize}";
        CheckPage(pageSize, direction, cursor, query);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Workspace?.CancellationToken ?? default);
        var token = linked.Token;
        void Check() { token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); }
        Check();
        if (_timelineScope is not null && _timelineScope != scope)
        { TimelineCacheStatistics.ScopeChanges++; DropTimelineSnapshot(); }
        _timelineScope = scope;
        if (_timelineSnapshot is { } snapshot && snapshot.Authority == Authority && snapshot.Scope == scope)
        {
            // Completed timestamps are captured materialized data, not fresh frame validation.
            // Keep the existing explicit epoch-expiry behavior even though this page needs no
            // historical handles. This bounded, validated state read never refreshes the capture.
            RequireTimelineEpoch(); Check();
            var page = Slice(snapshot.Ticks, pageSize, direction, cursor?.Time, query);
            Check(); LastTimelineStatistics = new() { CacheHit = true }; TimelineCacheStatistics.Hits++;
            return page;
        }
        TimelineCacheStatistics.Misses++;
        using var collector = new TimelineTickCollector(budget.MaximumCollectedTicks, readBudget.MaximumEntries);
        try
        {
            // The original pageSize+1 selector remains active during this ONE traversal. If
            // collection overflows, the current exact page is already known; do not scan twice.
            var page = ReadTimelinePage(readBudget, scope, pageSize, direction, cursor, token, collector);
            Check();
            if (collector.Overflowed) { TimelineCacheStatistics.Overflows++; return page; }
            RequireTimelineEpoch(); Check();
            var ticks = collector.TakeResult();
            _timelineSnapshot = new(Authority, scope, ticks);
            TimelineCacheStatistics.RetainedTimestamps = ticks.Length; TimelineCacheStatistics.Builds++;
            return page;
        }
        finally
        {
            TimelineCacheStatistics.LastBuildTemporaryTimestampBytes = collector.MaximumTemporaryBytes;
            TimelineCacheStatistics.LastBuildCollectedTicks = collector.CollectedTicks;
            TimelineCacheStatistics.LastBuildSortMilliseconds = collector.SortMilliseconds;
            TimelineCacheStatistics.LastBuildDeduplicateMilliseconds = collector.DeduplicateMilliseconds;
            TimelineCacheStatistics.MaximumTemporaryTimestampBytes = Math.Max(
                TimelineCacheStatistics.MaximumTemporaryTimestampBytes, collector.MaximumTemporaryBytes);
        }
    }

    private void RequireTimelineEpoch()
    {
        EncryptionMigrationControl.RefuseOrdinaryOpen(_repository.DirectoryPath);
        TimelineCacheStatistics.EpochChecks++;
        try
        {
            if (AuthorityFiles.CurrentEpoch(_repository.DirectoryPath, Authority.RepositoryId, _repository.Crypto) != Captured.Root.Epoch)
                throw new ReaderGenerationExpiredException();
        }
        catch { DropTimelineSnapshot(); _repository.CheckBootstrap(); throw; }
    }

    private void DropTimelineSnapshot()
    { _timelineSnapshot = null; TimelineCacheStatistics.RetainedTimestamps = 0; }

    private HistoryPage<DateTimeOffset> Slice(long[] ticks, int size, HistoryDirection direction, DateTimeOffset? after, string query)
    {
        var forward = direction == HistoryDirection.OldestFirst;
        var position = forward ? 0 : ticks.Length - 1;
        if (after is not null)
        {
            var found = Array.BinarySearch(ticks, after.Value.UtcTicks);
            position = found >= 0 ? found + (forward ? 1 : -1) : ~found - (forward ? 0 : 1);
        }
        var count = Math.Min(size, forward ? ticks.Length - position : position + 1);
        var times = new DateTimeOffset[count];
        for (var i = 0; i < count; i++, position += forward ? 1 : -1) times[i] = new(ticks[position], TimeSpan.Zero);
        var more = position >= 0 && position < ticks.Length;
        return new(Array.AsReadOnly(times), more ? new(Authority, query, time: times[^1]) : null);
    }
}

// Timestamp state only: no version/path/UUID/address objects. Each physical read window is
// sorted/deduplicated before append, so duplicate-heavy history does not consume one slot per
// version. Duplicates across windows may still consume slots until the final sort/dedup.
internal sealed class TimelineTickCollector(int maximumTicks, int windowEntries) : IDisposable
{
    private long[] _window = new long[windowEntries];
    private long[] _ticks = [];
    private long[] _result = [];
    private int _windowCount, _count;
    private long _sortTicks, _deduplicateTicks;
    internal bool Overflowed { get; private set; }
    internal long MaximumTemporaryBytes { get; private set; } = windowEntries * sizeof(long);
    internal long CollectedTicks => _count;
    internal long RetainedTicks => _result.Length;
    internal double SortMilliseconds => _sortTicks * 1000.0 / Stopwatch.Frequency;
    internal double DeduplicateMilliseconds => _deduplicateTicks * 1000.0 / Stopwatch.Frequency;

    internal void Add(long tick)
    { if (!Overflowed) _window[_windowCount++] = tick; }

    internal void EndWindow(Action<TimelineReadStage> observe)
    {
        if (Overflowed || _windowCount == 0) return;
        observe(TimelineReadStage.SortTicks); var start = Stopwatch.GetTimestamp(); Array.Sort(_window, 0, _windowCount);
        _sortTicks += Stopwatch.GetTimestamp() - start;
        observe(TimelineReadStage.DeduplicateTicks); start = Stopwatch.GetTimestamp();
        for (var i = 0; i < _windowCount; i++)
        {
            if ((i & 4095) == 0) observe(TimelineReadStage.DeduplicateTicks);
            if (i != 0 && _window[i] == _window[i - 1]) continue;
            if (_count == maximumTicks)
            { Overflowed = true; _ticks = []; _window = []; _windowCount = _count = 0; _deduplicateTicks += Stopwatch.GetTimestamp() - start; return; }
            if (_count == _ticks.Length)
            {
                var capacity = (int)Math.Min(maximumTicks, Math.Max(256L, 2L * _ticks.Length));
                Peak(_window.LongLength + _ticks.LongLength + capacity);
                Array.Resize(ref _ticks, capacity);
            }
            _ticks[_count++] = _window[i];
        }
        _deduplicateTicks += Stopwatch.GetTimestamp() - start;
        _windowCount = 0;
    }

    internal void Complete(Action<TimelineReadStage> observe)
    {
        if (Overflowed) return;
        EndWindow(observe);
        if (Overflowed) return;
        observe(TimelineReadStage.SortTicks); var start = Stopwatch.GetTimestamp(); Array.Sort(_ticks, 0, _count);
        _sortTicks += Stopwatch.GetTimestamp() - start;
        observe(TimelineReadStage.DeduplicateTicks); start = Stopwatch.GetTimestamp(); var unique = 0;
        for (var i = 0; i < _count; i++)
        {
            if ((i & 4095) == 0) observe(TimelineReadStage.DeduplicateTicks);
            if (unique == 0 || _ticks[i] != _ticks[unique - 1]) _ticks[unique++] = _ticks[i];
        }
        _deduplicateTicks += Stopwatch.GetTimestamp() - start;
        Peak(_window.LongLength + _ticks.LongLength + unique);
        _result = _ticks.AsSpan(0, unique).ToArray(); _ticks = []; _window = [];
        observe(TimelineReadStage.SnapshotCompleted);
    }

    private void Peak(long slots) => MaximumTemporaryBytes = Math.Max(MaximumTemporaryBytes, slots * sizeof(long));
    internal long[] TakeResult() { var result = _result; _result = []; return result; }
    public void Dispose() { _ticks = []; _window = []; _result = []; }
}
