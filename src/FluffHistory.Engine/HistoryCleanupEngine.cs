using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>Explicit one-shot cleanup. Storage owns all durable removal and payload cleanup.</summary>
public sealed class HistoryCleanupEngine
{
    private readonly Repository _repository;
    private readonly RetentionOptions _options;
    internal Action<HistoryCleanupExecutionResult>? BatchCommitted { get; set; }
    internal Action<RepositoryWriter>? WriterAcquired { get; set; }
    public HistoryCleanupEngine(Repository repository, RetentionOptions? options = null)
    { ArgumentNullException.ThrowIfNull(repository); _repository = repository; _options = options ?? new(); _options.Validate(); }

    public HistoryCleanupPreview Preview(HistoryCleanupEvaluation evaluation, CancellationToken cancellationToken = default)
    { ValidateEvaluation(evaluation); using var reader = _repository.OpenReader(); return Preview(reader, evaluation, cancellationToken); }

    public HistoryCleanupPreview Preview(RepositoryReader reader, HistoryCleanupEvaluation evaluation, CancellationToken cancellationToken = default)
    {
        ValidateReader(reader); ValidateEvaluation(evaluation);
        var result = new HistoryCleanupPreview(reader.Authority, evaluation); var previousReads = reader.LastRetentionStatistics;
        using var scan = new HistoryCleanupScan(reader, evaluation, _options.MaximumExaminedVersions, cancellationToken);
        try
        {
            while (scan.Next(out _, out _)) { }
            result.Complete = scan.Complete; return result;
        }
        catch (OperationCanceledException error) when (Cancelled(error, cancellationToken)) { result.Cancelled = true; return result; }
        finally
        {
            result.Examined = scan.Examined; result.PathsExamined = scan.PathsExamined; result.Protected = scan.Protected;
            result.Eligible = scan.Eligible; result.AffectedPaths = scan.AffectedPaths; result.EligibleLogicalBytes = scan.EligibleLogicalBytes;
            result.BudgetExceeded = scan.BudgetExceeded;
            if (!ReferenceEquals(previousReads, reader.LastRetentionStatistics)) result.Reads = reader.LastRetentionStatistics;
        }
    }

    public HistoryCleanupCandidatePage GetCandidates(RepositoryReader reader, HistoryCleanupEvaluation evaluation, int pageSize = 128,
        HistoryCleanupCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        ValidateReader(reader); ValidateEvaluation(evaluation);
        if (pageSize is < 1 or > MutationBatch.MaximumChanges) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (cursor is not null && (cursor.Authority != reader.Authority || cursor.Evaluation != evaluation || cursor.PageSize != pageSize ||
            cursor.Bytes != _options.MaximumCandidateBytes || cursor.Work != _options.MaximumExaminedVersions))
            throw new InvalidOperationException("History Cleanup cursor does not match authority, evaluation or query bounds.");
        using var scan = new HistoryCleanupScan(reader, evaluation, _options.MaximumExaminedVersions, cancellationToken, cursor?.Position);
        var items = new List<RetentionVersion>(); long bytes = 0; var position = scan.Position; bool cancelled = false;
        try
        {
            while (true)
            {
                var before = scan.Position;
                if (!scan.Next(out var version, out var eligible)) { position = scan.Position; break; }
                if (eligible)
                {
                    var charge = RetentionOptions.Charge(version);
                    if (charge > _options.MaximumCandidateBytes - bytes) { position = before; break; }
                    items.Add(version); bytes += charge;
                }
                position = scan.Position;
                if (items.Count == pageSize) break;
            }
        }
        catch (OperationCanceledException error) when (Cancelled(error, cancellationToken)) { cancelled = true; position = scan.Position; }
        var complete = scan.Complete && !cancelled;
        return new(items.AsReadOnly(), complete ? null : position.After is not null ? new(reader.Authority, evaluation, pageSize, _options, position) : cursor,
            complete, cancelled, scan.BudgetExceeded, scan.Examined, bytes);
    }

