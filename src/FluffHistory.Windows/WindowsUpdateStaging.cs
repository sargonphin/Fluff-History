using System.Net;
using System.Security.Cryptography;
using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>Fixed owned layout. File/directory handles retain path ownership through validation/launch.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsUpdateStaging(HttpClient http, IUpdateFileVerifier verifier)
{
    internal string Root { get; init; } = Path.Combine(WindowsProductPaths.Root, "Updates");
    public string GetDirectory(UpdateRelease release) => Path.Combine(Root, release.Version.ToString());
    public string GetPath(UpdateRelease release) => Path.Combine(GetDirectory(release), UpdateRelease.InstallerName);
    public async Task<StagedUpdate> StageAsync(UpdateRelease release, IProgress<long> progress, CancellationToken token)
    {
        release.Validate(); if (!release.CanDownload) throw new UpdateException(UpdateFailure.Integrity);
        var directory = GetDirectory(release); WindowsOperationAdmission.CheckParents(directory);
        Directory.CreateDirectory(Root);
        using var stagingWriter = new WindowsOperationAdmission(Path.Combine(Root, "staging.lock")).TryAcquire();
        if (stagingWriter is null) throw new UpdateException(UpdateFailure.Busy);
        await CleanupOwnedAsync(release, token).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        using var parents = new WindowsUpdatePathLease(directory);
        var ownership = new UpdateJsonStore<UpdateRelease?>(Path.Combine(directory, "ownership-v1.json"), null,
            r => (r ?? throw new InvalidDataException()).Validate(), 8192);
        var owner = await ownership.LoadAsync(token).ConfigureAwait(false);
        if (owner.State == SettingsLoadState.Missing)
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new UpdateException(UpdateFailure.Integrity);
            await ownership.SaveAsync(release, token).ConfigureAwait(false);
        }
        else if (owner.State != SettingsLoadState.Loaded || owner.Value != release) throw new UpdateException(UpdateFailure.Integrity);
        var target = GetPath(release);
        if (File.Exists(target))
        {
            using var existing = OpenVerified(new(release, target)); progress.Report(release.Size); return new(release, target);
        }
        var candidate = Path.Combine(directory, "candidate.tmp");
        WindowsOperationAdmission.CheckParents(candidate);
        // One exact owned uncommitted candidate can survive a crash; the root writer lease excludes live writers.
        if (File.Exists(candidate)) File.Delete(candidate);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var response = await DownloadAsync(new Uri(release.DownloadUrl), timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new UpdateException(UpdateFailure.Download);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; long total = 0, reported = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (read == 0) break;
                    total = checked(total + read);
                    if (total > release.Size || total > UpdateRelease.MaximumInstallerBytes) throw new UpdateException(UpdateFailure.Integrity);
                    hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    if (total == release.Size || total - reported >= 1024 * 1024) { progress.Report(total); reported = total; }
                }
                if (total != release.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException(UpdateFailure.Integrity);
                output.Flush(true);
            }
            using (var readback = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read)) verifier.Verify(candidate, readback, release);
            token.ThrowIfCancellationRequested(); File.Move(candidate, target); return new(release, target);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new UpdateException(UpdateFailure.Download); }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }
    private async Task CleanupOwnedAsync(UpdateRelease keep, CancellationToken token)
    {
        var entries = Directory.EnumerateDirectories(Root).Take(65).ToArray();
        if (entries.Length > 64) throw new UpdateException(UpdateFailure.Download);
        var owned = new List<string>();
        foreach (var directory in entries)
        {
            if (!ReleaseVersion.TryParse(Path.GetFileName(directory), out var version) || version.ToString() != Path.GetFileName(directory)) continue;
            WindowsOperationAdmission.CheckParents(directory);
            var marker = new UpdateJsonStore<UpdateRelease?>(Path.Combine(directory, "ownership-v1.json"), null,
                r => (r ?? throw new InvalidDataException()).Validate(), 8192);
            var state = await marker.LoadAsync(token).ConfigureAwait(false);
            if (state.State == SettingsLoadState.Loaded && state.Value!.Version == version) owned.Add(directory);
        }
        // Keep the selected release and at most two older entries. Unknown content is never swept.
        foreach (var directory in owned.Where(d => !string.Equals(d, GetDirectory(keep), StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray())
        {
            if (owned.Count < 3) break;
            try
            {
                using var pinned = new WindowsUpdatePathLease(directory);
                var files = Directory.EnumerateFileSystemEntries(directory).Take(8).ToArray();
                var allowed = new[] { "ownership-v1.json", "candidate.tmp", UpdateRelease.InstallerName, "FluffHistory.InstallSupport.exe", "handoff-v1.json" };
                if (files.Length > allowed.Length || files.Any(p => Directory.Exists(p) || !allowed.Contains(Path.GetFileName(p), StringComparer.Ordinal))) continue;
                var retained = new List<FileStream>();
                try
                {
                    foreach (var file in files)
                    {
                        WindowsOperationAdmission.CheckParents(file);
                        retained.Add(new(file, FileMode.Open, FileAccess.Read, FileShare.Delete)); // reject any active reader/helper/installer
                    }
                    foreach (var file in files.Where(p => Path.GetFileName(p) != "ownership-v1.json")) File.Delete(file);
                    File.Delete(Path.Combine(directory, "ownership-v1.json"));
                }
                finally { foreach (var file in retained) file.Dispose(); }
                pinned.Dispose(); Directory.Delete(directory, false); owned.Remove(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retain live/unknown entry and refuse unbounded growth. */ }
        }
        if (!owned.Contains(GetDirectory(keep), StringComparer.OrdinalIgnoreCase) && owned.Count >= 3) throw new UpdateException(UpdateFailure.Busy);
    }
    /// <summary>The returned lease denies writing/deleting both exact installer and every parent until disposed.</summary>
    public IDisposable OpenVerified(StagedUpdate update)
    {
        update.Release.Validate();
        if (!string.Equals(Path.GetFullPath(update.Path), Path.GetFullPath(GetPath(update.Release)), StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateFailure.Integrity);
        var parents = new WindowsUpdatePathLease(GetDirectory(update.Release));
        FileStream? file = null;
        try
        {
            WindowsOperationAdmission.CheckParents(update.Path);
            file = new FileStream(update.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            verifier.Verify(update.Path, file, update.Release); return new RetainedFile(file, parents);
        }
        catch { file?.Dispose(); parents.Dispose(); throw; }
    }
    private sealed class RetainedFile(FileStream file, IDisposable parents) : IDisposable
    { public void Dispose() { file.Dispose(); parents.Dispose(); } }
    private async Task<HttpResponseMessage> DownloadAsync(Uri uri, CancellationToken token)
    {
        // Composition disables HttpClientHandler redirects. Every redirect is inspected before following.
        for (var count = 0; count <= 5; count++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) throw new UpdateException(UpdateFailure.Download);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("Fluff-History/updater");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (count == 5 || location is null) throw new UpdateException(UpdateFailure.Download);
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                // GitHub release assets currently redirect to this owned HTTPS delivery domain.
                if (uri.Host != "github.com" && uri.Host != "release-assets.githubusercontent.com" && uri.Host != "objects.githubusercontent.com")
                    throw new UpdateException(UpdateFailure.Download);
                continue;
            }
            return response;
        }
        throw new UpdateException(UpdateFailure.Download);
    }
}
