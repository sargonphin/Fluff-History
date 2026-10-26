using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using FluffHistory.Application;
using Xunit;

namespace FluffHistory.Tests;

public sealed class UpdateReleaseTests
{
    [Theory]
    [InlineData("0.0.0")][InlineData("v1.10.0")][InlineData("1.11.0-beta.0")]
    [InlineData("v1.11.0-beta.2")][InlineData("2147483647.2147483647.2147483647-beta.2147483647")]
    public void ReleaseGrammarRoundTrips(string text)
    {
        var v = ReleaseVersion.Parse(text);
        Assert.Equal(text.TrimStart('v'), v.ToString()); Assert.Equal(v, ReleaseVersion.Parse(v.ToString()));
    }
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("1")][InlineData("v1")][InlineData("1.2")]
    [InlineData("1.2.x")][InlineData("1.2.3.4.5")][InlineData("1.02.3")][InlineData("01.2.3")]
    [InlineData("1.2.03")][InlineData("1.2.3-beta")][InlineData("1.2.3-beta.foo")][InlineData("1.2.3-beta.-1")]
    [InlineData("1.2.3-beta.01")][InlineData("1.2.3 garbage")][InlineData(" 1.2.3")][InlineData("1.2.3\n")]
    [InlineData("V1.2.3")][InlineData("1.2.3-Beta.1")][InlineData("1.2.3-rc.1")][InlineData("1.2.3+build")]
    [InlineData("2147483648.1.1")][InlineData("1.1.1-beta.2147483648")][InlineData("-1.1.1")]
    [InlineData("١.٢.٣")]
    public void InvalidGrammarRejected(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));
    [Theory]
    [InlineData("1.10.0", "1.9.0")][InlineData("1.10.1", "1.10.0")][InlineData("2.0.0", "1.99.99")]
    [InlineData("1.11.0-beta.1", "1.10.0")][InlineData("1.11.0-beta.2", "1.11.0-beta.1")]
    [InlineData("1.11.0", "1.11.0-beta.999")]
    public void NumericSemanticOrdering(string newer, string older)
    {
        var n = ReleaseVersion.Parse(newer); var o = ReleaseVersion.Parse(older);
        Assert.True(n > o); Assert.True(o < n); Assert.True(n >= o); Assert.True(o <= n);
        Assert.Equal(n, ReleaseVersion.Parse(newer)); Assert.Equal(0, n.CompareTo(ReleaseVersion.Parse(newer)));
    }
    [Theory]
    [InlineData("0.24.1.227", true)][InlineData("1.10.0.0", true)][InlineData("2.0.0.248", true)]
    [InlineData("65535.65535.65535.65535", true)][InlineData("65536.0.0.0", false)]
    [InlineData("0.0.0.0", false)][InlineData("1.02.3.4", false)][InlineData("../1.2.3.4", false)]
    [InlineData("1.2.3", false)][InlineData("1.2.3.4.5", false)]
    public void ProductBuildGrammarIsSeparate(string text, bool valid) => Assert.Equal(valid, ProductBuildVersion.TryParse(text, out _));
    [Fact]
    public void SignedInnoIdentityAllowsOnlyTrailingResourcePaddingAndExactPublicReleaseBinding()
    {
        var release = ReleaseVersion.Parse("1.11.0-beta.2");
        Assert.True(InstallerProductIdentity.Matches("Fluff-History   ", "Fish & Dog Software\0 ", "Fluff-History Setup [1.11.0-beta.2]  ", "1.11.0.248   ", release));
        Assert.False(InstallerProductIdentity.Matches(" Fluff-History", "Fish & Dog Software", "Fluff-History Setup [1.11.0-beta.2]", "1.11.0.248", release));
        Assert.False(InstallerProductIdentity.Matches("Fluff-History", "Another publisher", "Fluff-History Setup [1.11.0-beta.2]", "1.11.0.248", release));
        Assert.False(InstallerProductIdentity.Matches("Fluff-History", "Fish & Dog Software", "Fluff-History Setup [1.11.0-beta.1]", "1.11.0.248", release));
        Assert.False(InstallerProductIdentity.Matches("Fluff-History", "Fish & Dog Software", "Fluff-History Setup [1.11.0-beta.2]", "1.10.0.248", release));
    }

    internal static object Release(string tag, bool beta = false, object[]? assets = null, bool draft = false) => new
    {
        id = 12, tag_name = tag, name = "Fluff-History " + tag, draft, prerelease = beta,
        html_url = "https://github.com/sargonphin/Fluff-History/releases/tag/" + tag,
        published_at = "2026-10-25T10:00:00Z", assets = assets ?? [Asset(tag)]
    };
    internal static object Asset(string tag, string? digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", long size = 100,
        string? url = null, string name = UpdateRelease.InstallerName, string state = "uploaded") => new
    {
        id = 44, name, state, size, digest,
        browser_download_url = url ?? "https://github.com/sargonphin/Fluff-History/releases/download/" + tag + "/" + UpdateRelease.InstallerName
    };
    internal static ImmutableArray<UpdateRelease> Parse(params object[] values) => GitHubReleaseSource.Parse(JsonSerializer.SerializeToUtf8Bytes(values));
    [Fact]
    public void HighestVersionWinsAndChannelsConvergeOnStable()
    {
        var values = new[] { Release("v1.9.0"), Release("v1.11.0-beta.1", true), Release("v1.10.0") };
        Assert.Equal("v1.10.0", UpdateReleaseSelection.Select(Parse(values), UpdateChannel.Stable, ReleaseVersion.Parse("1.0.0"))!.Tag);
        Assert.Equal("v1.11.0-beta.1", UpdateReleaseSelection.Select(Parse(values), UpdateChannel.Beta, null)!.Tag);
        foreach (var channel in Enum.GetValues<UpdateChannel>())
            Assert.Equal("v1.11.0", UpdateReleaseSelection.Select(Parse([.. values, Release("v1.11.0")]), channel, null)!.Tag);
        Assert.Equal("v1.12.0-beta.1", UpdateReleaseSelection.Select(Parse([.. values, Release("v1.11.0"), Release("v1.12.0-beta.1", true)]), UpdateChannel.Beta, null)!.Tag);
    }
    [Fact]
    public void MalformedDraftMismatchedOrAmbiguousAssetsNeverSelected()
    {
        var values = Parse(Release("v9.0.0", draft: true), Release("v9.0.0-beta.1"), Release("v9.0.0", true), Release("invalid"),
            Release("v8.0.0", assets: []), Release("v7.0.0", assets: [Asset("v7.0.0"), Asset("v7.0.0")]),
            Release("v6.0.0", assets: [Asset("v6.0.0", state: "new")]), Release("v1.0.0"));
        Assert.Single(values); Assert.Equal("v1.0.0", values[0].Tag);
        Assert.Throws<InvalidDataException>(() => UpdateReleaseSelection.Select(Parse(Release("v1.0.0"), Release("1.0.0")), UpdateChannel.Stable, null));
    }
    [Theory]
    [InlineData("1.0.0")][InlineData("1.1.0")][InlineData("1.12.0-beta.3")]
    public void EqualLowerOrChannelChangeNeverDowngrades(string current) =>
        Assert.Null(UpdateReleaseSelection.Select(Parse(Release("v1.0.0")), UpdateChannel.Stable, ReleaseVersion.Parse(current)));
    [Theory]
    [InlineData(null)][InlineData("sha256:bad")][InlineData("md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MissingOrMalformedDigestCanAnnounceButCannotDownload(string? digest)
    {
        var release = Assert.Single(Parse(Release("v1.0.0", assets: [Asset("v1.0.0", digest)])));
        Assert.False(release.CanDownload);
    }
    [Theory]
    [InlineData("http://github.com/sargonphin/Fluff-History/releases/download/v1.0.0/Fluff-History-Setup.exe")]
    [InlineData("https://github.com/attacker/Fluff-History/releases/download/v1.0.0/Fluff-History-Setup.exe")]
    [InlineData("file:///C:/Fluff-History-Setup.exe")][InlineData("https://localhost/installer.exe")]
    public void ForeignDownloadUrlRejected(string url) => Assert.Empty(Parse(Release("v1.0.0", assets: [Asset("v1.0.0", url: url)])));
    [Theory]
    [InlineData(-1)][InlineData(0)][InlineData(UpdateRelease.MaximumInstallerBytes + 1)]
    public void InvalidSizeRejected(long size) => Assert.Empty(Parse(Release("v1.0.0", assets: [Asset("v1.0.0", size: size)])));
    [Fact]
    public void ResponseBoundsAndDuplicateFieldsFailClosed()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Enumerable.Repeat(Release("v1.0.0"), 101).ToArray()));
        Assert.Throws<InvalidDataException>(() => Parse(Release("v1.0.0", assets: Enumerable.Repeat(Asset("v1.0.0"), 33).ToArray())));
        Assert.Throws<InvalidDataException>(() => GitHubReleaseSource.Parse(Encoding.UTF8.GetBytes("[{\"id\":1,\"id\":2}]")));
        Assert.Throws<InvalidDataException>(() => GitHubReleaseSource.Parse(new byte[GitHubReleaseSource.MaximumJsonBytes + 1]));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    [Theory]
    [InlineData(403, UpdateFailure.RateLimited)][InlineData(404, UpdateFailure.Unavailable)][InlineData(429, UpdateFailure.RateLimited)]
    [InlineData(500, UpdateFailure.Unavailable)][InlineData(503, UpdateFailure.Unavailable)]
    public async Task NonSuccessIsNonfatal(int status, UpdateFailure expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        Assert.Equal(expected, (await new GitHubReleaseSource(http, null).CheckAsync(null, default, default)).Failure);
    }
    [Fact]
    public async Task Anonymous200AndConditional304ReuseExactValidatedCache()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(GitHubReleaseSource.Endpoint, request.RequestUri); Assert.Null(request.Headers.Authorization);
            Assert.Contains("Fluff-History/1.9.0", request.Headers.UserAgent.ToString());
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
            var result = new HttpResponseMessage(calls++ == 0 ? HttpStatusCode.OK : HttpStatusCode.NotModified);
            if (calls == 1) { result.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[] { Release("v1.10.0") })); result.Headers.ETag = new("\"list1\""); }
            else Assert.Equal("\"list1\"", request.Headers.IfNoneMatch.Single().ToString());
            return Task.FromResult(result);
        }));
        var source = new GitHubReleaseSource(http, ReleaseVersion.Parse("1.9.0"));
        var first = await source.CheckAsync(null, default, default); var second = await source.CheckAsync(first.ETag, first.Releases, default);
        Assert.Equal(UpdateFailure.None, second.Failure); Assert.Equal(first, second); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task Uncached304OversizedBodyAndMalformedJsonAreRejected()
    {
        foreach (var response in new[] { new HttpResponseMessage(HttpStatusCode.NotModified),
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GitHubReleaseSource.MaximumJsonBytes + 1]) },
            new(HttpStatusCode.OK) { Content = new StringContent("oops") } })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
            Assert.Equal(UpdateFailure.InvalidResponse, (await new GitHubReleaseSource(http, null).CheckAsync(null, default, default)).Failure);
        }
    }
    [Fact]
    public async Task TransportTimeoutAndCallerCancellationRemainDistinct()
    {
        foreach (var error in new Exception[] { new HttpRequestException("DNS"), new TaskCanceledException() })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromException<HttpResponseMessage>(error)));
            Assert.Equal(UpdateFailure.Offline, (await new GitHubReleaseSource(http, null).CheckAsync(null, default, default)).Failure);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var caller = new HttpClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubReleaseSource(caller, null).CheckAsync(null, default, cancelled.Token));
    }
}
