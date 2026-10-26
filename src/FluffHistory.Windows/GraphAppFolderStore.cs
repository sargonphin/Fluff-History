using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>Fixed Graph v1.0, exact AppFolder objects only. OAuth stays outside Storage/Agent IPC.
/// Uses per-request authorization and disables automatic redirects so a download never inherits it.</summary>
public sealed class GraphAppFolderStore : ICloudRecoveryStore
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0/me/drive/";
    private const int MetadataLimit = 32 * 1024;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string? _accessToken;
    private string? _appRoot;

    public static GraphAppFolderStore Create(string accessToken) => new(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false }, accessToken);

    internal GraphAppFolderStore(HttpMessageHandler handler, string accessToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("Missing access token.", nameof(accessToken));
        _accessToken = accessToken;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _delay = delay ?? Task.Delay;
    }

    public Task<CloudRecoveryItem> CreateAsync(Guid repositoryId, Guid generationId, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        if (repositoryId == Guid.Empty || generationId == Guid.Empty || content.Length is 0 or > ICloudRecoveryStore.MaximumContentBytes)
            throw new ArgumentException("Invalid recovery object identity or size.");
        return Bounded(async cancellation =>
        {
            var root = await AppRoot(cancellation).ConfigureAwait(false);
            var name = $"recovery-{repositoryId:N}-{generationId:N}.json";
            var path = $"items/{Escape(root)}:/{name}:/content?@microsoft.graph.conflictBehavior=fail";
            // No write retries: a lost response can leave a harmless cloud-only orphan.
            using var response = await SendGraph(HttpMethod.Put, path, content, null, false, cancellation).ConfigureAwait(false);
            var item = await Metadata(response, cancellation).ConfigureAwait(false);
            if (item.Name != name) throw Failure(CloudRecoveryFailure.ObjectChanged);
            // Return the exact server identity even before orchestration performs a separate fresh readback.
            return new CloudRecoveryItem(item.Id, item.ETag, item.Name, []);
        }, token);
    }

    public async Task InspectAsync(CancellationToken token) => _ = await Bounded(AppRoot, token).ConfigureAwait(false);

    public Task<CloudRecoveryItem> ReadExactAsync(string itemId, CancellationToken token)
    {
        RequireId(itemId);
        return Bounded(async cancellation =>
        {
            _ = await AppRoot(cancellation).ConfigureAwait(false);
            var path = "items/" + Escape(itemId);
            var before = await ReadMetadata(path, cancellation).ConfigureAwait(false);
            if (before.Id != itemId) throw Failure(CloudRecoveryFailure.ObjectChanged);
            byte[]? content = null;
            try
            {
                using var response = await SendGraph(HttpMethod.Get, path + "/content", null, null, true, cancellation).ConfigureAwait(false);
                content = await Download(response, cancellation).ConfigureAwait(false);
                var after = await ReadMetadata(path, cancellation).ConfigureAwait(false);
                if (after.Id != itemId || after.ETag != before.ETag) throw Failure(CloudRecoveryFailure.ObjectChanged);
                var result = new CloudRecoveryItem(after.Id, after.ETag, after.Name, content);
                content = null; return result;
            }
            finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
        }, token);
    }

    public Task<CloudRecoveryDeleteResult> DeleteExactAsync(string itemId, string eTag, CancellationToken token)
    {
        RequireId(itemId); RequireTag(eTag);
        return Bounded(async cancellation =>
        {
            _ = await AppRoot(cancellation).ConfigureAwait(false);
            // Caller proves expected content first. The eTag binds deletion to that exact observation.
            using var response = await SendGraph(HttpMethod.Delete, "items/" + Escape(itemId), null, eTag, false, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return CloudRecoveryDeleteResult.AlreadyAbsent;
            if (response.StatusCode != HttpStatusCode.NoContent) throw Failure(CloudRecoveryFailure.ServiceUnavailable);
            return CloudRecoveryDeleteResult.Deleted;
        }, token);
    }

    private async Task<string> AppRoot(CancellationToken token)
    {
        if (_appRoot is not null) return _appRoot;
        using var response = await SendGraph(HttpMethod.Get, "special/approot", null, null, true, token, appRoot: true).ConfigureAwait(false);
        var bytes = await ReadBounded(response, MetadataLimit, token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            var id = Text(root, "id", 512); RequireId(id);
            if (!root.TryGetProperty("folder", out var folder) || folder.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("specialFolder", out var special) || !Text(special, "name", 32).Equals("approot", StringComparison.OrdinalIgnoreCase))
                throw Failure(CloudRecoveryFailure.InvalidObject);
            // Live consumer Graph returns a case variant in this facet. The request alias above
            // remains the documented case-sensitive lowercase /special/approot endpoint.
            return _appRoot = id;
        }
        catch (JsonException) { throw Failure(CloudRecoveryFailure.InvalidObject); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record ItemMetadata(string Id, string ETag, string Name);
    private async Task<ItemMetadata> ReadMetadata(string path, CancellationToken token)
    {
        using var response = await SendGraph(HttpMethod.Get, path + "?$select=id,name,eTag,file", null, null, true, token).ConfigureAwait(false);
        return await Metadata(response, token).ConfigureAwait(false);
    }
    private static async Task<ItemMetadata> Metadata(HttpResponseMessage response, CancellationToken token)
    {
        var bytes = await ReadBounded(response, MetadataLimit, token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            var id = Text(root, "id", 512); var tag = Text(root, "eTag", 1024); var name = Text(root, "name", 256);
            RequireId(id); RequireTag(tag);
            if (!root.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object)
                throw Failure(CloudRecoveryFailure.InvalidObject);
            return new(id, tag, name);
        }
        catch (JsonException) { throw Failure(CloudRecoveryFailure.InvalidObject); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<HttpResponseMessage> SendGraph(HttpMethod method, string path, ReadOnlyMemory<byte>? content,
        string? tag, bool retryRead, CancellationToken token, bool appRoot = false)
    {
        ObjectDisposedException.ThrowIf(_accessToken is null, this);
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, GraphRoot + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            if (tag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(tag));
            if (content is { } body)
            {
                request.Content = new ReadOnlyMemoryContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var status = response.StatusCode;
            if (response.IsSuccessStatusCode || status == HttpStatusCode.Found && method == HttpMethod.Get && path.EndsWith("/content", StringComparison.Ordinal) ||
                status == HttpStatusCode.NotFound && method == HttpMethod.Delete) return response;
            if (retryRead && attempt < 2 && (status == HttpStatusCode.TooManyRequests || (int)status is 500 or 502 or 503 or 504))
            {
                var wait = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
                // Never retry earlier than Retry-After. A longer delay is returned to the user instead.
                if (wait >= TimeSpan.Zero && wait <= TimeSpan.FromSeconds(5))
                { response.Dispose(); await _delay(wait, token).ConfigureAwait(false); continue; }
            }
            response.Dispose();
            throw Failure(status switch
            {
                HttpStatusCode.Unauthorized => CloudRecoveryFailure.AuthenticationRequired,
                HttpStatusCode.Forbidden => CloudRecoveryFailure.PermissionUnavailable,
                HttpStatusCode.NotFound => appRoot ? CloudRecoveryFailure.OneDriveUnavailable : CloudRecoveryFailure.ObjectNotFound,
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => CloudRecoveryFailure.ObjectChanged,
                HttpStatusCode.TooManyRequests => CloudRecoveryFailure.Throttled,
                HttpStatusCode.InsufficientStorage => CloudRecoveryFailure.OneDriveUnavailable,
                _ => CloudRecoveryFailure.ServiceUnavailable
            });
        }
    }

    private async Task<byte[]> Download(HttpResponseMessage initial, CancellationToken token)
    {
        if (initial.IsSuccessStatusCode) return await ReadBounded(initial, ICloudRecoveryStore.MaximumContentBytes, token).ConfigureAwait(false);
        var location = initial.Headers.Location;
        for (int count = 0; count < 3; count++)
        {
            if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || !location.IsDefaultPort ||
                location.UserInfo.Length != 0 || location.Fragment.Length != 0 || location.IsLoopback || location.HostNameType != UriHostNameType.Dns)
                throw Failure(CloudRecoveryFailure.InvalidObject);
            using var request = new HttpRequestMessage(HttpMethod.Get, location); // Deliberately NO Authorization.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return await ReadBounded(response, ICloudRecoveryStore.MaximumContentBytes, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
            { location = response.Headers.Location; continue; }
            throw Failure(CloudRecoveryFailure.ServiceUnavailable);
        }
        throw Failure(CloudRecoveryFailure.InvalidObject);
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is { } size && (size < 0 || size > limit)) throw Failure(CloudRecoveryFailure.InvalidObject);
        // One fixed buffer, also for chunked responses without Content-Length. No unbounded MemoryStream.
        var buffer = new byte[limit + 1];
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            int length = 0, read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false)) != 0) length += read;
            if (length > limit) throw Failure(CloudRecoveryFailure.InvalidObject);
            return buffer.AsSpan(0, length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string Text(JsonElement parent, string name, int limit)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } text || text.Length > limit || text.Any(char.IsControl)) throw Failure(CloudRecoveryFailure.InvalidObject);
        return text;
    }
    private static void RequireId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 512 || id.Any(c => c < 33 || c > 126 || c is '/' or '\\' or '?' or '#'))
            throw Failure(CloudRecoveryFailure.InvalidObject);
    }
    private static void RequireTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 1024 || tag.Any(char.IsControl) ||
            !EntityTagHeaderValue.TryParse(tag, out var parsed) || parsed.Tag == "*") throw Failure(CloudRecoveryFailure.InvalidObject);
    }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static CloudRecoveryException Failure(CloudRecoveryFailure kind) => new(kind);
    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await operation(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Failure(CloudRecoveryFailure.NetworkUnavailable); }
        catch (HttpRequestException) { throw Failure(CloudRecoveryFailure.NetworkUnavailable); }
        catch (IOException error) when (error is not CloudRecoveryException) { throw Failure(CloudRecoveryFailure.NetworkUnavailable); }
    }
    public void Dispose() { _accessToken = null; _http.Dispose(); }
}
