using System.Collections.Immutable;

namespace FluffHistory.Application;

public enum ShellHistoryStatus { BackedUp, Inactive, NotProtected, RecoveryRequired, Unavailable, Expired }
public sealed record ShellRecentVersion(string Token, string Display);
public sealed record ShellHistorySummary(ShellHistoryStatus Status, string SessionId, string Summary, string BackupLocation,
    ImmutableArray<ShellRecentVersion> Rows, bool CanRestore);
public sealed record ShellExactRestore(BrowserVersion Version, SourceMapping Mapping);

/// <summary>One read-only captured browser and at most five exact handles. No scheduler, writer or password.
/// The host owns timeout/process isolation for native I/O that outlasts cooperative cancellation.</summary>
public sealed class ShellPathSession : IAsyncDisposable
{
    public const int MaximumCount = 4096, MaximumRecent = 5;
    private readonly Dictionary<string, BrowserVersion> _versions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1);
    private HistoryBrowserSession? _browser;
    private ShellPathTarget? _target;
    private AppSettings? _settings;
    private bool _disposed;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public ShellHistorySummary Summary { get; private set; } = new(ShellHistoryStatus.Unavailable, "", "", "", [], false);

    public static Task<ShellPathSession> OpenAsync(AppSettings settings, string path, ShellItemKind kind, IShellHistoryText text, CancellationToken token = default)
        => OpenBoundedAsync(settings, path, kind, MaximumCount, text, token);
    internal static async Task<ShellPathSession> OpenBoundedAsync(AppSettings settings, string path, ShellItemKind kind, int countLimit, IShellHistoryText text, CancellationToken token = default)
    {
        if (countLimit is < 128 or > MaximumCount || countLimit % 128 != 0) throw new ArgumentOutOfRangeException(nameof(countLimit));
        ArgumentNullException.ThrowIfNull(text);
        var session = new ShellPathSession { _settings = settings, _target = ShellPathResolver.Resolve(settings, path, kind) };
        try
        {
            var target = session._target;
            if (target is null) { session.Summary = Empty(ShellHistoryStatus.NotProtected, text); return session; }
            session._browser = await HistoryBrowserSession.OpenAsync(settings.RepositoryPath, token).ConfigureAwait(false);
            if (kind == ShellItemKind.Folder)
            {
                var page = await session._browser.GetTimelineAsync(new(target.SourceRootId, target.RelativePath), pageSize: MaximumRecent, token: token).ConfigureAwait(false);
                session.Summary = new(target.IsActive ? ShellHistoryStatus.BackedUp : page.Items.Length > 0 ? ShellHistoryStatus.Inactive : ShellHistoryStatus.NotProtected,
                    session.SessionId, text.Folder(page.Items), settings.RepositoryPath, [], false);
                return session;
            }
            BrowserContinuation? next = null; var count = 0; DateTimeOffset? oldest = null, latest = null;
            var rows = ImmutableArray.CreateBuilder<ShellRecentVersion>();
            do
            {
                token.ThrowIfCancellationRequested();
                var page = await session._browser.GetPathHistoryAsync(target.SourceRootId, target.RelativePath, next, 128, token).ConfigureAwait(false);
                foreach (var version in page.Items)
                {
                    count++; latest ??= version.StoredUtc;
                    if (oldest is null || version.StoredUtc < oldest) oldest = version.StoredUtc;
                    if (rows.Count < MaximumRecent)
                    {
                        var key = Guid.NewGuid().ToString("N"); session._versions.Add(key, version);
                        rows.Add(new(key, text.Version(version)));
                    }
                }
                next = page.Continuation;
            } while (next is not null && count < countLimit);
            var status = count == 0 ? ShellHistoryStatus.NotProtected : target.IsActive ? ShellHistoryStatus.BackedUp : ShellHistoryStatus.Inactive;
            session.Summary = new(status, session.SessionId, text.File(latest, oldest, count, next is null), settings.RepositoryPath, rows.ToImmutable(), count > 0);
            return session;
        }
        catch (OperationCanceledException) { await session.DisposeAsync().ConfigureAwait(false); throw; }
        catch (ApplicationOperationException error)
        {
            await session.ReleaseBrowserAsync().ConfigureAwait(false);
            session.Summary = Empty(error.Failure == ApplicationFailure.RecoveryRequired ? ShellHistoryStatus.RecoveryRequired : ShellHistoryStatus.Unavailable, text);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<ShellExactRestore> GetRestoreAsync(string sessionId, string versionToken, AppSettings currentSettings, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sessionId != SessionId || !_versions.TryGetValue(versionToken, out var version) || _target is null || _settings is null ||
                !SourcePaths.Same(currentSettings.RepositoryPath, _settings.RepositoryPath)) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
            var mapping = currentSettings.Sources.FirstOrDefault(s => s.SourceRootId == _target.SourceRootId);
            if (mapping?.RuntimePath is null || !SourcePaths.Same(mapping.RuntimePath, _target.RuntimeRoot))
                throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
            var livePath = Path.Combine(_target.RuntimeRoot, _target.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var currentTarget = ShellPathResolver.Resolve(currentSettings, livePath, ShellItemKind.File);
            if (currentTarget?.SourceRootId != _target.SourceRootId) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
            // The Windows restore leaf independently validates exact live repository authority and payload.
            return new(version, mapping);
        }
        finally { _gate.Release(); }
    }
    public static ShellHistorySummary Empty(ShellHistoryStatus status, IShellHistoryText text) => new(status, "", text.Empty(status), "", [], false);
    private async Task ReleaseBrowserAsync()
    { _versions.Clear(); if (_browser is { } browser) { _browser = null; await browser.DisposeAsync().ConfigureAwait(false); } }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await ReleaseBrowserAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
