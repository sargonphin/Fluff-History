using System.ComponentModel;
using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>Admission and telemetry only. All destructive work delegates to M16.1.</summary>
internal sealed class ScanCapacitySession
{
    private readonly Repository _repository;
    private readonly RepositoryWriter _writer;
    private readonly ScanResult _result;
    private readonly ScanCapacityStatistics _stats;
    private readonly AllocationGeometry _geometry;
    private readonly CountingProbe _probe;
    private readonly CapacityRetentionEngine _retention;
    private readonly Action _authorityAdvanced;
    private readonly CancellationToken _token;

    internal ScanCapacitySession(Repository repository, RepositoryWriter writer, ScanOptions options, ScanResult result,
        IRepositoryCapacityProbe probe, IRepositoryAllocationProbe geometry, Action authorityAdvanced, CancellationToken token)
    {
        _repository = repository; _writer = writer; _result = result; _stats = result.Capacity; _token = token; _authorityAdvanced = authorityAdvanced;
        _stats.Enabled = true; _stats.ReclamationAuthorized = options.Capacity!.Policy == RetentionPolicy.UntilSaturation;
        _geometry = geometry.Observe(repository, options.WorkspaceRoot); ArgumentNullException.ThrowIfNull(_geometry);
        _probe = new(probe, _stats);
        _retention = new(repository, (options.Capacity.Retention ?? new()) with { WorkspaceRoot = options.WorkspaceRoot }, _probe);
        _retention.BeforeHistory = (_, bytes, _) => EvictCache(bytes).Attempted;
    }

    internal BackupAllocationEstimate Estimate(PublicationAllocationRequest plan, long nextPayload, bool newPayload = false)
    {
        var publication = _writer.EstimatePublicationAllocation(plan, _geometry.AllocationUnit);
        var payload = AllocationGeometry.RoundUp(nextPayload == 0 && !newPayload ? 0 : _repository.EstimatePayloadPhysicalBytes(nextPayload), _geometry.AllocationUnit);
        var estimate = new BackupAllocationEstimate(payload, publication.TotalBytes, checked(payload + publication.TotalBytes), publication);
        if (estimate.TotalBytes >= _stats.MaximumUpcomingAllocation) _stats.MaximumEstimate = estimate;
        _stats.LatestEstimate = estimate; _stats.MaximumPayloadAllocation = Math.Max(_stats.MaximumPayloadAllocation, payload);
        _stats.MaximumPublicationAllocation = Math.Max(_stats.MaximumPublicationAllocation, publication.TotalBytes);
        var prior = _stats.PublicationComponentMaxima;
        _stats.PublicationComponentMaxima = prior is null ? publication : new(
            Math.Max(prior.SemanticBytes, publication.SemanticBytes), Math.Max(prior.IndexBytes, publication.IndexBytes),
            Math.Max(prior.RootBytes, publication.RootBytes), Math.Max(prior.RotationBytes, publication.RotationBytes),
            Math.Max(prior.TransferAndInstallBytes, publication.TransferAndInstallBytes), Math.Max(prior.ControlBytes, publication.ControlBytes),
            Math.Max(prior.NamespaceAllowanceBytes, publication.NamespaceAllowanceBytes), Math.Max(prior.TotalBytes, publication.TotalBytes),
            Math.Max(prior.MaximumPages, publication.MaximumPages));
        Upcoming(estimate.TotalBytes); return estimate;
    }
    private void Upcoming(long bytes)
    { _stats.LatestUpcomingAllocation = bytes; _stats.MaximumUpcomingAllocation = Math.Max(_stats.MaximumUpcomingAllocation, bytes); }

