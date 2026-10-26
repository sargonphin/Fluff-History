using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluffHistory.Application;
using FluffHistory.Windows;
using Xunit;

namespace FluffHistory.Tests;

public sealed class GraphAppFolderTests
{
    [Theory]
    [InlineData("Files.ReadWrite.AppFolder", true)]
    [InlineData("https://graph.microsoft.com/Files.ReadWrite.AppFolder", true)]
    [InlineData("Files.ReadWrite", false)] [InlineData("Files.ReadWrite.All", false)]
    [InlineData("User.Read", false)] [InlineData("Sites.Read.All", false)]
    public void GrantedScopesCannotSilentlyBroadenFolderAccess(string scope, bool allowed)
    {
        var scopes = new[] { "openid", "profile", "offline_access", scope };
        if (allowed) OneDriveRecoveryPermissions.ValidateGrantedScopes(scopes);
        else Assert.Equal(CloudRecoveryFailure.PermissionUnavailable,
            Assert.Throws<CloudRecoveryException>(() => OneDriveRecoveryPermissions.ValidateGrantedScopes(scopes)).Failure);
    }

    private const string SyntheticToken = "synthetic-token-not-a-credential";
    private const string RootJson = "{\"id\":\"app-root\",\"folder\":{},\"specialFolder\":{\"name\":\"approot\"}}";
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Item(string name = "renamed.json", string tag = "\"generation-1\"", string id = "item!1") =>
        Json(JsonSerializer.Serialize(new { id, eTag = tag, name, file = new { } }));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<(string Method, string Uri, string? Authorization, string? IfMatch)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.IfMatch.SingleOrDefault()?.ToString()));
            cancellationToken.ThrowIfCancellationRequested(); return send(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("approot", true)]
    [InlineData("appRoot", true)]
    [InlineData("AppRoot", true)]
    [InlineData("documents", false)]
    [InlineData("approot/children", false)]
    [InlineData(" approot", false)]
    public async Task AppFolderResponseAliasToleratesServiceCasingWithoutChangingRequestScope(string alias, bool accepted)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(
            new { id = "app-root", folder = new { }, specialFolder = new { name = alias } }))));
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        if (accepted) await store.InspectAsync(default);
        else Assert.Equal(CloudRecoveryFailure.InvalidObject,
            (await Assert.ThrowsAsync<CloudRecoveryException>(() => store.InspectAsync(default))).Failure);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/special/approot", call.Uri);
    }

    [Fact]
    public async Task CreateUsesOnlyAppRootUniqueSafeNameAndFailOnConflictWithoutWriteRetry()
    {
        var repo = Guid.NewGuid(); var generation = Guid.NewGuid();
        var name = $"recovery-{repo:N}-{generation:N}.json";
        var input = "{\"synthetic\":true}"u8.ToArray();
        var handler = new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Json(RootJson);
            Assert.Equal(input, await request.Content!.ReadAsByteArrayAsync(token)); return Item(name);
        });
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        using var created = await store.CreateAsync(repo, generation, input, default);
        Assert.Equal("item!1", created.ItemId); Assert.Equal("\"generation-1\"", created.ETag);
        Assert.Equal(2, handler.Calls.Count);
        Assert.EndsWith("/special/approot", handler.Calls[0].Uri);
        Assert.EndsWith($"/items/app-root:/{name}:/content?@microsoft.graph.conflictBehavior=fail", handler.Calls[1].Uri);
        Assert.All(handler.Calls, call => Assert.Equal("Bearer " + SyntheticToken, call.Authorization));
    }

    [Fact]
    public async Task ExactReadSurvivesRenameAndDownloadRedirectHasNoBearerOrRootTraversal()
    {
        var bytes = "synthetic recovery content"u8.ToArray();
        var handler = new Handler((request, _) => Task.FromResult(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath) switch
        {
            "/v1.0/me/drive/special/approot" => Json(RootJson),
            "/v1.0/me/drive/items/item!1" => Item(),
            "/v1.0/me/drive/items/item!1/content" => new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://synthetic-download.example/content?temporary=synthetic") } },
            "/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            _ => throw new InvalidOperationException("Unexpected broad access")
        }));
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        using var item = await store.ReadExactAsync("item!1", default);
        Assert.Equal(bytes, item.Content.ToArray()); Assert.Equal("renamed.json", item.Name);
        Assert.Equal(5, handler.Calls.Count);
        Assert.Null(handler.Calls.Single(c => c.Uri.StartsWith("https://synthetic-download", StringComparison.Ordinal)).Authorization);
        Assert.Equal(2, handler.Calls.Count(c => c.Uri.Contains("?$select=id,name,eTag,file", StringComparison.Ordinal)));
        var owned = item.Content; item.Dispose(); Assert.All(owned.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => item.Content);
    }

    [Theory]
    [InlineData(401, CloudRecoveryFailure.AuthenticationRequired)]
    [InlineData(403, CloudRecoveryFailure.PermissionUnavailable)]
    [InlineData(404, CloudRecoveryFailure.OneDriveUnavailable)]
    [InlineData(429, CloudRecoveryFailure.Throttled)]
    [InlineData(500, CloudRecoveryFailure.ServiceUnavailable)]
    [InlineData(503, CloudRecoveryFailure.ServiceUnavailable)]
    [InlineData(507, CloudRecoveryFailure.OneDriveUnavailable)]
    public async Task RootErrorsAreTypedBoundedAndNeverEscalate(int status, CloudRecoveryFailure expected)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var store = new GraphAppFolderStore(handler, SyntheticToken, (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<CloudRecoveryException>(() => store.ReadExactAsync("item!1", default));
        Assert.Equal(expected, error.Failure); Assert.InRange(handler.Calls.Count, 1, 3);
        Assert.All(handler.Calls, call => Assert.EndsWith("/special/approot", call.Uri));
        Assert.DoesNotContain(SyntheticToken, error.ToString());
    }

    [Fact]
    public async Task RetryAfterIsRespectedAndLongDelaysReturnWithoutEarlyRetry()
    {
        int count = 0; var waits = new List<TimeSpan>();
        var handler = new Handler((_, _) => Task.FromResult(++count switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)) } },
            2 => Json(RootJson),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1)) } }
        }));
        using var store = new GraphAppFolderStore(handler, SyntheticToken, (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<CloudRecoveryException>(() => store.ReadExactAsync("item!1", default));
        Assert.Equal(CloudRecoveryFailure.Throttled, error.Failure);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, waits); Assert.Equal(3, handler.Calls.Count);
    }

    [Theory]
    [InlineData(204, CloudRecoveryDeleteResult.Deleted)]
    [InlineData(404, CloudRecoveryDeleteResult.AlreadyAbsent)]
    public async Task ConditionalDeleteOwnsOnlyExactItem(int status, CloudRecoveryDeleteResult expected)
    {
        var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Json(RootJson) : new HttpResponseMessage((HttpStatusCode)status)));
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        Assert.Equal(expected, await store.DeleteExactAsync("item!1", "\"generation-1\"", default));
        var delete = handler.Calls.Single(c => c.Method == "DELETE");
        Assert.EndsWith("/items/item%211", delete.Uri.Replace("!", "%21", StringComparison.Ordinal));
        Assert.Equal("\"generation-1\"", delete.IfMatch); Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(412, CloudRecoveryFailure.ObjectChanged)]
    [InlineData(403, CloudRecoveryFailure.PermissionUnavailable)]
    [InlineData(503, CloudRecoveryFailure.ServiceUnavailable)]
    public async Task DeleteFailureNeverRetriesOrWeakensConditionalOwnership(int status, CloudRecoveryFailure expected)
    {
        var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Json(RootJson) : new HttpResponseMessage((HttpStatusCode)status)));
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        var error = await Assert.ThrowsAsync<CloudRecoveryException>(() => store.DeleteExactAsync("item!1", "\"generation-1\"", default));
        Assert.Equal(expected, error.Failure); Assert.Single(handler.Calls, c => c.Method == "DELETE");
    }

    [Theory]
    [InlineData("http://download.example/content")]
    [InlineData("https://localhost/content")]
    [InlineData("https://127.0.0.1/content")]
    [InlineData("https://user@download.example/content")]
    [InlineData("https://download.example:8443/content")]
    public async Task UnsafeDownloadRedirectsAreRejectedBeforeRequest(string location)
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("approot", StringComparison.Ordinal)
            ? Json(RootJson) : request.RequestUri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } } : Item()));
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        var error = await Assert.ThrowsAsync<CloudRecoveryException>(() => store.ReadExactAsync("item!1", default));
        Assert.Equal(CloudRecoveryFailure.InvalidObject, error.Failure); Assert.Equal(3, handler.Calls.Count);
    }

    [Theory]
    [InlineData("root-json")][InlineData("root-oversize")][InlineData("metadata-json")]
    [InlineData("metadata-oversize")][InlineData("content-oversize")][InlineData("content-chunked-oversize")]
    [InlineData("changed-etag")][InlineData("wrong-item")][InlineData("missing-item")]
    public async Task UntrustedResponsesCannotEscapeBoundsOrExactIdentity(string fault)
    {
        int metadataReads = 0;
        var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("approot", StringComparison.Ordinal)) return Task.FromResult(Json(fault switch
            { "root-json" => "{}", "root-oversize" => new string('x', 32769), _ => RootJson }));
            if (path.EndsWith("/content", StringComparison.Ordinal))
            {
                var body = new ByteArrayContent(fault.StartsWith("content-", StringComparison.Ordinal) ? new byte[16385] : "synthetic"u8.ToArray());
                if (fault == "content-chunked-oversize") body.Headers.ContentLength = null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
            }
            metadataReads++;
            return Task.FromResult(fault switch
            {
                "metadata-json" => Json("{\"id\":7}"), "metadata-oversize" => Json(new string('x', 32769)),
                "missing-item" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "wrong-item" => Item(id: "item!2"),
                "changed-etag" when metadataReads == 2 => Item(tag: "\"different\""),
                _ => Item()
            });
        });
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        var error = await Assert.ThrowsAsync<CloudRecoveryException>(() => store.ReadExactAsync("item!1", default));
        Assert.Equal(fault == "missing-item" ? CloudRecoveryFailure.ObjectNotFound :
            fault is "wrong-item" or "changed-etag" ? CloudRecoveryFailure.ObjectChanged : CloudRecoveryFailure.InvalidObject, error.Failure);
        Assert.DoesNotContain(handler.Calls, call => call.Method is "PUT" or "DELETE");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NetworkTimeoutAndCallerCancellationAreDistinct(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((_, _) => { if (cancelled) cancellation.Cancel(); throw new OperationCanceledException(); });
        using var store = new GraphAppFolderStore(handler, SyntheticToken);
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadExactAsync("item!1", cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<CloudRecoveryException>(() => store.ReadExactAsync("item!1", default));
            Assert.Equal(CloudRecoveryFailure.NetworkUnavailable, error.Failure);
        }
    }
}
