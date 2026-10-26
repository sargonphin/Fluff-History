using FluffHistory.Core;

namespace FluffHistory.Storage;

internal enum PublicationStage { MetadataFlushed, CandidateFlushed, BeforeReplace, AfterReplace, LocalPrepared, BeforeMetadataInstall }

/// <summary>One retained exclusive OS handle; bounded batches publish independently. Any failed mutation aborts the session.</summary>
public sealed partial class RepositoryWriter : IDisposable
{
    private readonly Repository _repository;
    private readonly FileStream _handle;
    private readonly byte[]? _networkOwnershipBuffer;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly HashSet<PreparedVersion> _prepared = [];
    private readonly string? _workspaceRoot;
    private readonly bool _streamMetadataReads;
    private PreparedMutation? _pending;
    internal PreparedMutation? PendingPublication => _pending;
    private bool _aborted;
    private bool _disposed;
    internal Action<PublicationStage>? Observe { get; set; }
    internal long RotationBytes { get; set; } = Format.MaximumSegmentBytes;
    internal IoStatistics? LastWriteStatistics { get; private set; }
    /// <summary>Encoded metadata bytes appended by the last prepared publication; excludes payload/workspace bytes.</summary>
    public long LastPublicationMetadataBytes => LastWriteStatistics?.PhysicalBytesWritten ?? 0;
    internal HistoryFoldStatistics? LastHistoryFoldStatistics { get; private set; }
    internal int HistoryFoldBufferEntries { get; set; } = 4096;
    // Only pure local compaction work is batched; publication and absence proofs
    // retain their independent ownership/predecessor checks.
    internal int LocalCompactionCheckInterval { get; set; } = 4096;
    internal RepositoryWriter(Repository repository, string? workspaceRoot = null, bool streamMetadataReads = false)
    {
        _repository = repository; _workspaceRoot = workspaceRoot; _streamMetadataReads = streamMetadataReads;
        if (WindowsFileSystem.IsNetwork(repository.DirectoryPath)) _networkOwnershipBuffer = new byte[RepositoryWriterLock.ProbeBufferBytes];
        _handle = RepositoryWriterLock.Open(repository.DirectoryPath);
        try { Check(); } catch { _handle.Dispose(); throw; }
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_aborted) throw new InvalidOperationException("Writer session was aborted; reopen and reconcile explicitly.");
        try
        {
            RepositoryWriterLock.Check(_handle);
            if (_networkOwnershipBuffer is not null) RepositoryWriterLock.ProbeServerHandle(_handle, _networkOwnershipBuffer);
            _ = RepositoryLayout.NormalizeDirectory(_repository.DirectoryPath);
            _repository.CheckBootstrap();
        }
        catch { _aborted = true; throw; }
    }
    internal void LoseHandleForTest() => _handle.Dispose();
    /// <summary>Capture read authority under retained ownership, refusing unresolved or pending publication.</summary>
    public RepositoryReader OpenOwnedReader()
    { Check(); RequireNoCandidates(); RequireIdle(); return _repository.OpenReader(); }
    public PreparedVersion PrepareVersion(LogicalFileMetadata metadata, DateTimeOffset storedUtc, XxHash64Checksum checksum)
    {
        if (_repository.IsEncrypted) throw new InvalidOperationException("Encrypted versions require an owned CreatePayloadDraft one-pass copy.");
        Check(); if (_pending is not null) throw new InvalidOperationException("Finish the pending publication first.");
        if (_prepared.Count >= MutationBatch.MaximumChanges) throw new InvalidOperationException("At most 4096 prepared versions may be outstanding per session.");
        var prepared = new PreparedVersion(_owner, Guid.NewGuid(), metadata, storedUtc, checksum);
        if (File.Exists(_repository.GetPayloadPath(prepared.VersionId))) throw new IOException("Generated payload collision.");
        _prepared.Add(prepared); return prepared;
    }
    public string GetPreparedPayloadPath(PreparedVersion prepared)
    { CheckPrepared(prepared); return _repository.GetPayloadPath(prepared.VersionId); }
    private void CheckPrepared(PreparedVersion prepared)
    {
        Check(); ValidatePrepared(prepared);
    }
    private void ValidatePrepared(PreparedVersion prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Owner != _owner || !_prepared.Contains(prepared)) throw new ArgumentException("Version belongs to another or completed session.", nameof(prepared));
    }
    private void RequireNoCandidates()
    {
        if (Directory.EnumerateFileSystemEntries(_repository.DirectoryPath, "state.*.tmp").Any())
            throw new IOException("An unresolved state candidate exists. Reconcile explicitly before mutation.");
    }
    public MutationResult Apply(MutationBatch batch, AuthorityIdentity expected, CancellationToken cancellationToken = default)
    {
        using var prepared = PrepareMutation(batch, expected, cancellationToken);
        return prepared.Commit(cancellationToken);
    }
    /// <summary>Construct metadata locally and stage non-authoritative control/append data. Payloads need not yet exist.</summary>
    public PreparedMutation PrepareMutation(MutationBatch batch, AuthorityIdentity expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch); MetadataWorkspace? workspace = null;
        try
        {
            Check(); RequireNoCandidates();
            if (_pending is not null) throw new InvalidOperationException("Finish the pending publication first.");
            cancellationToken.ThrowIfCancellationRequested(); LastWriteStatistics = null;
            workspace = new(_repository, _workspaceRoot, cancellationToken, streamMetadataReads: _streamMetadataReads);
            using var baseline = _repository.OpenWorkingReader(workspace);
            if (baseline.Authority != expected) throw new InvalidOperationException("Stale expected authority.");
            var roots = batch.SourceRoots.Where(s => !s.Equals(baseline.GetSourceRoot(s.SourceRootId))).ToArray();
            var removes = batch.CurrentRemovals.Where(p => baseline.GetCurrent(p) is not null).ToArray();
            var upserts = new List<(HistoryPath Path, FrameAddress Address, StoredVersion Version)>();
            foreach (var id in batch.CurrentUpserts)
            {
                var latest = baseline.GetNewestVersion(id.Path);
                if (latest?.Identity != id) throw new ArgumentException("Current upsert must reference the exact newest successful version.", nameof(batch));
                if (baseline.GetCurrent(id.Path)?.Identity != id)
                    upserts.Add((id.Path, baseline.VersionIndex.Find(baseline.Captured.Root.Versions, PathKeys.Version(id))!, latest));
            }
            foreach (var version in batch.Versions) { ValidatePrepared(version); RequireAllowed(version.Path, version.Metadata.Length); }
            foreach (var upsert in upserts) RequireAllowed(upsert.Path, upsert.Version.Metadata.Length);
            var replaced = batch.Versions.Select(v => v.Path).Concat(removes).ToHashSet();
            foreach (var sourceRoot in roots)
                foreach (var entry in baseline.EnumerateCurrent(sourceRoot.SourceRootId))
                    if (!replaced.Contains(entry.Identity.Path)) RequireAllowed(entry.Identity.Path, entry.Metadata.Length);
            if (roots.Length + removes.Length + upserts.Count + batch.Versions.Count == 0)
            {
                _pending = new(this, baseline.Captured, baseline.Captured, workspace, true, [], null); return _pending;
            }
            var sequence = checked(baseline.Captured.Root.Sequence + 1);
            using var append = workspace.CreateAppender(baseline.Captured, baseline.Captured.Root.Epoch, RotationBytes);
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? baseline.Read(p);
            var currentIndex = new OrderedIndex(IndexKind.Current, Read, append);
            var versionsIndex = new OrderedIndex(IndexKind.Versions, Read, append);
            var sourcesIndex = new OrderedIndex(IndexKind.Sources, Read, append);
            var journal = baseline.Captured.Root.Journal;
            FrameAddress Semantic(SemanticRecord record)
            { journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(record with { Previous = journal })); return journal; }
            var current = new List<(byte[], FrameAddress?)>(); var versions = new List<(byte[], FrameAddress?)>(); var sources = new List<(byte[], FrameAddress?)>();
            foreach (var source in roots)
                sources.Add((PathKeys.Source(source.SourceRootId), Semantic(new(SemanticKind.SourceRoot, null, Source: source))));
            for (var i = 0; i < batch.Versions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var prepared = batch.Versions[i];
                var version = new StoredVersion(new(prepared.Path, sequence, i, prepared.VersionId), prepared.Metadata, prepared.StoredUtc, prepared.Checksum);
                var pointer = Semantic(new(SemanticKind.VersionAdded, null, Version: version)); versions.Add((PathKeys.Version(version.Identity), pointer));
                Semantic(new(SemanticKind.CurrentUpsert, null, Path: prepared.Path, VersionAddress: pointer)); current.Add((PathKeys.Current(prepared.Path), pointer));
            }
            foreach (var upsert in upserts)
            {
                Semantic(new(SemanticKind.CurrentUpsert, null, Path: upsert.Path, VersionAddress: upsert.Address)); current.Add((PathKeys.Current(upsert.Path), upsert.Address));
            }
            foreach (var path in removes) { Semantic(new(SemanticKind.CurrentRemoved, null, Path: path)); current.Add((PathKeys.Current(path), null)); }
            var prior = baseline.Captured.Root;
            var root = NextRoot(baseline.Captured, sequence, prior.Epoch, journal, currentIndex.Update(prior.Current, current),
                versionsIndex.Update(prior.Versions, versions), sourcesIndex.Update(prior.Sources, sources), prior.Retired);
            return StagePublication(baseline.Captured, root, append, workspace, true, batch.Versions, Confirm, cancellationToken);
            void Confirm(RepositoryReader reader)
            {
                foreach (var version in batch.Versions)
                    Format.Require(reader.GetCurrent(version.Path)?.Identity.VersionId == version.VersionId &&
                        reader.GetNewestVersion(version.Path)?.Identity.VersionId == version.VersionId, "new version/current confirmation");
                foreach (var upsert in upserts) Format.Require(reader.GetCurrent(upsert.Path) == upsert.Version, "membership confirmation");
                foreach (var path in removes) Format.Require(reader.GetCurrent(path) is null, "removal confirmation");
                foreach (var source in roots) Format.Require(source.Equals(reader.GetSourceRoot(source.SourceRootId)), "configuration confirmation");
            }
            void RequireAllowed(HistoryPath path, long length)
            {
                var source = batch.SourceRoots.FirstOrDefault(s => s.SourceRootId == path.SourceRootId) ?? baseline.GetSourceRoot(path.SourceRootId);
                Format.Require(source is not null && source.IsProtected && length <= source.MaximumFileSize &&
                    !source.ExcludedDirectories.Any(e => path.CanonicalPath.StartsWith(e + "/", StringComparison.Ordinal)), "current entry outside protection configuration");
            }
        }
        catch
        {
            _aborted = true; CleanupTransfers(_pending); workspace?.Dispose(); throw;
        }
    }
    private static MetadataRoot NextRoot(Authority baseline, long sequence, Guid epoch, FrameAddress? journal, FrameAddress? current, FrameAddress? versions, FrameAddress? sources, FrameAddress? retired) =>
        new(checked(baseline.Root.Generation + 1), sequence, Guid.NewGuid(), epoch, baseline.Root.Generation, baseline.Root.TransactionId, baseline.Address.Sha256, journal, current, versions, sources, retired);
    private Authority ReplaceAndConfirm(PreparedMutation prepared)
    {
        try
        {
            AuthorityFiles.Replace(_repository.DirectoryPath, prepared.CandidateName);
            Observe?.Invoke(PublicationStage.AfterReplace);
            Check(); Format.Require(_repository.ReadAuthority() == prepared.Intended, "publication confirmation"); return prepared.Intended;
        }
        catch (Exception error)
        {
            try
            {
                Check(); var found = _repository.ReadAuthority();
                if (found == prepared.Intended) return prepared.Intended;
                if (found == prepared.Baseline) throw new UnpublishedException(error);
            }
            catch (UnpublishedException e) { throw new IOException("V4 mutation was not published; candidate retained for reconciliation.", e.InnerException); }
            catch (Exception reconciliation) { throw new PublicationUncertainException(prepared.Intended.Identity, new AggregateException(error, reconciliation)); }
            throw new PublicationUncertainException(prepared.Intended.Identity, error);
        }
    }
    private sealed class UnpublishedException(Exception cause) : Exception("Unpublished", cause);
    public IEnumerable<Guid> EnumeratePendingPublications()
    {
        Check();
        foreach (var path in Directory.EnumerateFileSystemEntries(_repository.DirectoryPath, "state.*.tmp"))
        {
            var name = Path.GetFileName(path);
            Format.Require(name.Length == 42 && Guid.TryParseExact(name.AsSpan(6, 32), "N", out _), "candidate filename");
            yield return Guid.ParseExact(name.AsSpan(6, 32), "N");
        }
    }
    public PublicationOutcome Reconcile(Guid transactionId)
    {
        Check(); var current = _repository.ReadAuthority(); var name = "state." + transactionId.ToString("N") + ".tmp";
        if (current.Root.TransactionId == transactionId)
        {
            var candidatePath = Path.Combine(_repository.DirectoryPath, name);
            try { RepositoryLayout.RequireFile(candidatePath); }
            catch (FileNotFoundException) { FinishReusable(transactionId, PublicationOutcome.Published); return PublicationOutcome.Published; }
            Format.Require(ReadCandidate(name, transactionId) == current, "conflicting published candidate");
            File.Delete(candidatePath); TryDelete(Path.Combine(_repository.DirectoryPath, $"prepare.{transactionId:N}.root"));
            FinishReusable(transactionId, PublicationOutcome.Published);
            return PublicationOutcome.Published;
        }
        var candidate = ReadCandidate(name, transactionId);
        Format.Require(candidate.Root.TransactionId == transactionId && candidate.Root.PreviousGeneration == current.Root.Generation &&
            candidate.Root.PreviousTransactionId == current.Root.TransactionId && candidate.Root.PreviousRootHash == current.Address.Sha256, "ambiguous candidate predecessor");
        File.Delete(Path.Combine(_repository.DirectoryPath, name)); TryDelete(Path.Combine(_repository.DirectoryPath, $"prepare.{transactionId:N}.root"));
        FinishReusable(transactionId, PublicationOutcome.Unpublished); return PublicationOutcome.Unpublished;
    }
    private Authority ReadCandidate(string name, Guid transactionId)
    {
        var control = $"prepare.{transactionId:N}.root";
        return File.Exists(Path.Combine(_repository.DirectoryPath, control))
            ? AuthorityFiles.ReadPrepared(_repository.DirectoryPath, _repository.Identity, name, control, _repository.Crypto)
            : AuthorityFiles.Read(_repository.DirectoryPath, _repository.Identity, name, _repository.Crypto);
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_pending is not null) Discard(_pending);
        foreach (var candidate in _reuseCandidates.ToArray()) candidate.Dispose();
        foreach (var draft in _payloadDrafts.ToArray()) draft.Dispose();
        CleanupPayloadDirectories();
        _disposed = true; _handle.Dispose();
    }
}
