using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>Fixed-age policy only. All durable removal/cleanup belongs to Storage.</summary>
public sealed class RetentionEngine
{
    private readonly Repository _repository;
    private readonly RetentionOptions _options;
    internal Action<RetentionExecutionResult>? BatchCommitted { get; set; }
    internal Action<RepositoryWriter>? WriterAcquired { get; set; }
    public RetentionEngine(Repository repository, RetentionOptions? options = null)
    { ArgumentNullException.ThrowIfNull(repository); _repository = repository; _options = options ?? new(); _options.Validate(); }

    public RetentionPreview Preview(RetentionPolicy policy, DateTimeOffset evaluationUtc, CancellationToken cancellationToken = default)
    {
        var evaluation = new RetentionEvaluation(policy, evaluationUtc);
        using var reader = _repository.OpenReader(); return Preview(reader, evaluation, cancellationToken);
    }
    public RetentionPreview Preview(RepositoryReader reader, RetentionEvaluation evaluation, CancellationToken cancellationToken = default)
    {
        ValidateReader(reader); ValidateEvaluation(evaluation);
        var result = new RetentionPreview(reader.Authority, evaluation);
        var previousReads = reader.LastRetentionStatistics;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evaluation.Policy == RetentionPolicy.Forever) { result.Complete = true; return result; }
            foreach (var version in reader.EnumerateRetentionVersions(cancellationToken: cancellationToken))
            {
                result.Examined++; if (version.IsCurrent) result.Protected++;
                if (Eligible(version, evaluation)) { result.Eligible++; result.EligibleLogicalBytes = checked(result.EligibleLogicalBytes + version.Length); }
                if (result.Examined >= _options.MaximumExaminedVersions) { result.BudgetExceeded = true; return result; }
            }
            result.Complete = true; return result;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
        { result.Cancelled = true; return result; }
        finally
        {
            if (evaluation.Policy != RetentionPolicy.Forever && !ReferenceEquals(previousReads, reader.LastRetentionStatistics))
                result.Reads = reader.LastRetentionStatistics;
        }
    }

    public RetentionCandidatePage GetCandidates(RepositoryReader reader, RetentionEvaluation evaluation, int pageSize = 128,
        RetentionCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        ValidateReader(reader); ValidateEvaluation(evaluation);
        if (pageSize is < 1 or > MutationBatch.MaximumChanges) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (cursor is not null && (cursor.Authority != reader.Authority || cursor.Evaluation != evaluation ||
            cursor.PageSize != pageSize || cursor.Bytes != _options.MaximumCandidateBytes))
            throw new InvalidOperationException("Retention cursor does not match authority, evaluation or query bounds.");
        var items = new List<RetentionVersion>(); long examined = 0, bytes = 0; VersionIdentity? after = cursor?.After;
        bool complete = false, cancelled = false, budget = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evaluation.Policy == RetentionPolicy.Forever) complete = true;
            else
            {
                using var versions = reader.EnumerateRetentionVersions(after, cancellationToken).GetEnumerator();
                while (true)
                {
                    if (!versions.MoveNext()) { complete = true; break; }
                    var version = versions.Current; examined++;
                    if (Eligible(version, evaluation))
                    {
                        var charge = RetentionOptions.Charge(version);
                        if (charge > _options.MaximumCandidateBytes - bytes) break; // Retry this exact next record on continuation.
                        items.Add(version); bytes += charge;
                    }
                    after = version.Identity;
                    if (examined >= _options.MaximumExaminedVersions) { budget = true; break; }
                    if (items.Count == pageSize) break;
                }
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested || error.CancellationToken.IsCancellationRequested) { cancelled = true; }
        return new(items.AsReadOnly(), complete ? null : after is not null ? new(reader.Authority, evaluation, after, pageSize, _options.MaximumCandidateBytes) : cursor,
            complete, cancelled, budget, examined, bytes);
    }

    public RetentionExecutionResult Execute(RetentionPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.Complete || preview.Cancelled || preview.BudgetExceeded) throw new InvalidOperationException("A complete retention preview is required.");
        ValidateEvaluation(preview.Evaluation);
        using var writer = _repository.OpenWriter(_options.WorkspaceRoot, streamMetadataReads: true);
        WriterAcquired?.Invoke(writer);
        using var reader = writer.OpenOwnedReader();
        if (reader.Authority != preview.Authority) throw new StaleRetentionPreviewException(preview.Authority, reader.Authority);
        ValidateEvaluation(preview.Evaluation);
        var result = new RetentionExecutionResult(preview, reader.Authority);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.Evaluation.Policy == RetentionPolicy.Forever) { result.Complete = true; return result; }
            result.AgeScans = 1; var batch = new List<VersionIdentity>(); long bytes = 0;
            foreach (var version in reader.EnumerateRetentionVersions(cancellationToken: cancellationToken))
            {
                result.Examined++; if (version.IsCurrent) result.Protected++;
                if (Eligible(version, preview.Evaluation))
                {
                    result.Eligible++; var charge = RetentionOptions.Charge(version);
                    if (charge > _options.MaximumCandidateBytes - bytes) Flush();
                    batch.Add(version.Identity); bytes += charge;
                    if (batch.Count == _options.BatchSize) Flush();
                }
                if (result.Examined >= _options.MaximumExaminedVersions) { result.BudgetExceeded = true; return result; }
            }
            Flush(); result.Complete = true; return result;
            void Flush()
            {
                if (batch.Count == 0) return;
                cancellationToken.ThrowIfCancellationRequested();
                VersionRemovalResult removal;
                try { removal = writer.RemoveVersions(batch, result.FinalConfirmedAuthority, cancellationToken: cancellationToken); }
                catch (VersionRemovalCommittedException error) { Record(error.Result); throw; }
                Record(removal);
                batch.Clear(); bytes = 0; BatchCommitted?.Invoke(result);
                cancellationToken.ThrowIfCancellationRequested();
            }
            void Record(VersionRemovalResult removal)
            {
                result.FinalConfirmedAuthority = removal.Authority;
                result.Removed = checked(result.Removed + removal.Removed); result.AlreadyAbsent = checked(result.AlreadyAbsent + removal.AlreadyAbsent);
                result.RemovedLogicalBytes = checked(result.RemovedLogicalBytes + removal.RemovedLogicalBytes);
                if (removal.Removed != 0) result.BatchesCommitted++;
                result.MaximumBatchCount = Math.Max(result.MaximumBatchCount, batch.Count); result.MaximumBatchBytes = Math.Max(result.MaximumBatchBytes, bytes);
                result.Cleanup = AddCleanup(result.Cleanup, removal.Cleanup);
                result.VersionPagesWritten = checked(result.VersionPagesWritten + removal.VersionPagesWritten);
                result.MetadataBytesAppended = checked(result.MetadataBytesAppended + removal.MetadataBytesAppended);
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
        { result.Cancelled = true; return result; }
        catch (Exception error) { throw new RetentionExecutionException(result, error); }
        finally { if (result.AgeScans != 0) result.Reads = reader.LastRetentionStatistics; }
    }
    private static PayloadCleanupResult AddCleanup(PayloadCleanupResult a, PayloadCleanupResult b) => new(
        checked(a.Inspected + b.Inspected), checked(a.Deleted + b.Deleted), checked(a.AlreadyAbsent + b.AlreadyAbsent),
        checked(a.Deferred + b.Deferred), checked(a.Failed + b.Failed), a.Cancelled || b.Cancelled,
        Array.AsReadOnly(a.Problems.Concat(b.Problems).Take(128).ToArray()));
    private void ValidateReader(RepositoryReader reader)
    { ArgumentNullException.ThrowIfNull(reader); if (reader.Authority.RepositoryId != _repository.Identity.RepositoryId) throw new ArgumentException("Reader belongs to another repository.", nameof(reader)); }
    private static void ValidateEvaluation(RetentionEvaluation evaluation)
    { ArgumentNullException.ThrowIfNull(evaluation); if (new RetentionEvaluation(evaluation.Policy, evaluation.EvaluationUtc) != evaluation) throw new ArgumentException("Invalid evaluation.", nameof(evaluation)); }
    private static bool Eligible(RetentionVersion version, RetentionEvaluation evaluation) => !version.IsCurrent && version.StoredUtc < evaluation.CutoffUtc;
}
