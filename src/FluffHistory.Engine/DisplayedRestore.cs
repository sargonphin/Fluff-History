using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

public abstract record DisplayedRestoreItem;
public sealed record DisplayedRestoreFile(VersionIdentity Identity) : DisplayedRestoreItem;
public sealed record DisplayedRestoreFolder(string Name) : DisplayedRestoreItem;
public enum InteractiveRestoreDestination { AnotherFolder, OriginalLocation }
public sealed class RestoreStructureConflictException()
    : InvalidOperationException("These selected historical items contain a file and folder with the same path and cannot be restored together.");

/// <summary>Transient selection of immediate children in one displayed authority/scope/view.</summary>
public sealed record DisplayedRestoreRequest(AuthorityIdentity Authority, HistoryScope Scope, HistoryView View,
    IReadOnlyList<DisplayedRestoreItem> Items, InteractiveRestoreDestination DestinationMode,
    string? DestinationDirectory = null, SourceSelection? OriginalSource = null, nint OwnerWindow = 0);
public sealed record RestorePlanLimits(int MaximumFiles = 100_000, long MaximumChargedBytes = 128 * 1024 * 1024);
public sealed record PlannedRestoreFile(StoredVersion Version, string PayloadPath, string DestinationPath);

/// <summary>Owns one captured reader; disposing the plan releases it. No payload handles/content are retained.</summary>
public sealed class DisplayedRestorePlan : IDisposable
{
    internal RepositoryReader Reader { get; }
    internal Repository Repository { get; }
    public DisplayedRestoreRequest Request { get; }
    public IReadOnlyList<PlannedRestoreFile> Files { get; }
    public string DestinationDirectory { get; }
    public long PlannedBytes { get; }
    public long ChargedBytes { get; }
    internal DisplayedRestorePlan(Repository repository, RepositoryReader reader, DisplayedRestoreRequest request,
        string destination, List<PlannedRestoreFile> files, long bytes, long charged)
    { Repository = repository; Reader = reader; Request = request; DestinationDirectory = destination; Files = files.AsReadOnly(); PlannedBytes = bytes; ChargedBytes = charged; }
    public void Dispose() => Reader.Dispose();
}

public sealed class DisplayedRestorePlanner(Repository repository)
{
    public DisplayedRestorePlan Plan(DisplayedRestoreRequest request, RestorePlanLimits? limits = null, CancellationToken cancellationToken = default)
        => PlanCore(request, limits, cancellationToken, exactFile: false);

