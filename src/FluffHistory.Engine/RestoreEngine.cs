using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

internal enum RestoreStage { TemporaryCreated, BeforeRead, BeforeMetadata, BeforeInstall, Installed }

/// <summary>Read-only repository consumption; exact versions install independently into an external destination.</summary>
public sealed class RestoreEngine
{
    private readonly Repository _repository;
    internal Action<RestoreStage, string>? Observe { get; set; }
    internal Func<Guid> TemporaryIdentity { get; set; } = Guid.NewGuid;
    internal Action<string, DateTime> SetLastWrite { get; set; } = File.SetLastWriteTimeUtc;
    internal Func<DateTimeOffset> RestoreTime { get; set; } = () => DateTimeOffset.UtcNow;
    public RestoreEngine(Repository repository) { ArgumentNullException.ThrowIfNull(repository); _repository = repository; }

    public RestoreResult RestoreFile(VersionIdentity identity, string destination, ExistingTargetPolicy policy = ExistingTargetPolicy.FailIfExists,
        CancellationToken cancellationToken = default) => Single(identity, destination, policy, false, cancellationToken);

    public RestoreResult RestoreFileInPlace(VersionIdentity identity, SourceSelection activeSource,
        ExistingTargetPolicy policy = ExistingTargetPolicy.FailIfExists, CancellationToken cancellationToken = default)
    {
        CheckMapping(identity.Path.SourceRootId, activeSource);
        using var reader = _repository.OpenReader();
        var selected = reader.GetVersion(identity) ?? throw new FileNotFoundException("Exact historical version does not survive.");
        return Single(identity, Materialize(activeSource.DirectoryPath, selected), policy, true, cancellationToken, reader);
    }

    public RestoreResult RestoreScope(HistoryScope scope, string destinationRoot, HistoryView? view = null, RestoreOptions? options = null,
        CancellationToken cancellationToken = default) => Folder(scope, destinationRoot, view, options, false, cancellationToken);

    public RestoreResult RestoreScopeInPlace(HistoryScope scope, SourceSelection activeSource, HistoryView? view = null,
        RestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckMapping(scope.SourceRootId, activeSource);
        return Folder(scope, activeSource.DirectoryPath, view, options, true, cancellationToken);
    }

    /// <summary>Materialize one bounded displayed plan using the same integrity/install pipeline as exact restore.
    /// Planning freezes authority and preflights intrinsic selection conflicts before any selected writes.</summary>
    public RestoreResult RestoreDisplayed(DisplayedRestoreRequest request, RestoreOptions? options = null,
        RestorePlanLimits? limits = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (!Enum.IsDefined(options.ExistingTarget) || !Enum.IsDefined(options.FailurePolicy)) throw new ArgumentException("Unknown restore policy.");
        using var plan = new DisplayedRestorePlanner(_repository).Plan(request, limits, cancellationToken);
        var result = new RestoreResult { Authority = plan.Reader.Authority, Selected = plan.Files.Count, SelectionComplete = true };
        var buffer = new byte[128 * 1024];
        try
        {
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RestoreOne(plan.Reader, file.Version.Identity, file.DestinationPath, options.ExistingTarget,
                    request.DestinationMode == InteractiveRestoreDestination.OriginalLocation, result, buffer, cancellationToken) &&
                    options.FailurePolicy == RestoreFailurePolicy.StopOnFailure) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { result.Cancelled = true; }
        result.Cancelled |= cancellationToken.IsCancellationRequested; return result;
    }

    private RestoreResult Single(VersionIdentity identity, string destination, ExistingTargetPolicy policy, bool inPlace, CancellationToken token,
        RepositoryReader? captured = null)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var target = RestoreDestination.Validate(destination, _repository.DirectoryPath);
        using var ownedReader = captured is null ? _repository.OpenReader() : null; var reader = captured ?? ownedReader!;
        var result = new RestoreResult { Authority = reader.Authority, Selected = 1, SelectionComplete = true };
        var buffer = new byte[128 * 1024];
        try { RestoreOne(reader, identity, target, policy, inPlace, result, buffer, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result.Cancelled = true; }
        result.Cancelled |= token.IsCancellationRequested; return result;
    }

