using System.IO.Hashing;
using System.Buffers;
using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Operational cache counters; never repository history or physical free-space claims.</summary>
public sealed class ReusablePayloadStatistics
{
    public long Lookups { get; internal set; }
    public long Hits { get; internal set; }
    public long Misses { get; internal set; }
    public long Rejected { get; internal set; }
    public long Conflicts { get; internal set; }
    public long BucketEntriesExamined { get; internal set; }
    public long GlobalEntriesExamined { get; internal set; }
    public long DescriptorBytesRead { get; internal set; }
    public long LocalValidationBytes { get; internal set; }
    public long FreshCopiesMarkedReady { get; internal set; }
    public long Adoptions { get; internal set; }
    public long ReusedPayloadBytes { get; internal set; }
    public long AdoptionPayloadCopyBytes => 0;
    public long Bound { get; internal set; }
    public long RestoredReady { get; internal set; }
    public long RetiredPublished { get; internal set; }
    public long ReadyDraftsPreserved { get; internal set; }
    public long CapacityEvictions { get; internal set; }
    public long PayloadFilesDeleted { get; internal set; }
    public long DeletedLogicalBytes { get; internal set; }
    public long CleanupDeferred { get; internal set; }
    private readonly List<string> _problems = [];
    public IReadOnlyList<string> Problems => _problems.AsReadOnly();
    internal void Problem(Exception error) { if (_problems.Count < 32) _problems.Add(error.Message[..Math.Min(512, error.Message.Length)]); }
}

/// <summary>Writer-owned exact locally validated copy. Disposing a candidate does not delete cache bytes.</summary>
public sealed class ReusablePayloadCandidate : IDisposable
{
    internal RepositoryWriter Writer { get; }
    internal ReusablePayloadDescriptor Descriptor { get; }
    internal FileStream? Input { get; private set; }
    internal bool Installed { get; }
    public Guid PayloadId => Descriptor.PayloadId;
    public long Length => Descriptor.Length;
    public XxHash64Checksum Checksum => Descriptor.Checksum;
    internal ReusablePayloadCandidate(RepositoryWriter writer, ReusablePayloadDescriptor descriptor, FileStream input, bool installed)
    { Writer = writer; Descriptor = descriptor; Input = input; Installed = installed; }
    public void Dispose() { Input?.Dispose(); Input = null; Writer.ReleaseReusableCandidate(this); }
}

/// <summary>Opaque exact maintenance target; fields cannot authorize arbitrary paths.</summary>
public sealed class ReusablePayloadEntry
{
    internal ReusablePayloadDescriptor Descriptor { get; }
    internal ReusablePayloadEntry(ReusablePayloadDescriptor descriptor) => Descriptor = descriptor;
    public Guid PayloadId => Descriptor.PayloadId;
    public long Length => Descriptor.Length;
}
public sealed record ReusablePayloadPage(IReadOnlyList<ReusablePayloadEntry> Entries, int NamesExamined, bool WorkLimitReached);
internal enum ReusablePayloadStage { Ready, Revoked, Bound, Adopted, Evicted }