    // Safe with vulnerable pending state: observation only, no M16.1 invocation or deletion.
    internal bool Assess(long bytes)
    {
        _token.ThrowIfCancellationRequested(); Upcoming(bytes);
        var observed = _probe.Observe(_repository);
        _stats.LatestTarget = null;
        if (_stats.ReclamationAuthorized) _stats.LatestTarget = new CapacityTarget(observed, bytes);
        return _stats.LatestTarget?.Satisfied ?? observed.Free >= bytes;
    }
    internal void RequirePublication(long bytes)
    {
        if (Assess(bytes)) return;
        var impossible = _stats.LatestTarget?.Impossible ?? bytes > _stats.LatestObservation!.Capacity;
        Stop(impossible ? ScanOutcome.CapacityTargetImpossible : ScanOutcome.CapacityBlocked);
    }
    internal void Owned(long bytes)
    {
        _token.ThrowIfCancellationRequested(); Upcoming(bytes);
        if (!_stats.ReclamationAuthorized)
        {
            if (Assess(bytes) || EvictCache(bytes).Satisfied) return;
            Stop(bytes > _stats.LatestObservation!.Capacity ? ScanOutcome.CapacityTargetImpossible : ScanOutcome.CapacityBlocked); return;
        }
        _stats.RetentionInvocations++;
        CapacityRetentionResult retention;
        try { retention = _retention.EnsureCapacity(_writer, bytes, _token); }
        catch (CapacityRetentionException error) { Record(error.Result); throw; }
        Record(retention);
        switch (retention.Outcome)
        {
            case CapacityRetentionOutcome.AlreadySatisfied:
            case CapacityRetentionOutcome.SatisfiedAfterRetention: return;
            case CapacityRetentionOutcome.Cancelled: Stop(ScanOutcome.Cancelled); break;
            case CapacityRetentionOutcome.WorkLimitExceeded: Stop(ScanOutcome.CapacityWorkLimitExceeded); break;
            case CapacityRetentionOutcome.ImpossibleTarget: Stop(ScanOutcome.CapacityTargetImpossible); break;
            case CapacityRetentionOutcome.InsufficientHistory: Stop(ScanOutcome.CapacityBlocked); break;
            default: throw new IOException("Capacity maintenance did not produce a usable admission result.");
        }
    }
    private (bool Attempted, bool Satisfied) EvictCache(long bytes)
    {
        // Inventory and deletion are explicitly bounded; never inspect unknown staging/version trees.
        // One page per clean assessment prevents repeated failed entries from creating an unbounded loop.
        var page = _writer.GetReadyReusablePayloads(maximumEntries: 128, maximumNames: 4096); bool attempted = false;
        foreach (var entry in page.Entries)
        {
            _token.ThrowIfCancellationRequested();
            if (!_writer.RemoveReadyReusablePayload(entry, forCapacity: true)) continue;
            attempted = true;
            if (Assess(bytes)) return (true, true); // Only actual F can establish satisfaction.
        }
        return (attempted, false);
    }
    private void Record(CapacityRetentionResult value)
    {
        // Preserve confirmed authority even if checked aggregate reporting fails.
        var advanced = _result.Authority != value.FinalConfirmedAuthority;
        _result.Authority = value.FinalConfirmedAuthority;
        _stats.LatestRetentionOutcome = value.Outcome; _stats.LatestTarget = value.Latest;
        checked
        {
            _stats.HistoryScans += value.HistoryScans; _stats.VersionsRemoved += value.Removed; _stats.RemovedLogicalBytes += value.RemovedLogicalBytes;
            _stats.RemovalBatches += value.BatchesCommitted; _stats.MetadataBytesAppended += value.MetadataBytesAppended;
            _stats.MetadataBytesRequested += value.Reads?.MetadataBytesRequested ?? 0;
            var a = _stats.Cleanup; var b = value.Cleanup;
            _stats.Cleanup = new(a.Inspected + b.Inspected, a.Deleted + b.Deleted, a.AlreadyAbsent + b.AlreadyAbsent,
                a.Deferred + b.Deferred, a.Failed + b.Failed, a.Cancelled || b.Cancelled, Array.AsReadOnly(a.Problems.Concat(b.Problems).Take(128).ToArray()));
        }
        if (advanced) _authorityAdvanced();
    }
    private void Stop(ScanOutcome outcome)
    { _result.Outcome = outcome; _result.Cancelled = outcome == ScanOutcome.Cancelled; throw new ScanCapacityStop(); }
    internal static bool DiskFull(Exception error)
    {
        // Never classify uncertain publication, a capacity-control failure, or wrapped source errors.
        if (error is not IOException || error is PublicationUncertainException or CapacityRetentionException or SourceFailureException) return false;
        var code = error.InnerException is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;
        return OperatingSystem.IsWindows() ? code is 112 or 39 : code == 28;
    }
    private sealed class CountingProbe(IRepositoryCapacityProbe inner, ScanCapacityStatistics stats) : IRepositoryCapacityProbe
    {
        public CapacityObservation Observe(Repository repository)
        {
            stats.Checks++; var result = inner.Observe(repository); ArgumentNullException.ThrowIfNull(result);
            result = new(result.Capacity, result.Free); stats.InitialObservation ??= result; stats.LatestObservation = result; return result;
        }
    }
}