    private RestoreResult Folder(HistoryScope scope, string destinationRoot, HistoryView? view, RestoreOptions? options, bool inPlace, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(scope); options ??= new(); view ??= new();
        if (!Enum.IsDefined(options.ExistingTarget) || !Enum.IsDefined(options.FailurePolicy)) throw new ArgumentException("Unknown restore policy.", nameof(options));
        var root = RestoreDestination.Validate(destinationRoot, _repository.DirectoryPath);
        using var reader = _repository.OpenReader(); var result = new RestoreResult { Authority = reader.Authority };
        var buffer = new byte[128 * 1024];
        try
        {
            // Two streaming passes against one capture: structural proof before writes, no full selection list.
            foreach (var item in Selection())
            {
                token.ThrowIfCancellationRequested(); result.Selected++;
                if (item.HasConflict)
                {
                    result.SelectionConflicts++;
                    result.Problem(item.Version.Identity, root, "Historical selection contains a file/directory collision.");
                }
            }
            result.SelectionComplete = true;
            if (result.SelectionConflicts != 0) { result.Cancelled = token.IsCancellationRequested; return result; }
            RestoreDestination.CheckParents(root, create: false);
            foreach (var item in Selection())
            {
                token.ThrowIfCancellationRequested();
                var success = RestoreOne(reader, item.Version.Identity, Materialize(root, item.Version), options.ExistingTarget, inPlace, result, buffer, token);
                if (!success && options.FailurePolicy == RestoreFailurePolicy.StopOnFailure) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result.Cancelled = true; }
        result.Cancelled |= token.IsCancellationRequested; return result;

        IEnumerable<ScopeHistoryItem> Selection()
        {
            HistoryCursor? cursor = null;
            do
            {
                var page = reader.GetScopeHistoryPage(scope, view, 128, cursor, token); cursor = page.Continuation;
                foreach (var item in page.Items) yield return item;
            } while (cursor is not null);
        }
    }

    private bool RestoreOne(RepositoryReader reader, VersionIdentity identity, string target, ExistingTargetPolicy policy,
        bool inPlace, RestoreResult result, byte[] buffer, CancellationToken token)
    {
        string? temporary = null; var ownsTemporary = false;
        try
        {
            token.ThrowIfCancellationRequested(); var parent = Path.GetDirectoryName(target)!;
            RestoreDestination.CheckParents(parent, create: false);
            if (RestoreDestination.TargetExists(target))
            {
                if (policy == ExistingTargetPolicy.SkipExisting) { result.Skipped++; return true; }
                if (policy == ExistingTargetPolicy.FailIfExists) throw new IOException("Restore target already exists.");
            }
            using var payload = reader.OpenVersionPayload(identity) ?? throw new FileNotFoundException("Exact historical version does not survive.");
            var metadata = payload.Version.Metadata;
            RestoreDestination.CheckParents(parent, create: true);
            temporary = Path.Combine(parent, $".fluff-restore-{TemporaryIdentity():N}.tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                ownsTemporary = true; Observe?.Invoke(RestoreStage.TemporaryCreated, temporary);
                var hash = new XxHash64(); long bytes = 0;
                while (true)
                {
                    Observe?.Invoke(RestoreStage.BeforeRead, temporary); token.ThrowIfCancellationRequested();
                    var count = payload.Content.Read(buffer, 0, (int)Math.Min(buffer.Length, metadata.Length - bytes + (bytes == metadata.Length ? 1 : 0)));
                    result.BytesRead += count; if (count == 0) break;
                    bytes += count; if (bytes > metadata.Length) throw new InvalidDataException("Payload grew beyond recorded length.");
                    hash.Append(buffer.AsSpan(0, count)); output.Write(buffer.AsSpan(0, count)); result.BytesWritten += count;
                }
                if (bytes != metadata.Length) throw new InvalidDataException("Payload truncated during restore.");
                if (Convert.ToHexString(hash.GetCurrentHash()) != payload.Version.Checksum.Value)
                { result.ChecksumFailures++; throw new RestoreChecksumException(); }
                result.ChecksumsVerified++; output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); Observe?.Invoke(RestoreStage.BeforeMetadata, temporary);
            if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(temporary, metadata.CreationTimeUtc.UtcDateTime);
            var baseline = inPlace ? reader.GetCurrent(identity.Path) ?? reader.GetNewestVersion(identity.Path) : null;
            var adjusted = RestoreTimestamps.Apply(temporary, metadata.LastWriteTimeUtc, baseline?.Metadata.LastWriteTimeUtc,
                inPlace ? RestoreTime() : null, SetLastWrite);
            File.SetAttributes(temporary, metadata.Attributes);
            Observe?.Invoke(RestoreStage.BeforeInstall, temporary); token.ThrowIfCancellationRequested();
            _ = RestoreDestination.Validate(target, _repository.DirectoryPath);
            RestoreDestination.CheckParents(parent, create: false);
            var exists = RestoreDestination.TargetExists(target);
            if (exists && policy == ExistingTargetPolicy.SkipExisting) { result.Skipped++; return true; }
            if (exists && policy == ExistingTargetPolicy.FailIfExists) throw new IOException("Restore target appeared before installation.");
            RestoreDestination.Install(temporary, target, policy == ExistingTargetPolicy.OverwriteFile);
            ownsTemporary = false; result.Restored++; if (adjusted) result.MtimeAdjustedForChangeDetection++;
            Observe?.Invoke(RestoreStage.Installed, target); return true;
        }
        catch (Exception e) when (e is not ReaderGenerationExpiredException && e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        { result.Failed++; result.Problem(identity, target, e.Message); return false; }
        finally
        {
            if (ownsTemporary)
            {
                try
                {
                    RestoreDestination.CheckParents(Path.GetDirectoryName(temporary!)!, create: false);
                    var attributes = RestoreDestination.Attributes(temporary!);
                    if (attributes is not null && (attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
                    { File.SetAttributes(temporary!, FileAttributes.Normal); File.Delete(temporary!); }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static void CheckMapping(Guid root, SourceSelection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Configuration.SourceRootId != root || !Path.IsPathFullyQualified(source.DirectoryPath))
            throw new ArgumentException("Explicit matching logical root and absolute runtime source location are required.", nameof(source));
    }
    private static string Materialize(string root, StoredVersion version) => Path.Combine(root, version.Metadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
