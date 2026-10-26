using FluffHistory.Core;

namespace FluffHistory.Storage;

public enum HistoryDirection { OldestFirst, NewestFirst }

public sealed record HistoryScope
{
    public Guid SourceRootId { get; }
    public string Folder { get; }
    public HistoryScope(Guid sourceRootId, string folder = "")
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (sourceRootId == Guid.Empty) throw new ArgumentException("A logical root is required.", nameof(sourceRootId));
        SourceRootId = sourceRootId; Folder = folder.Length == 0 ? "" : new HistoryPath(sourceRootId, folder).CanonicalPath;
    }
    internal bool Contains(HistoryPath path) => path.SourceRootId == SourceRootId &&
        (Folder.Length == 0 || path.CanonicalPath.StartsWith(Folder + "/", StringComparison.Ordinal));
}

/// <summary>Null time selects latest publication; a UTC time selects max (StoredUtc, sequence, ordinal) at/before it.</summary>
public sealed record HistoryView
{
    public DateTimeOffset? AtUtc { get; }
    public HistoryView(DateTimeOffset? atUtc = null)
    {
        if (atUtc is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(atUtc));
        AtUtc = atUtc;
    }
}

/// <summary>Opaque transient continuation. Bound to authority, query, direction and page size; never persisted.</summary>
public sealed class HistoryCursor
{
    internal AuthorityIdentity Authority { get; }
    internal string Query { get; }
    internal byte[]? Key { get; }
    internal DateTimeOffset? Time { get; }
    internal HistoryCursor(AuthorityIdentity authority, string query, byte[]? key = null, DateTimeOffset? time = null)
    { Authority = authority; Query = query; Key = key; Time = time; }
}

public sealed record HistoryPage<T>(IReadOnlyList<T> Items, HistoryCursor? Continuation);
public sealed record HistoricalConflict(HistoryPath File, HistoryPath Descendant);
public sealed record ScopeHistoryItem(StoredVersion Version, HistoricalConflict? AncestorConflict, HistoricalConflict? DescendantConflict)
{
    public bool HasConflict => AncestorConflict is not null || DescendantConflict is not null;
}
public sealed record HistoryReadStatistics(long IndexPagesRead, long SemanticFramesResolved, long SegmentOpens, long BytesRequested);

