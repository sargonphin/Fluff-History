using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

public sealed record RetentionOptions(int BatchSize = 128, long MaximumCandidateBytes = 4 * 1024 * 1024,
    long MaximumExaminedVersions = 2_000_000, string? WorkspaceRoot = null)
{
    internal void Validate()
    {
        if (BatchSize is < 1 or > MutationBatch.MaximumChanges || MaximumCandidateBytes is < 8_512 or > 4 * 1024 * 1024 ||
            MaximumExaminedVersions < 1) throw new ArgumentOutOfRangeException(nameof(RetentionOptions));
    }
    // Charge the encoded key, UTF-16 canonical path and fixed identity/result overhead.
    internal static long Charge(RetentionVersion version) => checked(256L + 2 * (30L + 2 * version.Identity.Path.CanonicalPath.Length));
}

public sealed class RetentionPreview
{
    internal RetentionPreview(AuthorityIdentity authority, RetentionEvaluation evaluation) { Authority = authority; Evaluation = evaluation; }
    public AuthorityIdentity Authority { get; }
    public RetentionEvaluation Evaluation { get; }
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
    public bool BudgetExceeded { get; internal set; }
    public long Examined { get; internal set; }
    public long Protected { get; internal set; }
    public long Eligible { get; internal set; }
    public long EligibleLogicalBytes { get; internal set; }
    public RetentionReadStatistics? Reads { get; internal set; }
}

/// <summary>Opaque in-memory repository-wide position, bound to exact authority/evaluation and page bounds.</summary>
public sealed class RetentionCursor
{
    internal AuthorityIdentity Authority { get; }
    internal RetentionEvaluation Evaluation { get; }
    internal VersionIdentity After { get; }
    internal int PageSize { get; }
    internal long Bytes { get; }
    internal RetentionCursor(AuthorityIdentity authority, RetentionEvaluation evaluation, VersionIdentity after, int pageSize, long bytes)
    { Authority = authority; Evaluation = evaluation; After = after; PageSize = pageSize; Bytes = bytes; }
}
public sealed record RetentionCandidatePage(IReadOnlyList<RetentionVersion> Items, RetentionCursor? Continuation,
    bool Complete, bool Cancelled, bool BudgetExceeded, long Examined, long ChargedCandidateBytes);

public sealed class RetentionExecutionResult
{
    internal RetentionExecutionResult(RetentionPreview preview, AuthorityIdentity starting)
    { PreviewAuthority = preview.Authority; StartingAuthority = starting; FinalConfirmedAuthority = starting; }
    public AuthorityIdentity PreviewAuthority { get; }
    public AuthorityIdentity StartingAuthority { get; }
    public AuthorityIdentity FinalConfirmedAuthority { get; internal set; }
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
    public bool BudgetExceeded { get; internal set; }
    public bool Partial => !Complete && BatchesCommitted > 0;
    public long Examined { get; internal set; }
    public long Protected { get; internal set; }
    public long Eligible { get; internal set; }
    public long Removed { get; internal set; }
    public long AlreadyAbsent { get; internal set; }
    public long RemovedLogicalBytes { get; internal set; }
    public long BatchesCommitted { get; internal set; }
    public long AgeScans { get; internal set; }
    public long MaximumBatchCount { get; internal set; }
    public long MaximumBatchBytes { get; internal set; }
    public long VersionPagesWritten { get; internal set; }
    public long MetadataBytesAppended { get; internal set; }
    public PayloadCleanupResult Cleanup { get; internal set; } = new(0, 0, 0, 0, 0, false, Array.Empty<PayloadCleanupProblem>());
    public RetentionReadStatistics? Reads { get; internal set; }
}

public sealed class StaleRetentionPreviewException(AuthorityIdentity preview, AuthorityIdentity actual)
    : InvalidOperationException("Retention preview is stale; produce a new preview before execution.")
{
    public AuthorityIdentity PreviewAuthority { get; } = preview;
    public AuthorityIdentity ActualAuthority { get; } = actual;
}

/// <summary>Execution stopped; confirmed earlier progress is available even on corruption/uncertain publication.
/// An inner PublicationUncertainException still requires fresh explicit Storage reconciliation.</summary>
public sealed class RetentionExecutionException(RetentionExecutionResult result, Exception cause)
    : IOException("Retention execution stopped. Confirmed batches remain committed; inspect the cause before retrying.", cause)
{
    public RetentionExecutionResult Result { get; } = result;
}
