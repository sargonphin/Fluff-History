using System.ComponentModel;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed class PayloadCleanupCursor
{
    internal AuthorityIdentity Authority { get; }
    internal FrameAddress Next { get; }
    internal PayloadCleanupCursor(AuthorityIdentity authority, FrameAddress next) { Authority = authority; Next = next; }
}
public sealed record PayloadCleanupPage(PayloadCleanupResult Result, long RecordsRead, PayloadCleanupCursor? Continuation);
internal enum PayloadCleanupStage { BeforeDelete, Validated, Deleted, DeleteAttempt }
internal sealed class PayloadCleanupInterruptedException(PayloadCleanupResult result, Exception cause) : IOException("Cleanup interrupted.", cause)
{ internal PayloadCleanupResult Result { get; } = result; }

public sealed partial class RepositoryWriter
{
    internal Action<PayloadCleanupStage, VersionIdentity>? PayloadCleanupObserve { get; set; }
    /// <summary>Stream committed cleanup declarations only. Continuations are transient and bind exact authority.</summary>
    public PayloadCleanupPage CleanupRetiredPayloads(PayloadCleanupLimits? limits = null, PayloadCleanupCursor? continuation = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new();
        if (limits.MaximumRecords <= 0 || limits.MaximumPayloads is < 1 or > MutationBatch.MaximumChanges) throw new ArgumentOutOfRangeException(nameof(limits));
        try
        {
            Check(); RequireNoCandidates(); RequireIdle(); using var reader = _repository.OpenReader();
            if (continuation is not null && continuation.Authority != reader.Authority) throw new InvalidOperationException("Stale payload-cleanup continuation.");
            var cursor = continuation?.Next ?? reader.Captured.Root.Journal; long records = 0; int payloads = 0;
            var result = CleanupIdentities(reader, Identities(), cancellationToken);
            return new(result, records, cursor is null ? null : new(reader.Authority, cursor));
            IEnumerable<VersionIdentity> Identities()
            {
                while (cursor is not null && records < limits.MaximumRecords && payloads < limits.MaximumPayloads)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var record = reader.Semantic(cursor); records++;
                    if (record.Kind == SemanticKind.VersionRemoved)
                    {
                        // Advance only once this identity has actually been attempted, including cancellation races.
                        payloads++; yield return record.RemovedVersion!;
                    }
                    cursor = record.Previous;
                }
            }
        }
        catch { _aborted = true; throw; }
    }
    private void RequireIdle()
    {
        if (_pending is not null) throw new InvalidOperationException("Finish the pending publication first.");
    }
    private PayloadCleanupResult CleanupIdentities(RepositoryReader reader, IEnumerable<VersionIdentity> identities, CancellationToken token, bool reportInterrupted = false)
    {
        long inspected = 0, deleted = 0, absent = 0, deferred = 0, failed = 0; bool cancelled = false;
        var problems = new List<PayloadCleanupProblem>();
        try
        {
            foreach (var identity in identities)
            {
                token.ThrowIfCancellationRequested(); Check(); RequireNoCandidates();
                Format.Require(_repository.ReadAuthority() == reader.Captured, "payload cleanup authority changed");
                Format.Require(reader.VersionIndex.Find(reader.Captured.Root.Versions, PathKeys.Version(identity)) is null &&
                    reader.GetCurrent(identity.Path)?.Identity.VersionId != identity.VersionId, "payload cleanup targets surviving/current version");
                PayloadCleanupObserve?.Invoke(PayloadCleanupStage.BeforeDelete, identity);
                Check(); Format.Require(_repository.ReadAuthority() == reader.Captured, "payload cleanup predecessor changed");
                inspected++; string path; FileAttributes attributes;
                try { path = _repository.GetPayloadPath(identity.VersionId); attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { absent++; continue; }
                catch (DirectoryNotFoundException) { absent++; continue; }
                catch (UnauthorizedAccessException error)
                { deferred++; if (problems.Count < 128) problems.Add(new(identity, error.Message)); continue; }
                Format.Require((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) == 0, "unsafe retired payload type");
                PayloadCleanupObserve?.Invoke(PayloadCleanupStage.Validated, identity);
                try
                {
                    PayloadCleanupObserve?.Invoke(PayloadCleanupStage.DeleteAttempt, identity);
                    RetiredPayloadFile.Delete(path); deleted++; PayloadCleanupObserve?.Invoke(PayloadCleanupStage.Deleted, identity);
                }
                catch (FileNotFoundException)
                {
                    Format.Require(PayloadConclusivelyAbsent(identity), "retired payload disappeared without safe absence"); absent++;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException)
                {
                    var code = error.InnerException is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;
                    if (error is UnauthorizedAccessException || code is 1 or 5 or 13 or 32 or 33) deferred++; else failed++;
                    if (problems.Count < 128) problems.Add(new(identity, error.Message));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
        catch (Exception error) when (reportInterrupted)
        { throw new PayloadCleanupInterruptedException(new(inspected, deleted, absent, deferred, failed, false, problems.AsReadOnly()), error); }
        return new(inspected, deleted, absent, deferred, failed, cancelled, problems.AsReadOnly());
    }
    private bool PayloadConclusivelyAbsent(VersionIdentity identity)
    {
        Check(); // Repository/control failure is never an absence observation.
        try { _ = File.GetAttributes(_repository.GetPayloadPath(identity.VersionId)); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
