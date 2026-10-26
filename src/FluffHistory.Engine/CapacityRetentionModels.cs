using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>A narrow repository-bound observation seam. Implementations must report trustworthy caller-visible bytes.</summary>
public interface IRepositoryCapacityProbe { CapacityObservation Observe(Repository repository); }
internal sealed class NativeRepositoryCapacityProbe : IRepositoryCapacityProbe
{ public CapacityObservation Observe(Repository repository) => repository.ObserveCapacity(); }

public sealed record CapacityRetentionOptions(int BatchSize = 16, long MaximumCandidateBytes = 4 * 1024 * 1024,
    long MaximumVersions = 2_000_000, long MaximumBatches = 16_384, int SortChunkEntries = 4096,
    long SortChunkBytes = 4 * 1024 * 1024, long MaximumScratchBytes = 1_073_741_824,
    long MaximumScratchBytesWritten = 8_589_934_592, string? WorkspaceRoot = null)
{
    internal void Validate()
    {
        if (BatchSize is < 1 or > MutationBatch.MaximumChanges || MaximumCandidateBytes is < 8512 or > 4 * 1024 * 1024 ||
            MaximumVersions < 1 || MaximumBatches < 1 || SortChunkEntries is < 1 or > 16_384 || SortChunkBytes is < 8512 or > 4 * 1024 * 1024 ||
            MaximumScratchBytes < 1 || MaximumScratchBytesWritten < 1) throw new ArgumentOutOfRangeException(nameof(CapacityRetentionOptions));
    }
}
public enum CapacityRetentionOutcome { Interrupted, AlreadySatisfied, SatisfiedAfterRetention, InsufficientHistory, ImpossibleTarget, Cancelled, WorkLimitExceeded, ControlError }

public sealed class CapacitySortStatistics
{
    public long InputVersions { get; internal set; }
    public long PublicationRecordsValidated { get; internal set; }
    public long EligibleInput { get; internal set; }
    public long OrderedCandidates { get; internal set; }
    public long RunsCreated { get; internal set; }
    public long Merges { get; internal set; }
    public long ScratchBytesWritten { get; internal set; }
    public long PeakScratchBytes { get; internal set; }
    public long PeakChunkEntries { get; internal set; }
    public long PeakChunkBytes { get; internal set; }
    public int PeakOpenFiles { get; internal set; }
    public int PeakReadFiles { get; internal set; }
    public int PeakMergeFanIn { get; internal set; }
}
public sealed class CapacityRetentionResult
{
    internal CapacityRetentionResult(AuthorityIdentity authority, long upcoming)
    { StartingAuthority = authority; FinalConfirmedAuthority = authority; UpcomingAllocation = upcoming; }
    public AuthorityIdentity StartingAuthority { get; internal set; }
    public AuthorityIdentity FinalConfirmedAuthority { get; internal set; }
    public long UpcomingAllocation { get; }
    public CapacityTarget? Initial { get; internal set; }
    public CapacityTarget? Latest { get; internal set; }
    public CapacityObservation? InitialObservation { get; internal set; }
    public CapacityObservation? LatestObservation { get; internal set; }
    public bool Satisfied => Latest?.Satisfied == true;
    public CapacityRetentionOutcome Outcome { get; internal set; }
    public bool Partial => BatchesCommitted != 0 && Outcome is not CapacityRetentionOutcome.SatisfiedAfterRetention;
    public long ProbeAttempts { get; internal set; }
    public long CapacityObservations { get; internal set; }
    private readonly List<CapacityTarget> _observations = [];
    public IReadOnlyList<CapacityTarget> Observations => _observations.AsReadOnly();
    internal void Observed(CapacityTarget target)
    {
        Initial ??= target; Latest = target;
        if (_observations.Count < 128) _observations.Add(target); else _observations[^1] = target;
    }
    public long HistoryScans { get; internal set; }
    public long Examined { get; internal set; }
    public long Protected { get; internal set; }
    public long Eligible { get; internal set; }
    public long Removed { get; internal set; }
    public long AlreadyAbsentCandidates { get; internal set; }
    public long RemovedLogicalBytes { get; internal set; }
    public long BatchesCommitted { get; internal set; }
    public long MaximumBatchCount { get; internal set; }
    public long MaximumBatchBytes { get; internal set; }
    public long VersionPagesWritten { get; internal set; }
    public long MetadataBytesAppended { get; internal set; }
    public RetentionReadStatistics? Reads { get; internal set; }
    public CapacitySortStatistics Sort { get; } = new();
    public PayloadCleanupResult Cleanup { get; internal set; } = new(0, 0, 0, 0, 0, false, Array.Empty<PayloadCleanupProblem>());
}
public sealed class CapacityRetentionException(CapacityRetentionResult result, Exception cause)
    : IOException("Capacity retention stopped. Confirmed removals remain committed; inspect the cause before retry/reconciliation.", cause)
{ public CapacityRetentionResult Result { get; } = result; }
internal sealed class CapacityWorkLimitException(string message) : IOException(message);