public sealed partial class RepositoryReader
{
    public HistoryReadStatistics HistoryStatistics => new(Segments.Statistics.PagesRead + _timelineIo.PagesRead,
        Segments.Statistics.SemanticReads + _timelineIo.SemanticReads,
        Segments.Statistics.SegmentOpens + _timelineIo.SegmentOpens, Segments.Statistics.BytesRead + _timelineIo.BytesRead);
    public const int MaximumHistoryPageSize = 4096;
    /// <summary>Captured-scope presence using one index entry, without traversing its history.</summary>
    public bool HasHistory(HistoryScope scope, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); RequireTimelineEpoch();
        var prefix = PathKeys.Scope(scope.SourceRootId, scope.Folder);
        var entry = VersionIndex.Range(Captured.Root.Versions, prefix, ScopeUpper(scope, prefix)).FirstOrDefault();
        if (entry is not null) _ = IndexedVersion(entry);
        token.ThrowIfCancellationRequested(); RequireTimelineEpoch(); return entry is not null;
    }

    /// <summary>Resolve exactly this surviving identity; never substitute current/newest.</summary>
    public StoredVersion? GetVersion(VersionIdentity identity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.Path);
        if (identity.VersionId == Guid.Empty) throw new ArgumentException("A version UUID is required.", nameof(identity));
        var key = PathKeys.Version(identity); var address = VersionIndex.Find(Captured.Root.Versions, key);
        if (address is null) return null;
        var version = Version(address, identity.Path, key);
        return version.Identity == identity ? version : null;
    }

    public HistoryPage<StoredVersion> GetPathHistoryPage(HistoryPath path, int pageSize = 128,
        HistoryDirection direction = HistoryDirection.NewestFirst, HistoryCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path); var query = $"path|{path.SourceRootId:N}|{path.CanonicalPath}|{direction}|{pageSize}";
        CheckPage(pageSize, direction, cursor, query); cancellationToken.ThrowIfCancellationRequested();
        var prefix = PathKeys.Current(path); var reverse = direction == HistoryDirection.NewestFirst;
        var lower = !reverse && cursor is not null ? PathKeys.UpperBound(cursor.Key!) : prefix;
        var upper = reverse && cursor is not null ? cursor.Key! : PathKeys.UpperBound(prefix);
        var items = new List<StoredVersion>(); byte[]? last = null; var more = false;
        foreach (var entry in VersionIndex.Range(Captured.Root.Versions, lower, upper, reverse))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count == pageSize) { more = true; break; }
            items.Add(Version(entry.Value, path, entry.Key)); last = entry.Key;
        }
        return new(items.AsReadOnly(), more ? new(Authority, query, last) : null);
    }

    public HistoryPage<ScopeHistoryItem> GetScopeHistoryPage(HistoryScope scope, HistoryView? view = null, int pageSize = 128,
        HistoryCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); view ??= new();
        var query = $"scope|{scope.SourceRootId:N}|{scope.Folder}|{view.AtUtc?.Ticks}|{pageSize}";
        CheckPage(pageSize, HistoryDirection.OldestFirst, cursor, query); cancellationToken.ThrowIfCancellationRequested();
        var lower = PathKeys.Scope(scope.SourceRootId, scope.Folder);
        var items = new List<ScopeHistoryItem>(); byte[]? last = null; var more = false;
        foreach (var version in SelectScope(scope, view, cursor?.Key ?? lower, cancellationToken))
        {
            if (items.Count == pageSize) { more = true; break; }
            var path = version.Identity.Path; HistoricalConflict? ancestor = null;
            var name = path.CanonicalPath;
            while (name.LastIndexOf('/') is var slash && slash >= 0)
            {
                name = name[..slash]; var parent = new HistoryPath(scope.SourceRootId, name);
                if (!scope.Contains(parent)) break;
                if (SelectPath(parent, view, cancellationToken) is not null) { ancestor = new(parent, path); break; }
            }
            var childScope = new HistoryScope(scope.SourceRootId, path.CanonicalPath);
            var child = SelectScope(childScope, view, PathKeys.Scope(scope.SourceRootId, path.CanonicalPath), cancellationToken).FirstOrDefault();
            items.Add(new(version, ancestor, child is null ? null : new(path, child.Identity.Path)));
            last = PathKeys.UpperBound(PathKeys.Current(path));
        }
        return new(items.AsReadOnly(), more ? new(Authority, query, last) : null);
    }

    /// <summary>Exact captured timeline. Cold scope requests scan surviving history; subsequent pages reuse one bounded
    /// transient timestamp snapshot. Scope changes recache; over-budget requests retain exact streaming behavior.</summary>
    public HistoryPage<DateTimeOffset> GetTimelinePage(HistoryScope scope, int pageSize = 128,
        HistoryDirection direction = HistoryDirection.NewestFirst, HistoryCursor? cursor = null, CancellationToken cancellationToken = default)
        => ReadCachedTimelinePage(new TimelineCacheBudget(), scope, pageSize, direction, cursor, cancellationToken);

    private void CheckPage(int size, HistoryDirection direction, HistoryCursor? cursor, string query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size is < 1 or > MaximumHistoryPageSize || !Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(size));
        if (cursor is not null && (cursor.Authority != Authority || cursor.Query != query))
            throw new ArgumentException("Continuation belongs to another authority/query; restart explicitly.", nameof(cursor));
    }
    private StoredVersion IndexedVersion(PageEntry entry) => Version(entry.Value, versionKey: entry.Key);
    private static byte[]? ScopeUpper(HistoryScope scope, byte[] prefix) =>
        scope.Folder.Length == 0 && prefix.All(b => b == byte.MaxValue) ? null : PathKeys.UpperBound(prefix);
    private StoredVersion? SelectPath(HistoryPath path, HistoryView view, CancellationToken token)
    {
        StoredVersion? selected = null;
        foreach (var version in EnumerateVersions(path, newestFirst: true))
        {
            token.ThrowIfCancellationRequested();
            if (view.AtUtc is null) return version;
            if (Eligible(version, view) && (selected is null || LaterTime(version, selected))) selected = version;
        }
        return selected;
    }
    private static bool Eligible(StoredVersion version, HistoryView view) => view.AtUtc is null || version.StoredUtc <= view.AtUtc;
    private static bool LaterTime(StoredVersion a, StoredVersion b) => a.StoredUtc > b.StoredUtc || a.StoredUtc == b.StoredUtc &&
        (a.Identity.Sequence > b.Identity.Sequence || a.Identity.Sequence == b.Identity.Sequence && a.Identity.Ordinal > b.Identity.Ordinal);
    private IEnumerable<StoredVersion> SelectScope(HistoryScope scope, HistoryView view, byte[] lower, CancellationToken token)
    {
        var scopeLower = PathKeys.Scope(scope.SourceRootId, scope.Folder);
        HistoryPath? path = null; StoredVersion? selected = null; PageEntry? last = null;
        foreach (var entry in VersionIndex.Range(Captured.Root.Versions, lower, ScopeUpper(scope, scopeLower)))
        {
            token.ThrowIfCancellationRequested();
            var nextPath = PathKeys.DecodePath(entry.Key.AsSpan(0, entry.Key.Length - 12));
            if (path is not null && path != nextPath)
            {
                if (view.AtUtc is null) selected = IndexedVersion(last!);
                if (selected is not null) yield return selected;
                selected = null;
            }
            path = nextPath; last = entry;
            if (view.AtUtc is not null)
            {
                var version = IndexedVersion(entry);
                if (Eligible(version, view) && (selected is null || LaterTime(version, selected))) selected = version;
            }
        }
        if (last is not null && view.AtUtc is null) selected = IndexedVersion(last);
        if (selected is not null) yield return selected;
    }
}
