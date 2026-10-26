using FluffHistory.Core;

namespace FluffHistory.Core;

public sealed record RepositoryIdentity
{
    public const int FormatVersion = 4;
    public Guid RepositoryId { get; }
    public DateTimeOffset CreatedUtc { get; }
    public RepositoryIdentity(Guid repositoryId, DateTimeOffset createdUtc)
    {
        ModelValidation.RequireIdentity(repositoryId, nameof(repositoryId));
        ModelValidation.RequireUtc(createdUtc, nameof(createdUtc));
        RepositoryId = repositoryId;
        CreatedUtc = createdUtc;
    }
}

/// <summary>Storage authority identity, not a backup run or user-facing history checkpoint.</summary>
public sealed record AuthorityIdentity(Guid RepositoryId, long Generation, long MutationSequence, Guid TransactionId);

public sealed record VersionIdentity(HistoryPath Path, long Sequence, int Ordinal, Guid VersionId);

/// <summary>The successful current baseline needed for size + last-write comparison, without restore metadata.</summary>
public sealed record CurrentFileComparison
{
    internal CurrentFileComparison(VersionIdentity identity, long length, DateTimeOffset lastWriteTimeUtc)
    { Identity = identity; Length = length; LastWriteTimeUtc = lastWriteTimeUtc; }
    public HistoryPath Path => Identity.Path;
    public VersionIdentity Identity { get; }
    public long Length { get; }
    public DateTimeOffset LastWriteTimeUtc { get; }
}

public sealed record StoredVersion
{
    internal StoredVersion(VersionIdentity identity, LogicalFileMetadata metadata, DateTimeOffset storedUtc, XxHash64Checksum checksum)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(checksum);
        ModelValidation.RequireIdentity(identity.VersionId, nameof(identity));
        ModelValidation.RequireUtc(storedUtc, nameof(storedUtc));
        if (identity.Sequence <= 0 || identity.Ordinal < 0 || identity.Ordinal >= MutationBatch.MaximumChanges ||
            identity.Path != new HistoryPath(metadata.SourceRootId, metadata.RelativePath))
            throw new ArgumentException("Invalid v4 version identity/metadata relationship.", nameof(identity));
        Identity = identity;
        Metadata = metadata;
        StoredUtc = storedUtc;
        Checksum = checksum;
    }
    public VersionIdentity Identity { get; }
    public LogicalFileMetadata Metadata { get; }
    public DateTimeOffset StoredUtc { get; }
    public XxHash64Checksum Checksum { get; }
}

/// <summary>A session-owned generated identity for an externally prepared immutable payload. No copy is performed here.</summary>
public sealed class PreparedVersion
{
    internal PreparedVersion(Guid owner, Guid versionId, LogicalFileMetadata metadata, DateTimeOffset storedUtc, XxHash64Checksum checksum)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(checksum);
        ModelValidation.RequireUtc(storedUtc, nameof(storedUtc));
        Path = new HistoryPath(metadata.SourceRootId, metadata.RelativePath);
        Owner = owner;
        VersionId = versionId;
        Metadata = metadata;
        StoredUtc = storedUtc;
        Checksum = checksum;
    }
    internal Guid Owner { get; }
    public Guid VersionId { get; }
    public HistoryPath Path { get; }
    public LogicalFileMetadata Metadata { get; }
    public DateTimeOffset StoredUtc { get; }
    public XxHash64Checksum Checksum { get; }
}

/// <summary>Durable root description and protection configuration; no source filesystem is accessed.</summary>
public sealed class SourceRootConfiguration : IEquatable<SourceRootConfiguration>
{
    public SourceRootConfiguration(Guid sourceRootId, string originalPath, bool isProtected = true,
        long maximumFileSize = long.MaxValue, IEnumerable<string>? excludedDirectories = null)
    {
        ModelValidation.RequireIdentity(sourceRootId, nameof(sourceRootId));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        if (originalPath.Length > 32760 || originalPath.Any(char.IsControl))
            throw new ArgumentException("Invalid descriptive source location.", nameof(originalPath));
        _ = new System.Text.UnicodeEncoding(false, false, true).GetByteCount(originalPath);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileSize);
        var exclusions = (excludedDirectories ?? []).Take(257).Select(p => new HistoryPath(sourceRootId, p).CanonicalPath)
            .Order(StringComparer.Ordinal).ToArray();
        if (exclusions.Length > 256 || exclusions.Distinct(StringComparer.Ordinal).Count() != exclusions.Length)
            throw new ArgumentException("At most 256 distinct excluded directories are allowed.", nameof(excludedDirectories));
        if (System.Text.Encoding.UTF8.GetByteCount(originalPath) + exclusions.Sum(System.Text.Encoding.UTF8.GetByteCount) > 262144)
            throw new ArgumentException("Source configuration exceeds 256 KiB.", nameof(excludedDirectories));
        SourceRootId = sourceRootId;
        OriginalPath = originalPath;
        IsProtected = isProtected;
        MaximumFileSize = maximumFileSize;
        ExcludedDirectories = Array.AsReadOnly(exclusions);
    }
    public Guid SourceRootId { get; }
    public string OriginalPath { get; }
    public bool IsProtected { get; }
    public long MaximumFileSize { get; }
    public IReadOnlyList<string> ExcludedDirectories { get; }
    public bool Equals(SourceRootConfiguration? other) => other is not null && SourceRootId == other.SourceRootId &&
        OriginalPath == other.OriginalPath && IsProtected == other.IsProtected && MaximumFileSize == other.MaximumFileSize &&
        ExcludedDirectories.SequenceEqual(other.ExcludedDirectories);
    public override bool Equals(object? obj) => obj is SourceRootConfiguration other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(SourceRootId, OriginalPath, IsProtected, MaximumFileSize);
}

