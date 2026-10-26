using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>Transient orchestration of the existing product policy, not settings persistence.
/// Null ScanOptions.Capacity preserves the legacy caller-managed capacity contract.</summary>
public sealed record ScanCapacityOptions(RetentionPolicy Policy = RetentionPolicy.Forever, CapacityRetentionOptions? Retention = null)
{
    internal void Validate()
    { if (!Enum.IsDefined(Policy)) throw new ArgumentOutOfRangeException(nameof(Policy)); Retention?.Validate(); }
}

/// <summary>Trusted seam: geometry MUST belong to this repository and workspace MUST be on a
/// separate filesystem. Production verifies both natively; portable tests may inject a model.</summary>
public interface IRepositoryAllocationProbe
{ AllocationGeometry Observe(Repository repository, string? workspaceRoot); }
internal sealed class NativeRepositoryAllocationProbe : IRepositoryAllocationProbe
{ public AllocationGeometry Observe(Repository repository, string? workspaceRoot) => repository.ObserveAllocationGeometry(workspaceRoot); }

public enum ScanOutcome { Completed, Cancelled, CapacityBlocked, CapacityTargetImpossible, CapacityWorkLimitExceeded, ControlError }

public sealed record BackupAllocationEstimate(long UpcomingPayloadBytes, long PublicationBytes, long TotalBytes,
    PublicationAllocationEstimate Publication);

public sealed class ScanCapacityStatistics
{
    public bool Enabled { get; internal set; }
    public bool ReclamationAuthorized { get; internal set; }
    public long Checks { get; internal set; }
    public long RetentionInvocations { get; internal set; }
    public CapacityObservation? InitialObservation { get; internal set; }
    public CapacityObservation? LatestObservation { get; internal set; }
    public CapacityTarget? LatestTarget { get; internal set; }
    public long LatestUpcomingAllocation { get; internal set; }
    public long MaximumUpcomingAllocation { get; internal set; }
    public long MaximumPayloadAllocation { get; internal set; }
    public long MaximumPublicationAllocation { get; internal set; }
    public BackupAllocationEstimate? LatestEstimate { get; internal set; }
    public BackupAllocationEstimate? MaximumEstimate { get; internal set; }
    /// <summary>Independent per-component maxima, not the components of one combined request.</summary>
    public PublicationAllocationEstimate? PublicationComponentMaxima { get; internal set; }
    public long HistoryScans { get; internal set; }
    public long VersionsRemoved { get; internal set; }
    public long RemovedLogicalBytes { get; internal set; }
    public long RemovalBatches { get; internal set; }
    public long MetadataBytesAppended { get; internal set; }
    public long MetadataBytesRequested { get; internal set; }
    public CapacityRetentionOutcome? LatestRetentionOutcome { get; internal set; }
    public PayloadCleanupResult Cleanup { get; internal set; } = new(0, 0, 0, 0, 0, false, Array.Empty<PayloadCleanupProblem>());
}

/// <summary>Control failure with confirmed source and retention progress. Publication uncertainty
/// anywhere in the cause chain still requires existing explicit Storage reconciliation.</summary>
public sealed class ScanCapacityException(ScanResult result, Exception cause)
    : IOException("Capacity-enabled scan stopped. Confirmed progress remains committed; inspect the cause before retrying.", cause)
{ public ScanResult Result { get; } = result; }

internal sealed class ScanCapacityStop : Exception;
