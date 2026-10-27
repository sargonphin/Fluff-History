using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

internal enum CapacityExecutionStage { OrderDerived, BatchCommitted, BeforeProbe, AfterProbe }

/// <summary>Standalone UntilSaturation operation. The caller supplies conservative upcoming allocation B.
/// Capacity observation, never logical removed lengths, controls satisfaction. No source scan.</summary>
public sealed class CapacityRetentionEngine
{
    private readonly Repository _repository;
    private readonly IRepositoryCapacityProbe _probe;
    private readonly CapacityRetentionOptions _options;
    internal Action<RepositoryWriter>? WriterAcquired { get; set; }
    internal Action<CapacityExecutionStage, CapacityRetentionResult>? Observe { get; set; }
    internal Action<CapacityOrder>? OrderReady { get; set; }
    internal Action<CapacitySortStage, string>? SortObserve { get; set; }
    // Live integration may discard optional cache at this clean boundary. Standalone behavior is unchanged.
    internal Func<RepositoryWriter, long, CancellationToken, bool>? BeforeHistory { get; set; }
    public CapacityRetentionEngine(Repository repository, CapacityRetentionOptions? options = null, IRepositoryCapacityProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(repository); _repository = repository; _options = options ?? new(); _options.Validate();
        _probe = probe ?? new NativeRepositoryCapacityProbe();
    }
    public CapacityRetentionResult EnsureCapacity(long upcomingAllocation, CancellationToken cancellationToken = default)
        => EnsureCapacityCore(upcomingAllocation, null, cancellationToken);

    /// <summary>Reuse retained ownership at a clean boundary. Never acquires/releases the caller's writer.</summary>
    public CapacityRetentionResult EnsureCapacity(RepositoryWriter writer, long upcomingAllocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return EnsureCapacityCore(upcomingAllocation, writer, cancellationToken);
    }
    private CapacityRetentionResult EnsureCapacityCore(long upcomingAllocation, RepositoryWriter? retainedWriter, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(upcomingAllocation);
        using var assessment = retainedWriter is null ? _repository.OpenReader() : retainedWriter.OpenCapacityReader(_repository);
        var result = new CapacityRetentionResult(assessment.Authority, upcomingAllocation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); Probe();
            if (Terminal()) return result; // No writer, history scan, scratch, cleanup or authority mutation.
            using var acquired = retainedWriter is null ? _repository.OpenWriter(_options.WorkspaceRoot, streamMetadataReads: true) : null;
            var writer = retainedWriter ?? acquired!; WriterAcquired?.Invoke(writer);
            using var reader = retainedWriter is null ? writer.OpenOwnedReader() : writer.OpenCapacityReader(_repository);
            result.StartingAuthority = reader.Authority; result.FinalConfirmedAuthority = reader.Authority;
            cancellationToken.ThrowIfCancellationRequested(); Probe();
            if (Terminal()) return result;
            if (BeforeHistory?.Invoke(writer, upcomingAllocation, cancellationToken) == true)
            { Probe(); if (Terminal()) return result; }
            using var ordered = CapacityOrder.Derive(_repository, reader, _options, result, cancellationToken, SortObserve);
            Observe?.Invoke(CapacityExecutionStage.OrderDerived, result); OrderReady?.Invoke(ordered);
            cancellationToken.ThrowIfCancellationRequested(); Probe(); // Sorting may take time; check again before destruction.
            if (Terminal()) return result;
            using var candidates = ordered.Candidates().GetEnumerator();
            RetentionVersion? pending = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<VersionIdentity>(); long bytes = 0, logicalBytes = 0;
                while (batch.Count < _options.BatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pending is null && !candidates.MoveNext()) break;
                    var item = pending ?? candidates.Current; pending = null; var charge = RetentionOptions.Charge(item);
                    if (charge > _options.MaximumCandidateBytes - bytes) { pending = item; break; }
                    batch.Add(item.Identity); bytes += charge; logicalBytes = checked(logicalBytes + item.Length);
                }
                if (batch.Count == 0) { result.Outcome = CapacityRetentionOutcome.InsufficientHistory; return result; }
                if (result.BatchesCommitted >= _options.MaximumBatches) { result.Outcome = CapacityRetentionOutcome.WorkLimitExceeded; return result; }
                _ = checked(result.RemovedLogicalBytes + logicalBytes); // Refuse unrepresentable reporting before this batch can commit.
                result.MaximumBatchCount = Math.Max(result.MaximumBatchCount, batch.Count); result.MaximumBatchBytes = Math.Max(result.MaximumBatchBytes, bytes);
                VersionRemovalResult removal;
                try { removal = writer.RemoveVersions(batch, result.FinalConfirmedAuthority, cancellationToken: cancellationToken); }
                catch (VersionRemovalCommittedException error) { Record(error.Result); throw; }
                Record(removal); Observe?.Invoke(CapacityExecutionStage.BatchCommitted, result);
                // Even late cancellation cannot turn confirmed visibility into an unpublished batch.
                // Obtain post-cleanup feedback before observing cancellation for the next batch.
                Probe(); if (Terminal()) return result;
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
        { result.Outcome = CapacityRetentionOutcome.Cancelled; return result; }
        catch (CapacityWorkLimitException) { result.Outcome = CapacityRetentionOutcome.WorkLimitExceeded; return result; }
        catch (Exception error) { result.Outcome = CapacityRetentionOutcome.ControlError; throw new CapacityRetentionException(result, error); }

        void Probe()
        {
            Observe?.Invoke(CapacityExecutionStage.BeforeProbe, result); result.ProbeAttempts++;
            var observed = _probe.Observe(_repository); ArgumentNullException.ThrowIfNull(observed);
            // Preserve trustworthy C/F even when new B+R cannot be represented; never retain a stale target.
            observed = new(observed.Capacity, observed.Free);
            result.InitialObservation ??= observed; result.LatestObservation = observed; result.CapacityObservations++; result.Latest = null;
            result.Observed(new(observed, upcomingAllocation)); Observe?.Invoke(CapacityExecutionStage.AfterProbe, result);
        }
        bool Terminal()
        {
            if (result.Latest!.Impossible) { result.Outcome = CapacityRetentionOutcome.ImpossibleTarget; return true; }
            if (!result.Latest.Satisfied) return false;
            result.Outcome = result.BatchesCommitted == 0 ? CapacityRetentionOutcome.AlreadySatisfied : CapacityRetentionOutcome.SatisfiedAfterRetention; return true;
        }
        void Record(VersionRemovalResult removal)
        {
            result.FinalConfirmedAuthority = removal.Authority;
            result.Removed = checked(result.Removed + removal.Removed); result.AlreadyAbsentCandidates = checked(result.AlreadyAbsentCandidates + removal.AlreadyAbsent);
            result.RemovedLogicalBytes = checked(result.RemovedLogicalBytes + removal.RemovedLogicalBytes);
            if (removal.Removed != 0) result.BatchesCommitted++;
            result.VersionPagesWritten = checked(result.VersionPagesWritten + removal.VersionPagesWritten);
            result.MetadataBytesAppended = checked(result.MetadataBytesAppended + removal.MetadataBytesAppended);
            var a = result.Cleanup; var b = removal.Cleanup;
            result.Cleanup = new(checked(a.Inspected + b.Inspected), checked(a.Deleted + b.Deleted), checked(a.AlreadyAbsent + b.AlreadyAbsent),
                checked(a.Deferred + b.Deferred), checked(a.Failed + b.Failed), a.Cancelled || b.Cancelled,
                Array.AsReadOnly(a.Problems.Concat(b.Problems).Take(128).ToArray()));
        }
    }
}