/// <summary>Only durable semantic changes. Added versions also become current; existing successful versions can be reactivated.</summary>
public sealed class MutationBatch
{
    public const int MaximumChanges = 4096;
    public MutationBatch(IEnumerable<PreparedVersion>? versions = null, IEnumerable<VersionIdentity>? currentUpserts = null,
        IEnumerable<HistoryPath>? currentRemovals = null, IEnumerable<SourceRootConfiguration>? sourceRoots = null)
    {
        Versions = Copy(versions);
        CurrentUpserts = Copy(currentUpserts);
        CurrentRemovals = Copy(currentRemovals);
        SourceRoots = Copy(sourceRoots);
        if ((long)Versions.Count + CurrentUpserts.Count + CurrentRemovals.Count + SourceRoots.Count > MaximumChanges)
            throw new ArgumentException("A v4 batch has at most 4096 requested changes.");
        var paths = Versions.Select(v => v.Path).Concat(CurrentUpserts.Select(v => v.Path)).Concat(CurrentRemovals).ToArray();
        if (paths.Distinct().Count() != paths.Length || SourceRoots.Select(r => r.SourceRootId).Distinct().Count() != SourceRoots.Count)
            throw new ArgumentException("A batch cannot contain conflicting changes to one path/root.");
    }
    public IReadOnlyList<PreparedVersion> Versions { get; }
    public IReadOnlyList<VersionIdentity> CurrentUpserts { get; }
    public IReadOnlyList<HistoryPath> CurrentRemovals { get; }
    public IReadOnlyList<SourceRootConfiguration> SourceRoots { get; }
    private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? items) where T : class
    {
        var array = (items ?? []).Take(MaximumChanges + 1).ToArray();
        if (array.Length > MaximumChanges || array.Any(v => v is null)) throw new ArgumentException("Invalid bounded batch collection.");
        return Array.AsReadOnly(array);
    }
}

public sealed record MutationResult(AuthorityIdentity Authority, bool Changed);
public sealed record MaintenanceLimits(long MaximumRecords = 2_000_000, int MaximumCurrentPaths = 1_000_000,
    long MaximumOutputBytes = 1_073_741_824, long MaximumScratchBytes = 1_073_741_824,
    long MaximumScratchBytesWritten = 8_589_934_592, long MaximumMaterializedBytes = 17_179_869_184);
public sealed record MetadataCleanupResult(int DeletedSegments, int DeferredSegments);
public sealed record PayloadCleanupLimits(long MaximumRecords = 2_000_000, int MaximumPayloads = 4096);
public sealed record PayloadCleanupProblem(VersionIdentity Identity, string Message);
public sealed record PayloadCleanupResult(long Inspected, long Deleted, long AlreadyAbsent, long Deferred,
    long Failed, bool Cancelled, IReadOnlyList<PayloadCleanupProblem> Problems);
public sealed record VersionRemovalResult(AuthorityIdentity Authority, int Removed, int AlreadyAbsent,
    long RemovedLogicalBytes, PayloadCleanupResult Cleanup)
{
    public long VersionPagesWritten { get; init; }
    public long MetadataBytesAppended { get; init; }
}

/// <summary>Visibility was confirmed before cleanup stopped. Never retry this removal as an unconfirmed batch.</summary>
public sealed class VersionRemovalCommittedException(VersionRemovalResult result, Exception cause)
    : IOException("Version removal committed, but payload cleanup stopped. Retain the confirmed result.", cause)
{
    public VersionRemovalResult Result { get; } = result;
}
public enum PublicationOutcome { Published, Unpublished }

public sealed class PublicationUncertainException(AuthorityIdentity intended, Exception cause)
    : IOException("V4 authority publication is uncertain. Preserve evidence and reconcile under fresh ownership.", cause)
{
    public AuthorityIdentity IntendedAuthority { get; } = intended;
}

public sealed class ReaderGenerationExpiredException : IOException
{
    public ReaderGenerationExpiredException() : base("The captured metadata generation was retired. Restart the query with a fresh reader.") { }
}

public sealed class PayloadVersionExpiredException : IOException
{
    public PayloadVersionExpiredException(VersionIdentity identity)
        : base("The captured version no longer survives. Restart selection with a fresh reader.") { Identity = identity; }
    public VersionIdentity Identity { get; }
}