public sealed partial class RepositoryWriter
{
    public const int MaximumReusableLookupEntries = 16;
    public ReusablePayloadStatistics ReusableStatistics { get; } = new();
    internal Action<ReusablePayloadStage, Guid>? ReusableObserve { get; set; }
    private readonly HashSet<ReusablePayloadCandidate> _reuseCandidates = [];
    private string ReuseRoot => Path.Combine(_repository.DirectoryPath, "cache", "reusable-payloads");
    internal string ReusableBucket(HistoryPath path)
    {
        var hash = _repository.Crypto?.Bucket(PathKeys.Current(path)) ?? Convert.ToHexString(SHA256.HashData(PathKeys.Current(path))).ToLowerInvariant();
        return Path.Combine(ReuseRoot, "ready", hash[..2], hash);
    }
    internal string ReadyName(ReusablePayloadDescriptor d) => Path.Combine(ReusableBucket(d.Path), $"{d.PayloadId:N}.ready");
    private string BoundDirectory(Guid transaction) => Path.Combine(ReuseRoot, "bound", transaction.ToString("N"));
    private string BoundName(ReusablePayloadDescriptor d) => Path.Combine(BoundDirectory(d.TransactionId), $"{d.PayloadId:N}.bound");
    private static bool CacheError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException;
    private static void SafeDirectory(string directory, bool create = false)
    {
        _ = RepositoryLayout.NormalizeDirectory(directory);
        if (create) Directory.CreateDirectory(directory);
        if (!RepositoryLayout.IsExistingDirectory(directory)) throw new DirectoryNotFoundException();
        for (var p = directory; p is not null; p = Path.GetDirectoryName(p))
            Format.Require((File.GetAttributes(p) & (FileAttributes.Encrypted | FileAttributes.Device)) == 0, "unsafe reusable directory");
    }
    private static void SafeFile(string file)
    {
        SafeDirectory(Path.GetDirectoryName(file)!); RepositoryLayout.RequireFile(file);
        Format.Require((File.GetAttributes(file) & (FileAttributes.Device | FileAttributes.Encrypted)) == 0, "unsafe reusable file");
    }
    private ReusablePayloadDescriptor ReadReusable(string path)
    {
        SafeFile(path); using var input = PayloadInput.Open(path);
        Format.Require(input.Length <= ReusablePayloadDescriptor.MaximumBytes + (_repository.IsEncrypted ? 64 : 0), "oversized optional descriptor");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        ReusableStatistics.DescriptorBytesRead += bytes.Length;
        var d = ReusablePayloadDescriptor.Decode(_repository.Crypto?.Unprotect(bytes, "reusable-descriptor") ?? bytes);
        Format.Require(d.RepositoryId == _repository.Identity.RepositoryId, "foreign optional reusable descriptor");
        return d;
    }
    private void WriteReusable(string path, ReusablePayloadDescriptor d, bool replace = false, Action? publishing = null)
    {
        SafeDirectory(Path.GetDirectoryName(path)!, create: true);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"{Guid.NewGuid():N}.writing");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = ReusablePayloadDescriptor.Encode(d); output.Write(_repository.Crypto?.Protect(bytes, "reusable-descriptor") ?? bytes); output.Flush(true); }
            if (replace) SafeFile(path);
            publishing?.Invoke();
            File.Move(temporary, path, replace);
        }
        finally { TryDelete(temporary); }
    }
    private bool ProvenReady(ReusablePayloadDescriptor d, Authority authority) =>
        d.State == ReusablePayloadState.Ready && d.TransactionId == Guid.Empty &&
        d.Origin == authority.Identity && d.OriginRootHash == authority.Address.Sha256;
    private bool InUse(Guid id) => _payloadDrafts.Any(d => d.VersionId == id) || _reuseCandidates.Any(c => c.PayloadId == id);
    private static void PruneEmpty(string directory)
    {
        try { SafeDirectory(directory); Directory.Delete(directory, recursive: false); }
        catch (Exception error) when (CacheError(error)) { }
    }
    private (string Path, bool Installed) Artifact(ReusablePayloadDescriptor d)
    {
        var staging = Path.Combine(_repository.DirectoryPath, "staging", d.StagingOwner.ToString("N"), $"{d.PayloadId:N}.part");
        var installed = _repository.GetPayloadPath(d.PayloadId);
        bool Exists(string path)
        {
            try { SafeFile(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        var a = Exists(staging); var b = Exists(installed);
        if (!a && !b) throw new FileNotFoundException("Optional reusable artifact is absent.");
        Format.Require(a != b, "reusable artifact ambiguous");
        return a ? (staging, false) : (installed, true);
    }
    /// <summary>Caller attests ordinary source handle/path stability. Storage additionally requires its exact completed draft.
    /// Optional creation failure before visibility returns false. No partial copy can become reusable.</summary>
    public bool MarkPayloadReusable(PayloadDraft draft, HistoryPath path, DateTimeOffset lastWriteUtc, XxHash64Checksum checksum)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(checksum);
        CheckDraft(draft); RequireNoCandidates();
        if (!draft.Ready || draft.Installed || draft.Prepared is not null || draft.Reusable is not null || _pending is not null)
            throw new InvalidOperationException("Only an owned, completed stable unbound draft can become reusable.");
        ModelValidation.RequireUtc(lastWriteUtc, nameof(lastWriteUtc));
        var authority = _repository.ReadAuthority();
        var d = new ReusablePayloadDescriptor(_repository.Identity.RepositoryId, path, draft.VersionId,
            Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(draft.StagingPath)!), "N"), draft.Length, lastWriteUtc,
            checksum, authority.Identity, authority.Address.Sha256, ReusablePayloadState.Ready, Guid.Empty);
        bool publishAttempted = false;
        try { SafeFile(draft.StagingPath); WriteReusable(ReadyName(d), d, publishing: () => publishAttempted = true); }
        catch (Exception error) when (CacheError(error))
        {
            if (!publishAttempted) return false;
            // A failed namespace acknowledgement is not evidence that Ready never became visible.
            // Preserve bytes on ambiguity; binding must later prove/revoke any visible authorization.
            try { if (ReadReusable(ReadyName(d)) == d) draft.Reusable = d; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (Exception probe) when (CacheError(probe)) { draft.Reusable = d; throw new IOException("Optional reuse creation outcome is ambiguous.", error); }
            if (draft.Reusable is null) throw new IOException("Optional reuse creation collided with other evidence.", error);
        }
        // No throwing optional work between visibility and assigning preservation ownership.
        draft.Reusable = d; ReusableStatistics.FreshCopiesMarkedReady++; ReusableObserve?.Invoke(ReusablePayloadStage.Ready, d.PayloadId); return true;
    }
    /// <summary>Exact bucket only (at most16 descriptors plus one overflow sentinel), followed by local xxHash64 validation.
    /// A changed authority conservatively invalidates Ready provenance; no history replay is used.</summary>
    public ReusablePayloadCandidate? FindReusablePayload(HistoryPath path, long length, DateTimeOffset lastWriteUtc, CancellationToken token = default)
    {
        Check(); RequireNoCandidates(); RequireIdle(); ArgumentNullException.ThrowIfNull(path);
        if (_reuseCandidates.Count != 0) throw new InvalidOperationException("Finish the existing writer-owned reusable candidate first.");
        ArgumentOutOfRangeException.ThrowIfNegative(length); ModelValidation.RequireUtc(lastWriteUtc, nameof(lastWriteUtc));
        var authority = _repository.ReadAuthority(); ReusableStatistics.Lookups++;
        ReusablePayloadDescriptor? selected = null; bool conflict = false;
        try
        {
            var bucket = ReusableBucket(path); SafeDirectory(bucket);
            var count = 0;
            foreach (var name in Directory.EnumerateFileSystemEntries(bucket))
            {
                token.ThrowIfCancellationRequested(); ReusableStatistics.BucketEntriesExamined++;
                if (++count > MaximumReusableLookupEntries) { conflict = true; break; }
                try
                {
                    var d = ReadReusable(name);
                    Format.Require(name == ReadyName(d), "optional descriptor name binding");
                    if (d.Path != path || d.Length != length || d.LastWriteUtc != lastWriteUtc || !ProvenReady(d, authority) || InUse(d.PayloadId))
                    { ReusableStatistics.Rejected++; continue; }
                    if (selected is not null && selected.Checksum != d.Checksum) conflict = true;
                    if (selected is null || d.PayloadId.CompareTo(selected.PayloadId) < 0) selected = d;
                }
                catch (Exception error) when (CacheError(error)) { ReusableStatistics.Rejected++; ReusableStatistics.Problem(error); }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.Rejected++; }
        if (conflict) { ReusableStatistics.Conflicts++; selected = null; }
        if (selected is not null)
        {
            FileStream? input = null;
            try
            {
                var artifact = Artifact(selected); input = PayloadInput.Open(artifact.Path);
                Format.Require(input.CanSeek && input.Length == PayloadPhysicalLength(selected.Length) &&
                    (File.GetAttributes(input.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) == 0,
                    "reusable payload length/type");
                var hash = new XxHash64(); var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                using var decrypted = _repository.Crypto is null ? null : new EncryptedPayload.Reader(_repository, selected.PayloadId, input, selected.Length, ownsInput: false);
                Stream content = (Stream?)decrypted ?? input;
                try
                {
                    int read;
                    while ((read = content.Read(buffer, 0, 128 * 1024)) != 0)
                    { ReusableStatistics.LocalValidationBytes += read; token.ThrowIfCancellationRequested(); hash.Append(buffer.AsSpan(0, read)); }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
                Format.Require(Convert.ToHexString(hash.GetCurrentHash()) == selected.Checksum.Value, "reusable payload checksum");
                var candidate = new ReusablePayloadCandidate(this, selected, input, artifact.Installed);
                _reuseCandidates.Add(candidate); ReusableStatistics.Hits++; return candidate;
            }
            catch (Exception error) when (CacheError(error)) { input?.Dispose(); ReusableStatistics.Rejected++; ReusableStatistics.Problem(error); }
            catch { input?.Dispose(); throw; }
        }
        ReusableStatistics.Misses++; return null;
    }
    public PayloadDraft AdoptReusablePayload(ReusablePayloadCandidate candidate) => TryAdoptReusablePayload(candidate)
        ?? throw new IOException("Reusable optional evidence is no longer eligible.");
    /// <summary>Decline changed/bad optional evidence without disguising required authority failures.</summary>
    public PayloadDraft? TryAdoptReusablePayload(ReusablePayloadCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate); Check(); RequireNoCandidates(); RequireIdle();
        if (candidate.Writer != this || candidate.Input is null || !_reuseCandidates.Contains(candidate) || _payloadDrafts.Count >= MutationBatch.MaximumChanges)
            throw new ArgumentException("A live candidate owned by this writer is required.", nameof(candidate));
        var authority = _repository.ReadAuthority(); // Required control, outside the optional-cache error boundary.
        ReusablePayloadDescriptor d; (string Path, bool Installed) artifact;
        try
        {
            d = ReadReusable(ReadyName(candidate.Descriptor));
            if (d != candidate.Descriptor || !ProvenReady(d, authority)) { ReusableStatistics.Rejected++; return null; }
            artifact = Artifact(d);
        }
        catch (Exception error) when (CacheError(error))
        { ReusableStatistics.Rejected++; ReusableStatistics.Problem(error); return null; }
        var draft = new PayloadDraft(this, d.PayloadId, Path.Combine(_repository.DirectoryPath, "staging", d.StagingOwner.ToString("N"), $"{d.PayloadId:N}.part"), d.Length, artifact.Installed, d);
        candidate.Dispose(); _payloadDrafts.Add(draft); ReusableStatistics.Adoptions++; ReusableStatistics.ReusedPayloadBytes += d.Length;
        ReusableObserve?.Invoke(ReusablePayloadStage.Adopted, d.PayloadId); return draft;
    }
    internal void ReleaseReusableCandidate(ReusablePayloadCandidate candidate) => _reuseCandidates.Remove(candidate);
    private void BindReusable(IReadOnlyList<PreparedVersion> versions, Authority baseline, Guid transaction)
    {
        foreach (var version in versions)
        {
            var draft = _payloadDrafts.FirstOrDefault(d => d.Prepared == version);
            if (draft?.Reusable is not { } d) continue;
            Format.Require(ProvenReady(d, baseline), "reusable predecessor changed");
            Format.Require(ReadReusable(ReadyName(d)) == d, "reusable descriptor changed before binding");
            var bound = d with { State = ReusablePayloadState.TransactionBound, TransactionId = transaction };
            SafeDirectory(BoundDirectory(transaction), create: true);
            // Atomic namespace revocation FIRST. A crash here can lose reuse, never authorize double publication.
            File.Move(ReadyName(d), BoundName(bound)); draft.Reusable = bound;
            ReusableObserve?.Invoke(ReusablePayloadStage.Revoked, d.PayloadId);
            WriteReusable(BoundName(bound), bound, replace: true);
            ReusableStatistics.Bound++; ReusableObserve?.Invoke(ReusablePayloadStage.Bound, d.PayloadId);
        }
    }
    private void FinishReusable(Guid transaction, PublicationOutcome outcome)
    {
        // Outcome was already conclusively established by normal publication/reconciliation. Cache errors cannot change it.
        try
        {
            var directory = BoundDirectory(transaction); SafeDirectory(directory);
            foreach (var name in Directory.EnumerateFileSystemEntries(directory).Take(MutationBatch.MaximumChanges))
            {
                try
                {
                    var d = ReadReusable(name);
                    if (d.State != ReusablePayloadState.TransactionBound || d.TransactionId != transaction || name != BoundName(d)) continue;
                    if (outcome == PublicationOutcome.Unpublished)
                    {
                        var authority = _repository.ReadAuthority();
                        if (d.Origin != authority.Identity || d.OriginRootHash != authority.Address.Sha256) continue;
                        _ = Artifact(d);
                        var ready = d with { State = ReusablePayloadState.Ready, TransactionId = Guid.Empty };
                        WriteReusable(ReadyName(ready), ready); File.Delete(name); ReusableStatistics.RestoredReady++;
                        foreach (var draft in _payloadDrafts.Where(p => p.VersionId == d.PayloadId)) draft.Reusable = ready;
                    }
                    else
                    {
                        File.Delete(name); ReusableStatistics.RetiredPublished++;
                        PruneEmpty(ReusableBucket(d.Path)); PruneEmpty(Path.GetDirectoryName(ReusableBucket(d.Path))!);
                    }
                }
                catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; ReusableStatistics.Problem(error); }
            }
            PruneEmpty(directory);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; }
    }
    /// <summary>Bounded optional-cache inventory. Never searches staging/versions. Malformed/unknown files are left alone.</summary>
    public ReusablePayloadPage GetReadyReusablePayloads(int maximumEntries = 128, int maximumNames = 4096)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        if (maximumEntries is < 1 or > 4096 || maximumNames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var entries = new List<ReusablePayloadEntry>(); int names = 0; bool limited = false;
        try
        {
            var ready = Path.Combine(ReuseRoot, "ready"); SafeDirectory(ready);
            foreach (var shard in Directory.EnumerateDirectories(ready))
            {
                if (!Charge()) break; SafeDirectory(shard);
                foreach (var bucket in Directory.EnumerateDirectories(shard))
                {
                    if (!Charge()) break; SafeDirectory(bucket);
                    foreach (var name in Directory.EnumerateFileSystemEntries(bucket))
                    {
                        if (!Charge()) break;
                        try
                        {
                            var d = ReadReusable(name);
                            if (d.State == ReusablePayloadState.Ready && name == ReadyName(d) && !InUse(d.PayloadId)) entries.Add(new(d));
                        }
                        catch (Exception error) when (CacheError(error)) { ReusableStatistics.Rejected++; }
                        if (entries.Count >= maximumEntries) { limited = true; break; }
                    }
                    if (limited) break;
                }
                if (limited) break;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; }
        return new(entries.OrderBy(e => e.Descriptor.Path.CanonicalPath, StringComparer.Ordinal).ThenBy(e => e.PayloadId).ToArray(), names, limited);
        bool Charge() { if (names >= maximumNames) { limited = true; return false; } names++; ReusableStatistics.GlobalEntriesExamined++; return true; }
    }
    /// <summary>Delete an exact proven Ready artifact, or retire only stale optional bookkeeping. Never deletes history.
    /// Authority advances deliberately invalidate provenance, including UUIDs absent after committed removal/compaction.</summary>
    public bool RemoveReadyReusablePayload(ReusablePayloadEntry entry, bool forCapacity = false)
    {
        ArgumentNullException.ThrowIfNull(entry); Check(); RequireNoCandidates(); RequireIdle();
        var authority = _repository.ReadAuthority();
        if (InUse(entry.PayloadId)) return false;
        ReusablePayloadDescriptor d; (string Path, bool Installed) artifact;
        try
        {
            d = ReadReusable(ReadyName(entry.Descriptor)); if (d != entry.Descriptor) return false;
            if (!ProvenReady(d, authority)) { File.Delete(ReadyName(d)); return false; }
            try { artifact = Artifact(d); }
            catch (FileNotFoundException) { File.Delete(ReadyName(d)); return false; }
            catch (InvalidDataException)
            {
                // Ambiguous/unsafe topology is not absence evidence and never authorizes deletion.
                return false;
            }
        }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; ReusableStatistics.Problem(error); return false; }
        // Required authority failures are control errors, never optional-cache misses.
        Format.Require(_repository.ReadAuthority() == authority, "reusable cleanup authority changed");
        try
        {
            RetiredPayloadFile.Delete(artifact.Path);
        }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; ReusableStatistics.Problem(error); return false; }
        ReusableStatistics.PayloadFilesDeleted++; ReusableStatistics.DeletedLogicalBytes += d.Length;
        if (forCapacity) ReusableStatistics.CapacityEvictions++;
        // Acknowledged payload deletion always requires actual capacity feedback, even if descriptor retirement fails.
        try { File.Delete(ReadyName(d)); PruneEmpty(ReusableBucket(d.Path)); PruneEmpty(Path.GetDirectoryName(ReusableBucket(d.Path))!); }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; ReusableStatistics.Problem(error); }
        ReusableObserve?.Invoke(ReusablePayloadStage.Evicted, d.PayloadId); return true;
    }
    /// <summary>Retire bounded obsolete transaction bookkeeping only. Never touches payloads or candidates.
    /// With no unresolved candidates, abandoning remaining bound evidence may lose reuse but cannot change history.</summary>
    public int CleanupReusableBookkeeping(int maximumNames = 4096)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        if (maximumNames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maximumNames));
        int examined = 0, retired = 0;
        try
        {
            var bound = Path.Combine(ReuseRoot, "bound"); SafeDirectory(bound);
            foreach (var directory in Directory.EnumerateDirectories(bound))
            {
                if (++examined > maximumNames) break; SafeDirectory(directory);
                foreach (var name in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++examined > maximumNames) break;
                    try
                    {
                        var d = ReadReusable(name);
                        if (d.State != ReusablePayloadState.TransactionBound || name != BoundName(d)) continue;
                        File.Delete(name); retired++;
                    }
                    catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; }
                }
                PruneEmpty(directory); if (examined > maximumNames) break;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (CacheError(error)) { ReusableStatistics.CleanupDeferred++; }
        return retired;
    }
}
