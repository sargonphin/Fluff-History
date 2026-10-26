using System.Collections.Immutable;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

/// <summary>One captured reader, serialized worker calls, no Dispatcher. Dispose cancels queued/active work,
/// joins reader ownership and disposes reader before key owner. Refresh explicitly invalidates old handles.</summary>
public sealed class HistoryBrowserSession : IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposalLock = new();
    private readonly object _selectionLock = new();
    private AuthorityIdentity? _selectionAuthority;
    public const int MaximumSelectionItems = 4096;
    private Repository? _repository;
    private RepositoryReader? _reader;
    private Guid _capture;
    private Task? _disposal;
    private int _disposed;
    internal Action? QueryEntered { get; set; }
    internal RepositoryReader ReaderForTests => _reader!;
    public BrowserState State { get; private set; } = null!;
    private HistoryBrowserSession(string path) => _path = path;

    public static async Task<HistoryBrowserSession> OpenAsync(string path, CancellationToken token = default)
    {
        var session = new HistoryBrowserSession(path);
        try { await session.RefreshAsync(token).ConfigureAwait(false); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task<BrowserState> RefreshAsync(CancellationToken token = default) => Execute(ct =>
    {
        lock (_selectionLock) { _selectionAuthority = null; }
        _reader?.Dispose(); _reader = null; _repository?.Dispose(); _repository = null;
        ct.ThrowIfCancellationRequested();
        var repository = ApplicationRepository.Open(_path);
        try
        {
            var reader = repository.OpenReader();
            try
            {
                var sources = ApplicationRepository.Sources(reader, ct);
                _repository = repository; _reader = reader;
                lock (_selectionLock)
                {
                    _capture = Guid.NewGuid(); _selectionAuthority = reader.Authority;
                    return State = new(repository.Identity.RepositoryId, _capture, sources);
                }
            }
            catch { reader.Dispose(); throw; }
        }
        catch { repository.Dispose(); throw; }
    }, token);

    public Task<IReadOnlySet<Guid>> GetHistoricalRootsAsync(CancellationToken token = default) => Execute<IReadOnlySet<Guid>>(ct =>
    {
        var roots = new HashSet<Guid>();
        foreach (var source in State.Sources) { ct.ThrowIfCancellationRequested(); if (Reader.HasHistory(new(source.SourceRootId), ct)) roots.Add(source.SourceRootId); }
        return roots;
    }, token);
    public Task<BrowserPage<DateTimeOffset>> GetTimelineAsync(BrowserScope scope, BrowserContinuation? continuation = null,
        int pageSize = 128, CancellationToken token = default) => Execute(ct =>
    {
        ValidateSize(pageSize); var query = Query("timeline", scope, pageSize); CheckContinuation(continuation, query);
        var page = Reader.GetTimelinePage(ToScope(scope), pageSize, HistoryDirection.NewestFirst, continuation?.Cursor, ct);
        return new BrowserPage<DateTimeOffset>(page.Items.ToImmutableArray(), Next(query, page.Continuation));
    }, token);

    /// <summary>Strict nearest timestamp on this exact capture. Keeps only one bounded page and one
    /// candidate even when the requested instant is older than the UI's retained timestamp window.</summary>
    public Task<DateTimeOffset?> FindTimelineNeighborAsync(BrowserScope scope, DateTimeOffset? atUtc, bool earlier, CancellationToken token = default)
        => Execute<DateTimeOffset?>(ct =>
        {
            if (atUtc is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.");
            if (!earlier && atUtc is null) return null;
            HistoryCursor? cursor = null; DateTimeOffset? candidate = null;
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = Reader.GetTimelinePage(ToScope(scope), 128, HistoryDirection.NewestFirst, cursor, ct);
                foreach (var time in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (earlier && (atUtc is null || time < atUtc)) return time;
                    if (!earlier)
                    {
                        if (time <= atUtc) return candidate;
                        candidate = time;
                    }
                }
                cursor = page.Continuation;
            } while (cursor is not null);
            return candidate;
        }, token);

    public Task<BrowserPage<BrowserVersion>> GetPathHistoryAsync(BrowserVersion version, BrowserContinuation? continuation = null,
        int pageSize = 128, CancellationToken token = default) => Execute(ct =>
    {
        ValidateSize(pageSize); CheckAuthority(version.Authority);
        var query = $"path:{version.SourceRootId}:{version.Identity.Path.CanonicalPath}:{pageSize}"; CheckContinuation(continuation, query);
        var page = Reader.GetPathHistoryPage(version.Identity.Path, pageSize, HistoryDirection.NewestFirst, continuation?.Cursor, ct);
        var current = Reader.GetCurrent(version.Identity.Path)?.Identity;
        return new BrowserPage<BrowserVersion>(page.Items.Select(v => new BrowserVersion(v, Reader.Authority, v.Identity == current)).ToImmutableArray(), Next(query, page.Continuation));
    }, token);

    /// <summary>Exact logical path lookup without first manufacturing a displayed version.</summary>
    public Task<BrowserPage<BrowserVersion>> GetPathHistoryAsync(Guid sourceRootId, string relativePath,
        BrowserContinuation? continuation = null, int pageSize = 128, CancellationToken token = default) => Execute(ct =>
    {
        ValidateSize(pageSize);
        var path = new HistoryPath(sourceRootId, relativePath);
        if (!State.Sources.Any(s => s.SourceRootId == sourceRootId))
            return new BrowserPage<BrowserVersion>([], null);
        var query = $"path:{sourceRootId}:{path.CanonicalPath}:{pageSize}"; CheckContinuation(continuation, query);
        var page = Reader.GetPathHistoryPage(path, pageSize, HistoryDirection.NewestFirst, continuation?.Cursor, ct);
        var current = Reader.GetCurrent(path)?.Identity;
        return new BrowserPage<BrowserVersion>(page.Items.Select(v => new BrowserVersion(v, Reader.Authority, v.Identity == current)).ToImmutableArray(), Next(query, page.Continuation));
    }, token);

    /// <summary>Bounded exact focus for a live Shell file, even beyond the first displayed children page.</summary>
    public Task<BrowserChild?> GetLatestFileAsync(Guid sourceRootId, string relativePath, CancellationToken token = default) => Execute<BrowserChild?>(ct =>
    {
        var path = new HistoryPath(sourceRootId, relativePath); var version = Reader.GetNewestVersion(path);
        if (version is null) return null;
        var relative = version.Metadata.RelativePath; var slash = relative.LastIndexOf('/');
        var name = slash < 0 ? relative : relative[(slash + 1)..]; var parent = slash < 0 ? "" : relative[..slash];
        var request = new DisplayedRestoreRequest(Reader.Authority, new(sourceRootId, parent), new HistoryView(null),
            [new DisplayedRestoreFile(version.Identity)], InteractiveRestoreDestination.AnotherFolder);
        return new(name, false, Reader.HasHistory(new(sourceRootId, relative), ct),
            new BrowserVersion(version, Reader.Authority, Reader.GetCurrent(path)?.Identity == version.Identity), new(request, _capture));
    }, token);

    public Task<BrowserPage<BrowserChild>> GetChildrenAsync(BrowserScope scope, BrowserContinuation? continuation = null,
        int pageSize = 128, CancellationToken token = default) => Execute(ct =>
    {
        ValidateSize(pageSize); var query = Query("children", scope, pageSize); CheckContinuation(continuation, query);
        var storageScope = ToScope(scope); var view = new HistoryView(scope.AtUtc);
        var prefixLength = storageScope.Folder.Length == 0 ? 0 : storageScope.Folder.Length + 1;
        var componentIndex = storageScope.Folder.Length == 0 ? 0 : storageScope.Folder.Count(c => c == '/') + 1;
        var rows = ImmutableArray.CreateBuilder<BrowserChild>();
        var pending = continuation?.Pending ?? []; var index = 0; var cursor = continuation?.Cursor;
        var end = continuation?.End ?? false; var lastFolder = continuation?.LastFolder;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (index == pending.Length)
            {
                if (end) break;
                var page = Reader.GetScopeHistoryPage(storageScope, view, 128, cursor, ct);
                pending = page.Items.ToImmutableArray(); index = 0; cursor = page.Continuation; end = cursor is null;
                if (pending.Length == 0) { if (end) break; continue; }
            }
            var item = pending[index]; var suffix = item.Version.Identity.Path.CanonicalPath[prefixLength..];
            var slash = suffix.IndexOf('/'); var isFolder = slash >= 0; var key = isFolder ? suffix[..slash] : suffix;
            if (isFolder && key == lastFolder) { index++; continue; }
            if (rows.Count == pageSize) break; // Retain at most one backend page, never the entire descendant group.
            index++;
            var name = item.Version.Metadata.RelativePath.Split('/')[componentIndex];
            var version = isFolder ? null : new BrowserVersion(item.Version, Reader.Authority, Reader.GetCurrent(item.Version.Identity.Path)?.Identity == item.Version.Identity);
            var request = new DisplayedRestoreRequest(Reader.Authority, storageScope, view,
                [isFolder ? new DisplayedRestoreFolder(name) : new DisplayedRestoreFile(item.Version.Identity)],
                InteractiveRestoreDestination.AnotherFolder);
            rows.Add(new(name, isFolder, item.HasConflict, version, new(request, _capture)));
            if (isFolder) lastFolder = key;
        }
        var next = index < pending.Length || !end ? new BrowserContinuation { Capture = _capture, Query = query,
            Pending = pending[index..], Cursor = cursor, End = end, LastFolder = lastFolder } : null;
        return new BrowserPage<BrowserChild>(rows.ToImmutable(), next);
    }, token);

    /// <summary>Freeze already displayed children without repository I/O. Only this active capture and
    /// one exact scope/view may be combined. Restoring still independently validates live authority.</summary>
    public BrowserSelection CreateSelection(IEnumerable<BrowserChild> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var frozen = children.Take(MaximumSelectionItems + 1).ToArray();
        if (frozen.Length is 0 or > MaximumSelectionItems || frozen.Any(c => c is null))
            throw new ApplicationOperationException(ApplicationFailure.InvalidSelection);
        lock (_selectionLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var first = frozen[0].Selection.Request;
            var identities = new HashSet<DisplayedRestoreItem>();
            var items = ImmutableArray.CreateBuilder<DisplayedRestoreItem>(frozen.Length);
            foreach (var child in frozen)
            {
                var selection = child.Selection; var request = selection.Request;
                if (selection.Capture != _capture || request.Authority != _selectionAuthority)
                    throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
                if (request.Scope != first.Scope || request.View != first.View || request.Items.Count != 1 ||
                    !identities.Add(request.Items[0]))
                    throw new ApplicationOperationException(ApplicationFailure.InvalidSelection);
                items.Add(request.Items[0]);
            }
            return new(first with { Items = items.ToImmutable() }, _capture);
        }
    }

    private RepositoryReader Reader => _reader ?? throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
    private void CheckAuthority(AuthorityIdentity authority)
    { if (Reader.Authority != authority) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired); }
    private void CheckContinuation(BrowserContinuation? continuation, string query)
    { if (continuation is not null && (continuation.Capture != _capture || continuation.Query != query)) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired); }
    private BrowserContinuation? Next(string query, HistoryCursor? cursor) => cursor is null ? null : new() { Capture = _capture, Query = query, Cursor = cursor };
    private static HistoryScope ToScope(BrowserScope scope) => new(scope.SourceRootId, scope.Folder);
    private static string Query(string kind, BrowserScope scope, int size)
        => $"{kind}:{ToScope(scope).SourceRootId}:{ToScope(scope).Folder}:{scope.AtUtc?.UtcTicks}:{size}";
    private static void ValidateSize(int size) { if (size is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(size)); }

    private Task<T> Execute<T>(Func<CancellationToken, T> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                linked.Token.ThrowIfCancellationRequested(); QueryEntered?.Invoke();
                _lifetime.Token.ThrowIfCancellationRequested(); linked.Token.ThrowIfCancellationRequested();
                return action(linked.Token);
            }
            catch (ApplicationOperationException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (ObjectDisposedException) { throw; }
            catch (Exception error) { throw ApplicationRepository.Failure(error); }
            finally { _gate.Release(); }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalLock)
        {
            if (_disposal is not null) return new(_disposal);
            Volatile.Write(ref _disposed, 1);
            lock (_selectionLock) { _selectionAuthority = null; }
            var cancellation = _lifetime.CancelAsync();
            _disposal = Task.Run(async () =>
            {
                await cancellation.ConfigureAwait(false);
                await _gate.WaitAsync().ConfigureAwait(false);
                try { _reader?.Dispose(); _reader = null; _repository?.Dispose(); _repository = null; }
                finally { _gate.Release(); }
                // The small CTS/semaphore stay valid for already scheduled callers to observe cancellation safely.
            });
            return new(_disposal);
        }
    }
}
