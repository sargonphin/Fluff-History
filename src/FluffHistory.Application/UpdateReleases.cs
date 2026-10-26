using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluffHistory.Application;

public enum UpdateChannel { Stable, Beta }
public enum UpdateFrequency { Never, Daily, Weekly, Monthly }
public enum UpdateFailure { None, Offline, Unavailable, RateLimited, InvalidResponse, NoValidRelease, Download,
    Integrity, Signature, SignerNotApproved, WrongProduct, Busy, BackupStillRunning, Installation, Relaunch, DevelopmentBuild }
public sealed record UpdateRelease(long ReleaseId, string Tag, string Name, bool Prerelease, string PageUrl,
    DateTimeOffset PublishedUtc, long AssetId, long Size, string DownloadUrl, string? Sha256)
{
    public const string InstallerName = "Fluff-History-Setup.exe";
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;
    [JsonIgnore] public ReleaseVersion Version => ReleaseVersion.Parse(Tag);
    [JsonIgnore] public bool CanDownload => IsDigest(Sha256);
    public static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public void Validate()
    {
        if (Tag is null || Name is null || PageUrl is null || DownloadUrl is null || ReleaseId <= 0 || AssetId <= 0 || Tag.Length > 64 || !ReleaseVersion.TryParse(Tag, out var version) ||
            version.IsBeta != Prerelease || Name.Length is < 1 or > 256 || Name.Any(char.IsControl) || Size is < 1 or > MaximumInstallerBytes ||
            PublishedUtc.Offset != TimeSpan.Zero || !ValidUrl(PageUrl, $"https://github.com/sargonphin/Fluff-History/releases/tag/{Tag}") ||
            !ValidUrl(DownloadUrl, $"https://github.com/sargonphin/Fluff-History/releases/download/{Tag}/{InstallerName}") ||
            Sha256 is not null && !IsDigest(Sha256)) throw new InvalidDataException("Invalid update release.");
    }
    private static bool ValidUrl(string value, string expected) => value.Length <= 2048 &&
        string.Equals(value, expected, StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}

public sealed record ReleaseCheck(ImmutableArray<UpdateRelease> Releases, string? ETag = null, UpdateFailure Failure = UpdateFailure.None);
public interface IUpdateReleaseSource
{
    Task<ReleaseCheck> CheckAsync(string? etag, ImmutableArray<UpdateRelease> cached, CancellationToken token);
}
public static class UpdateReleaseSelection
{
    public static UpdateRelease? Select(IEnumerable<UpdateRelease> releases, UpdateChannel channel, ReleaseVersion? current)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        UpdateRelease? highest = null; var seen = new HashSet<ReleaseVersion>();
        foreach (var release in releases)
        {
            release.Validate();
            if (channel == UpdateChannel.Stable && release.Prerelease) continue;
            if (!seen.Add(release.Version)) throw new InvalidDataException("Ambiguous equal release versions.");
            if (highest is null || release.Version > highest.Version) highest = release;
        }
        return highest is not null && (current is null || highest.Version > current.Value) ? highest : null;
    }
}

/// <summary>Anonymous fixed GitHub endpoint. The injected client must not follow unchecked redirects.</summary>
public sealed class GitHubReleaseSource(HttpClient http, ReleaseVersion? current) : IUpdateReleaseSource
{
    public const int MaximumJsonBytes = 2 * 1024 * 1024;
    public const int MaximumReleases = 100;
    public const int MaximumAssets = 32;
    public static readonly Uri Endpoint = new("https://api.github.com/repos/sargonphin/Fluff-History/releases?per_page=100");
    public async Task<ReleaseCheck> CheckAsync(string? etag, ImmutableArray<UpdateRelease> cached, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.UserAgent.ParseAdd("Fluff-History/" + (current?.ToString() ?? "development"));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            if (ValidETag(etag) && !cached.IsDefault) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag!));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached.IsDefault || !ValidETag(etag)) return new([], Failure: UpdateFailure.InvalidResponse);
                foreach (var release in cached) release.Validate();
                return new(cached, etag);
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) return new([], Failure: UpdateFailure.RateLimited);
            if (response.StatusCode != HttpStatusCode.OK) return new([], Failure: UpdateFailure.Unavailable);
            if (response.Content.Headers.ContentLength > MaximumJsonBytes) return new([], Failure: UpdateFailure.InvalidResponse);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[16 * 1024];
            while (true)
            {
                var read = await body.ReadAsync(bytes, timeout.Token).ConfigureAwait(false); if (read == 0) break;
                if (buffer.Length + read > MaximumJsonBytes) return new([], Failure: UpdateFailure.InvalidResponse);
                buffer.Write(bytes, 0, read);
            }
            var releases = Parse(buffer.ToArray()); var newTag = response.Headers.ETag?.ToString();
            return new(releases, ValidETag(newTag) ? newTag : null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new([], Failure: UpdateFailure.Offline); }
        catch (HttpRequestException) { return new([], Failure: UpdateFailure.Offline); }
        catch (IOException) { return new([], Failure: UpdateFailure.Offline); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        { return new([], Failure: UpdateFailure.InvalidResponse); }
    }
    public static bool ValidETag(string? etag) => etag is { Length: <= 256 } && EntityTagHeaderValue.TryParse(etag, out var parsed) && parsed.Tag != "*";
    public static ImmutableArray<UpdateRelease> Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("Release response too large.");
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 24 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaximumReleases) throw new InvalidDataException("Too many releases.");
        Unique(root);
        var result = ImmutableArray.CreateBuilder<UpdateRelease>();
        foreach (var value in root.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) continue;
            if (value.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array && assets.GetArrayLength() > MaximumAssets)
                throw new InvalidDataException("Too many release assets.");
            try
            {
                var tag = Text(value, "tag_name", 128);
                if (value.GetProperty("draft").GetBoolean() || !ReleaseVersion.TryParse(tag, out var version) ||
                    value.GetProperty("prerelease").GetBoolean() != version.IsBeta || assets.ValueKind != JsonValueKind.Array) continue;
                var candidates = assets.EnumerateArray().Where(a => Text(a, "name", 256) == UpdateRelease.InstallerName).ToArray();
                if (candidates.Length != 1) continue;
                var asset = candidates[0];
                if (Text(asset, "state", 32) != "uploaded") continue;
                string? digest = null;
                if (asset.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String)
                {
                    var text = hash.GetString()!;
                    if (text.Length == 71 && text.StartsWith("sha256:", StringComparison.Ordinal) && UpdateRelease.IsDigest(text[7..])) digest = text[7..];
                }
                var release = new UpdateRelease(value.GetProperty("id").GetInt64(), tag, Text(value, "name", 256), version.IsBeta,
                    Text(value, "html_url", 2048), value.GetProperty("published_at").GetDateTimeOffset().ToUniversalTime(),
                    asset.GetProperty("id").GetInt64(), asset.GetProperty("size").GetInt64(), Text(asset, "browser_download_url", 2048), digest);
                release.Validate(); result.Add(release);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { /* A malformed individual release cannot authorize execution or hide other valid releases. */ }
        }
        return result.ToImmutable();
    }
    private static string Text(JsonElement value, string name, int bound)
    {
        var result = value.GetProperty(name).GetString();
        return result is not null && result.Length <= bound ? result : throw new InvalidDataException("Release string exceeds bound.");
    }
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate update field."); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
}