    /// <summary>Explicit single exact version, independent of the assembled view's timestamp tie-breaking.</summary>
    public DisplayedRestorePlan PlanExact(AuthorityIdentity authority, VersionIdentity identity, InteractiveRestoreDestination mode,
        string? destinationDirectory = null, SourceSelection? originalSource = null, nint ownerWindow = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity); var path = identity.Path.CanonicalPath; var slash = path.LastIndexOf('/');
        var request = new DisplayedRestoreRequest(authority, new(identity.Path.SourceRootId, slash < 0 ? "" : path[..slash]),
            new HistoryView(null), [new DisplayedRestoreFile(identity)], mode, destinationDirectory, originalSource, ownerWindow);
        return PlanCore(request, new(MaximumFiles: 1), cancellationToken, exactFile: true);
    }
    private DisplayedRestorePlan PlanCore(DisplayedRestoreRequest request, RestorePlanLimits? limits, CancellationToken cancellationToken, bool exactFile)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Scope); ArgumentNullException.ThrowIfNull(request.View);
        ArgumentNullException.ThrowIfNull(request.Items); limits ??= new();
        if (limits.MaximumFiles is < 1 or > 1_000_000 || limits.MaximumChargedBytes is < 1024 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (!Enum.IsDefined(request.DestinationMode) || request.Items.Count == 0 || request.Items.Count > limits.MaximumFiles)
            throw new ArgumentException("Select a bounded nonempty set of displayed children.", nameof(request));
        // Freeze mutable caller lists before planning; plan and callbacks never reread a caller-owned list.
        request = request with { Items = Array.AsReadOnly(request.Items.ToArray()) };
        var original = request.DestinationMode == InteractiveRestoreDestination.OriginalLocation;
        if (original ? request.DestinationDirectory is not null : request.OriginalSource is not null)
            throw new ArgumentException("Use only the destination mapping for the selected mode.");
        string destination;
        if (original)
        {
            var source = request.OriginalSource ?? throw new ArgumentException("An explicit runtime source mapping is required.");
            if (source.Configuration.SourceRootId != request.Scope.SourceRootId || !Path.IsPathFullyQualified(source.DirectoryPath))
                throw new ArgumentException("Source mapping does not match the displayed logical root.");
            destination = Path.Combine(source.DirectoryPath, request.Scope.Folder.Replace('/', Path.DirectorySeparatorChar));
        }
        else destination = request.DestinationDirectory ?? throw new ArgumentException("An explicit destination folder is required.");
        destination = RestoreDestination.Validate(destination, repository.DirectoryPath);
        RestoreDestination.CheckParents(destination, create: false);
        var reader = repository.OpenReader();
        try
        {
            if (reader.Authority != request.Authority) throw new ArgumentException("Displayed authority is stale; refresh explicitly.");
            var files = new Dictionary<HistoryPath, PlannedRestoreFile>(); var payloads = new HashSet<Guid>();
            long bytes = 0, charged = request.Items.Count * 64L;
            var prefix = request.Scope.Folder.Length == 0 ? "" : request.Scope.Folder + "/";
            var skipComponents = request.Scope.Folder.Length == 0 ? 0 : request.Scope.Folder.Count(c => c == '/') + 1;
            foreach (var item in request.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item)
                {
                    case DisplayedRestoreFile file:
                        ArgumentNullException.ThrowIfNull(file.Identity); var path = file.Identity.Path; ArgumentNullException.ThrowIfNull(path);
                        if (path.SourceRootId != request.Scope.SourceRootId || !path.CanonicalPath.StartsWith(prefix, StringComparison.Ordinal) ||
                            path.CanonicalPath[prefix.Length..].Contains('/')) throw new ArgumentException("File is not an immediate displayed child.");
                        var selected = exactFile ? reader.GetVersion(file.Identity) : SelectFile(path);
                        if (selected?.Identity != file.Identity) throw new ArgumentException("File identity does not match the displayed view.");
                        Add(selected); break;
                    case DisplayedRestoreFolder folder:
                        var child = new HistoryPath(request.Scope.SourceRootId, folder.Name);
                        if (child.CanonicalPath.Contains('/')) throw new ArgumentException("Folder is not an immediate displayed child.");
                        var childScope = new HistoryScope(request.Scope.SourceRootId, prefix + child.CanonicalPath);
                        HistoryCursor? cursor = null; var found = false;
                        do
                        {
                            var page = reader.GetScopeHistoryPage(childScope, request.View, 128, cursor, cancellationToken); cursor = page.Continuation;
                            foreach (var entry in page.Items) { found = true; Add(entry.Version); }
                        } while (cursor is not null);
                        if (!found) throw new ArgumentException("Selected folder has no descendants in the displayed view.");
                        break;
                    default: throw new ArgumentException("Unknown or null displayed selection item.");
                }
            }
            // Check ONLY the expanded selected materialization, not conflicts in unselected history.
            foreach (var path in files.Keys)
            {
                var parent = path.CanonicalPath;
                while (parent.LastIndexOf('/') is var slash && slash >= 0)
                {
                    parent = parent[..slash];
                    if (files.ContainsKey(new(request.Scope.SourceRootId, parent)))
                        throw new RestoreStructureConflictException();
                }
            }
            foreach (var file in files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestoreDestination.CheckParents(Path.GetDirectoryName(file.DestinationPath)!, create: false);
                _ = RestoreDestination.TargetExists(file.DestinationPath); // Ordinary files intentionally pass to Shell.
            }
            return new(repository, reader, request, destination, files.Values.OrderBy(f => f.Version.Identity.Path.CanonicalPath, StringComparer.Ordinal).ToList(), bytes, charged);

            void Add(StoredVersion version)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.TryGetValue(version.Identity.Path, out var prior))
                {
                    if (prior.Version.Identity != version.Identity) throw new InvalidDataException("Conflicting exact identities in displayed selection.");
                    return;
                }
                var relative = string.Join(Path.DirectorySeparatorChar, version.Metadata.RelativePath.Split('/').Skip(skipComponents));
                var target = RestoreDestination.Validate(Path.Combine(destination, relative), repository.DirectoryPath);
                var payload = repository.GetPayloadPath(version.Identity.VersionId);
                var charge = 512L + 2L * (relative.Length + target.Length + payload.Length + version.Metadata.RelativePath.Length);
                if (files.Count >= limits.MaximumFiles || charge > limits.MaximumChargedBytes - charged)
                    throw new InvalidOperationException("Selected restore plan exceeds its safety budget.");
                if (!payloads.Add(version.Identity.VersionId)) throw new InvalidDataException("Ambiguous payload UUID in selected versions.");
                charged += charge; bytes = checked(bytes + version.Metadata.Length);
                files.Add(version.Identity.Path, new(version, payload, target));
            }
            StoredVersion? SelectFile(HistoryPath path)
            {
                if (request.View.AtUtc is null) return reader.GetNewestVersion(path);
                HistoryCursor? cursor = null; StoredVersion? best = null;
                do
                {
                    var page = reader.GetPathHistoryPage(path, 128, HistoryDirection.OldestFirst, cursor, cancellationToken); cursor = page.Continuation;
                    foreach (var v in page.Items)
                        if (v.StoredUtc <= request.View.AtUtc && (best is null || v.StoredUtc > best.StoredUtc || v.StoredUtc == best.StoredUtc &&
                            (v.Identity.Sequence > best.Identity.Sequence || v.Identity.Sequence == best.Identity.Sequence && v.Identity.Ordinal > best.Identity.Ordinal))) best = v;
                } while (cursor is not null);
                return best;
            }
        }
        catch { reader.Dispose(); throw; }
    }
}