    public HistoryCleanupExecutionResult Execute(HistoryCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.Complete || preview.Cancelled || preview.BudgetExceeded) throw new InvalidOperationException("A complete History Cleanup preview is required.");
        ValidateEvaluation(preview.Evaluation);
        using var writer = _repository.OpenWriter(_options.WorkspaceRoot, streamMetadataReads: true);
        WriterAcquired?.Invoke(writer);
        using var reader = writer.OpenOwnedReader();
        if (reader.Authority != preview.Authority) throw new StaleHistoryCleanupPreviewException(preview.Authority, reader.Authority);
        ValidateEvaluation(preview.Evaluation);
        var result = new HistoryCleanupExecutionResult(preview, reader.Authority); var previousReads = reader.LastRetentionStatistics;
        using var scan = new HistoryCleanupScan(reader, preview.Evaluation, _options.MaximumExaminedVersions, cancellationToken);
        var batch = new List<VersionIdentity>(); long bytes = 0, logicalBytes = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); result.HistoryScans = 1;
            while (scan.Next(out var version, out var eligible))
            {
                if (!eligible) continue;
                var charge = RetentionOptions.Charge(version);
                if (charge > _options.MaximumCandidateBytes - bytes) Flush();
                var nextLogical = checked(logicalBytes + version.Length);
                _ = checked(result.RemovedLogicalBytes + nextLogical); // Reporting must remain representable before publication.
                batch.Add(version.Identity); bytes += charge; logicalBytes = nextLogical;
                if (batch.Count == _options.BatchSize) Flush();
            }
            if (scan.BudgetExceeded) return result; // Discard the uncommitted partial batch.
            Flush(); result.Complete = true; return result;
        }
        catch (OperationCanceledException error) when (Cancelled(error, cancellationToken)) { result.Cancelled = true; return result; }
        catch (Exception error) { throw new HistoryCleanupExecutionException(result, error); }
        finally
        {
            UpdateCounts(); result.BudgetExceeded = scan.BudgetExceeded;
            if (!ReferenceEquals(previousReads, reader.LastRetentionStatistics)) result.Reads = reader.LastRetentionStatistics;
        }

        void Flush()
        {
            if (batch.Count == 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            VersionRemovalResult removal;
            try { removal = writer.RemoveVersions(batch, result.FinalConfirmedAuthority, cancellationToken: cancellationToken); }
            catch (VersionRemovalCommittedException error) { Record(error.Result); throw; }
            Record(removal); batch.Clear(); bytes = 0; logicalBytes = 0;
            UpdateCounts(); BatchCommitted?.Invoke(result); cancellationToken.ThrowIfCancellationRequested();
        }
        void UpdateCounts()
        {
            result.Examined = scan.Examined; result.PathsExamined = scan.PathsExamined; result.Protected = scan.Protected;
            result.Eligible = scan.Eligible; result.AffectedPaths = scan.AffectedPaths;
        }
        void Record(VersionRemovalResult removal)
        {
            result.FinalConfirmedAuthority = removal.Authority;
            result.Removed = checked(result.Removed + removal.Removed); result.AlreadyAbsent = checked(result.AlreadyAbsent + removal.AlreadyAbsent);
            result.RemovedLogicalBytes = checked(result.RemovedLogicalBytes + removal.RemovedLogicalBytes);
            if (removal.Removed != 0) result.BatchesCommitted++;
            result.MaximumBatchCount = Math.Max(result.MaximumBatchCount, batch.Count); result.MaximumBatchBytes = Math.Max(result.MaximumBatchBytes, bytes);
            result.Cleanup = new(checked(result.Cleanup.Inspected + removal.Cleanup.Inspected), checked(result.Cleanup.Deleted + removal.Cleanup.Deleted),
                checked(result.Cleanup.AlreadyAbsent + removal.Cleanup.AlreadyAbsent), checked(result.Cleanup.Deferred + removal.Cleanup.Deferred),
                checked(result.Cleanup.Failed + removal.Cleanup.Failed), result.Cleanup.Cancelled || removal.Cleanup.Cancelled,
                Array.AsReadOnly(result.Cleanup.Problems.Concat(removal.Cleanup.Problems).Take(128).ToArray()));
            result.VersionPagesWritten = checked(result.VersionPagesWritten + removal.VersionPagesWritten);
            result.MetadataBytesAppended = checked(result.MetadataBytesAppended + removal.MetadataBytesAppended);
        }
    }
    private static bool Cancelled(OperationCanceledException error, CancellationToken token) => token.IsCancellationRequested || error.CancellationToken.IsCancellationRequested;
    private void ValidateReader(RepositoryReader reader)
    { ArgumentNullException.ThrowIfNull(reader); if (reader.Authority.RepositoryId != _repository.Identity.RepositoryId) throw new ArgumentException("Reader belongs to another repository.", nameof(reader)); }
    private static void ValidateEvaluation(HistoryCleanupEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var valid = evaluation.EvaluationUtc is { } time ? new HistoryCleanupEvaluation(evaluation.Mode, time) : new HistoryCleanupEvaluation(evaluation.Mode);
        if (valid != evaluation) throw new ArgumentException("Invalid History Cleanup evaluation.", nameof(evaluation));
    }
}
