using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum MetadataMaintenanceState { NotSafe, BelowFloor, Throttled, HeadroomUnavailable, AssessmentUnavailable, BelowThreshold, Compacted }

/// <summary>Detached transient diagnostics. Decoded accounting is not physical free-space credit.</summary>
public sealed record MetadataMaintenanceSummary(MetadataMaintenanceState State, bool Assessed = false,
    long ActiveDecodedBytes = 0, long ReclaimableDecodedBytes = 0, long DenseDecodedBytes = 0,
    long OutputBytes = 0, int RetiredDeleted = 0, int RetiredDeferred = 0)
{
    public double DirtyRatio => ActiveDecodedBytes == 0 ? 0 : (double)ReclaimableDecodedBytes / ActiveDecodedBytes;
    public void Validate()
    {
        if (!Enum.IsDefined(State) || ActiveDecodedBytes < 0 || ReclaimableDecodedBytes < 0 ||
            ReclaimableDecodedBytes > ActiveDecodedBytes || DenseDecodedBytes < 0 || OutputBytes < 0 || RetiredDeleted < 0 || RetiredDeferred < 0)
            throw new InvalidDataException("Invalid metadata maintenance summary.");
    }
}

/// <summary>One admitted runner's in-memory maintenance policy. No timer, key retention, setting,
/// backup-run object or startup work. Repository writer remains final authority.</summary>
internal sealed class AutomaticMetadataMaintenance
{
    internal MaintenanceLimits Limits { get; init; } = new(MaximumScratchBytes: 256L * 1024 * 1024,
        MaximumScratchBytesWritten: 2L * 1024 * 1024 * 1024);
    internal Func<RepositoryWriter, MaintenanceLimits, bool> Headroom { get; init; } = (writer, limits) => writer.HasMaintenanceHeadroom(limits);
    internal TimeSpan AssessmentBudget { get; init; } = TimeSpan.FromMinutes(3);
    private ActiveMetadataSummary? _last;
    private bool _retryPrerequisite;
    private int _changedOpportunities;

    internal MetadataMaintenanceSummary Run(Repository repository, BackupConfiguration configuration, ScanResult scan,
        MaintenanceSummary retention, Action<BackupPhase> report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (scan.Outcome != ScanOutcome.Completed || scan.Partial || scan.Cancelled || scan.ReconciliationMayBeRequired ||
            retention.State is not (MaintenanceState.NotNeeded or MaintenanceState.Throttled or MaintenanceState.Completed))
            return new(MetadataMaintenanceState.NotSafe);
        using var writer = repository.OpenWriter(configuration.ScanOptions.WorkspaceRoot, streamMetadataReads: true);
        using (writer.OpenCapacityReader(repository)) { }
        report(BackupPhase.CleaningMetadata);
        var cleanup = writer.CleanupRetiredMetadata(token);
        report(BackupPhase.CheckingMetadata);
        var active = writer.InspectActiveMetadata(Limits, token);
        MetadataMaintenanceSummary Result(MetadataMaintenanceState state) => new(state, ActiveDecodedBytes: active.DecodedBytes,
            RetiredDeleted: cleanup.DeletedSegments, RetiredDeferred: cleanup.DeferredSegments);
        if (active.DecodedBytes < MetadataMaintenancePolicy.MinimumReclaimableBytes) return Result(MetadataMaintenanceState.BelowFloor);
        if (_last?.Authority == active.Authority)
        {
            if (!_retryPrerequisite || ++_changedOpportunities < 12) return Result(MetadataMaintenanceState.Throttled);
        }
        else if (_last?.Authority.RepositoryId == active.Authority.RepositoryId &&
            active.DecodedBytes - _last.DecodedBytes < MetadataMaintenancePolicy.MinimumReclaimableBytes &&
            !active.LatestRecordRemovesVersion && retention.Removed == 0 && ++_changedOpportunities < 12)
            return Result(MetadataMaintenanceState.Throttled);
        // Remember even a bounded failed assessment, avoiding a costly retry on every no-change tick.
        _last = active; _changedOpportunities = 0; _retryPrerequisite = false;
        bool Space(MaintenanceLimits limits)
        {
            try { return Headroom(writer, limits); }
            catch (Exception) { return false; }
            finally { token.ThrowIfCancellationRequested(); }
        }
        MetadataMaintenanceSummary Unavailable(MetadataMaintenanceState state)
        { _retryPrerequisite = true; return Result(state); }
        if (!Space(Limits with { MaximumOutputBytes = 1 })) return Unavailable(MetadataMaintenanceState.HeadroomUnavailable);
        MetadataAssessment assessment;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            budget.CancelAfter(AssessmentBudget);
            try { assessment = writer.AssessMetadata(Limits, budget.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Unavailable(MetadataMaintenanceState.AssessmentUnavailable); }
            catch (IOException) { token.ThrowIfCancellationRequested(); return Unavailable(MetadataMaintenanceState.AssessmentUnavailable); }
        }
        token.ThrowIfCancellationRequested();
        var measured = Result(MetadataMaintenanceState.BelowThreshold) with { Assessed = true,
            ReclaimableDecodedBytes = assessment.ReclaimableBytes, DenseDecodedBytes = assessment.DenseDecodedBytes };
        if (!MetadataMaintenancePolicy.Eligible(active.DecodedBytes, assessment.ReclaimableBytes)) return measured;
        if (assessment.MaximumOutputBytes > Limits.MaximumOutputBytes) return measured with { State = MetadataMaintenanceState.HeadroomUnavailable };
        var compactionLimits = Limits with { MaximumOutputBytes = assessment.MaximumOutputBytes };
        if (!Space(compactionLimits)) { _retryPrerequisite = true; return measured with { State = MetadataMaintenanceState.HeadroomUnavailable }; }
        token.ThrowIfCancellationRequested(); report(BackupPhase.CompactingMetadata);
        // From this point, errors retain the existing abort/publication/reconciliation behavior.
        writer.CompactMetadata(compactionLimits, token);
        var outputBytes = writer.LastPublicationMetadataBytes;
        report(BackupPhase.CleaningMetadata);
        var finalCleanup = writer.CleanupRetiredMetadata(token);
        _last = writer.InspectActiveMetadata(Limits, token);
        return measured with { State = MetadataMaintenanceState.Compacted, OutputBytes = outputBytes,
            RetiredDeleted = checked(cleanup.DeletedSegments + finalCleanup.DeletedSegments), RetiredDeferred = finalCleanup.DeferredSegments };
    }
}
