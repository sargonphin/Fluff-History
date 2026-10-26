using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

public sealed class HistoryCleanupPreview
{
    internal HistoryCleanupPreview(AuthorityIdentity authority, HistoryCleanupEvaluation evaluation)
    { Authority = authority; Evaluation = evaluation; }
    public AuthorityIdentity Authority { get; }
    public HistoryCleanupEvaluation Evaluation { get; }
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
    public bool BudgetExceeded { get; internal set; }
    public long Examined { get; internal set; }
    public long PathsExamined { get; internal set; }
    public long Protected { get; internal set; }
    public long Eligible { get; internal set; }
    public long AffectedPaths { get; internal set; }
    public long EligibleLogicalBytes { get; internal set; }
    public RetentionReadStatistics? Reads { get; internal set; }
}

/// <summary>Opaque, in-memory position. At most one undecided version bridges pages.</summary>
public sealed class HistoryCleanupCursor
{
    internal AuthorityIdentity Authority { get; }
    internal HistoryCleanupEvaluation Evaluation { get; }
    internal int PageSize { get; }
    internal long Bytes { get; }
    internal long Work { get; }
    internal CleanupPosition Position { get; }
    internal HistoryCleanupCursor(AuthorityIdentity authority, HistoryCleanupEvaluation evaluation, int pageSize,
        RetentionOptions options, CleanupPosition position)
    { Authority = authority; Evaluation = evaluation; PageSize = pageSize; Bytes = options.MaximumCandidateBytes;
        Work = options.MaximumExaminedVersions; Position = position; }
}

internal sealed record CleanupPosition(VersionIdentity? After, RetentionVersion? Pending, bool CurrentSeen);

public sealed record HistoryCleanupCandidatePage(IReadOnlyList<RetentionVersion> Items, HistoryCleanupCursor? Continuation,
    bool Complete, bool Cancelled, bool BudgetExceeded, long Examined, long ChargedCandidateBytes);

public sealed class HistoryCleanupExecutionResult
{
    internal HistoryCleanupExecutionResult(HistoryCleanupPreview preview, AuthorityIdentity starting)
    { PreviewAuthority = preview.Authority; StartingAuthority = starting; FinalConfirmedAuthority = starting; }
    public AuthorityIdentity PreviewAuthority { get; }
    public AuthorityIdentity StartingAuthority { get; }
    public AuthorityIdentity FinalConfirmedAuthority { get; internal set; }
    public bool Complete { get; internal set; }
    public bool Cancelled { get; internal set; }
    public bool BudgetExceeded { get; internal set; }
    public bool Partial => !Complete && BatchesCommitted > 0;
    public long Examined { get; internal set; }
    public long PathsExamined { get; internal set; }
    public long Protected { get; internal set; }
    public long Eligible { get; internal set; }
    public long AffectedPaths { get; internal set; }
    public long Removed { get; internal set; }
    public long AlreadyAbsent { get; internal set; }
    public long RemovedLogicalBytes { get; internal set; }
    public long BatchesCommitted { get; internal set; }
    public long HistoryScans { get; internal set; }
    public long MaximumBatchCount { get; internal set; }
    public long MaximumBatchBytes { get; internal set; }
    public long VersionPagesWritten { get; internal set; }
    public long MetadataBytesAppended { get; internal set; }
    public PayloadCleanupResult Cleanup { get; internal set; } = new(0, 0, 0, 0, 0, false, Array.Empty<PayloadCleanupProblem>());
    public RetentionReadStatistics? Reads { get; internal set; }
}

public sealed class StaleHistoryCleanupPreviewException(AuthorityIdentity preview, AuthorityIdentity actual)
    : InvalidOperationException("History Cleanup preview is stale; produce a fresh preview.")
{
    public AuthorityIdentity PreviewAuthority { get; } = preview;
    public AuthorityIdentity ActualAuthority { get; } = actual;
}

/// <summary>Confirmed progress survives failure. An inner PublicationUncertainException requires explicit reconciliation.</summary>
public sealed class HistoryCleanupExecutionException(HistoryCleanupExecutionResult result, Exception cause)
    : IOException("History Cleanup stopped. Confirmed batches remain committed; inspect the cause before retrying.", cause)
{
    public HistoryCleanupExecutionResult Result { get; } = result;
}
