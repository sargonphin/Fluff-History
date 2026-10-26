using FluffHistory.Core;

namespace FluffHistory.Storage;

internal enum PayloadStage { Created, BeforeWrite, BeforeFlush, BeforeInstall, Installed }

/// <summary>One generated, session-owned write-only payload. Disposal never removes an installed immutable file.</summary>
public sealed class PayloadDraft : IDisposable
{
    private readonly RepositoryWriter _writer;
    private FileStream? _stream;
    private EncryptedPayload.Writer? _encrypted;
    private bool _disposed;
    internal string StagingPath { get; }
    internal bool Ready { get; private set; }
    internal bool Installed { get; private set; }
    internal PreparedVersion? Prepared { get; private set; }
    internal long Length { get; private set; }
    internal ReusablePayloadDescriptor? Reusable { get; set; }
    public Guid VersionId { get; }
    internal PayloadDraft(RepositoryWriter writer, Guid id, string path, FileStream stream)
    { _writer = writer; VersionId = id; StagingPath = path; _stream = stream; _encrypted = writer.EncryptDraft(id, stream); }
    internal PayloadDraft(RepositoryWriter writer, Guid id, string path, long length, bool installed, ReusablePayloadDescriptor reusable)
    { _writer = writer; VersionId = id; StagingPath = path; Length = length; Ready = true; Installed = installed; Reusable = reusable; }
    /// <summary>Write-only stream. CompleteCopy requests disk flush and closes it before publication.</summary>
    public Stream Content
    {
        get { _writer.CheckDraft(this); ObjectDisposedException.ThrowIf(_disposed, this); return (Stream?)_encrypted ?? _stream ?? throw new InvalidOperationException("Copy is complete."); }
    }
    /// <summary>Write copied bytes once; a destination write failure aborts this writer session.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null) throw new InvalidOperationException("Copy is complete.");
            _writer.PayloadObserve?.Invoke(PayloadStage.BeforeWrite, this); ((Stream?)_encrypted ?? _stream).Write(bytes);
        }
        catch { _writer.AbortPayload(); throw; }
    }
    public void CompleteCopy(long expectedLength)
    {
        try
        {
            _writer.CheckDraft(this); ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null || Ready) throw new InvalidOperationException("Copy already completed.");
            ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
            _writer.PayloadObserve?.Invoke(PayloadStage.BeforeFlush, this);
            _encrypted?.Complete(expectedLength);
            _stream.Flush(true); Format.Require(_stream.Length == _writer.PayloadPhysicalLength(expectedLength), "staging payload length");
            _encrypted?.Dispose(); _encrypted = null;
            _stream.Dispose(); _stream = null; RepositoryLayout.RequireFile(StagingPath);
            Format.Require(new FileInfo(StagingPath).Length == _writer.PayloadPhysicalLength(expectedLength), "staging payload length");
            Length = expectedLength; Ready = true;
        }
        catch { _writer.AbortPayload(); throw; }
    }
    /// <summary>Bind successful metadata/checksum and this draft's generated identity to a batch protection time.</summary>
    public PreparedVersion PrepareVersion(LogicalFileMetadata metadata, DateTimeOffset storedUtc, XxHash64Checksum checksum)
    {
        _writer.CheckDraft(this); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Ready || Prepared is not null || metadata.Length != Length) throw new InvalidOperationException("A completed, unbound copy is required.");
        return Prepared = _writer.PrepareDraft(this, metadata, storedUtc, checksum);
    }
    /// <summary>After metadata preparation, move the flushed payload into its immutable final name without overwrite.</summary>
    public void Install()
    {
        try
        {
            _writer.CheckDraft(this); ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Ready || Prepared is null) throw new InvalidOperationException("Draft is not ready for installation.");
            _writer.InstallDraft(this); Installed = true;
        }
        catch { _writer.AbortPayload(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _encrypted?.Dispose(); _stream?.Dispose(); }
        finally
        {
            try
            {
                if (!Installed && Reusable is null) File.Delete(StagingPath);
                if (Reusable?.State == ReusablePayloadState.Ready) _writer.ReusableStatistics.ReadyDraftsPreserved++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _writer.ReleaseDraft(this);
        }
    }
}

public sealed partial class RepositoryWriter
{
    internal EncryptedPayload.Writer? EncryptDraft(Guid id, Stream stream) => _repository.Crypto is null ? null : new(_repository, id, stream);
    internal long PayloadPhysicalLength(long logical) => _repository.EstimatePayloadPhysicalBytes(logical);
    private readonly HashSet<PayloadDraft> _payloadDrafts = [];
    private string? _payloadDirectory;
    private bool _createdStagingRoot;
    internal Action<PayloadStage, PayloadDraft>? PayloadObserve { get; set; }
    internal Func<Guid> PayloadIdentity { get; set; } = Guid.NewGuid;
    internal void AbortPayload() => _aborted = true;
    /// <summary>Lazily create a same-filesystem, generated write-only draft under retained writer ownership.</summary>
    public PayloadDraft CreatePayloadDraft()
    {
        try
        {
            Check(); RequireNoCandidates();
            if (_pending is not null || _payloadDrafts.Count >= MutationBatch.MaximumChanges)
                throw new InvalidOperationException("Finish pending publication / bounded drafts first.");
            if (_payloadDirectory is null)
            {
                var root = Path.Combine(_repository.DirectoryPath, "staging");
                _createdStagingRoot = !RepositoryLayout.IsExistingDirectory(root);
                Directory.CreateDirectory(root); _ = RepositoryLayout.NormalizeDirectory(root);
                var directory = Path.Combine(root, _owner.ToString("N"));
                if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Payload session directory collision.");
                Directory.CreateDirectory(directory); _ = RepositoryLayout.NormalizeDirectory(directory); _payloadDirectory = directory;
            }
            var id = PayloadIdentity(); ModelValidation.RequireIdentity(id, nameof(PayloadIdentity));
            var path = Path.Combine(_payloadDirectory, $"{id:N}.part");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
            PayloadDraft draft;
            try { draft = new PayloadDraft(this, id, path, stream); }
            catch { stream.Dispose(); throw; }
            _payloadDrafts.Add(draft);
            PayloadObserve?.Invoke(PayloadStage.Created, draft); return draft;
        }
        catch { _aborted = true; throw; }
    }
    internal void CheckDraft(PayloadDraft draft)
    {
        Check();
        if (!_payloadDrafts.Contains(draft)) throw new ArgumentException("Payload draft belongs to another or completed session.", nameof(draft));
    }
    internal PreparedVersion PrepareDraft(PayloadDraft draft, LogicalFileMetadata metadata, DateTimeOffset utc, XxHash64Checksum checksum)
    {
        CheckDraft(draft);
        if (_pending is not null || _prepared.Count >= MutationBatch.MaximumChanges) throw new InvalidOperationException("Finish bounded pending publication first.");
        var version = new PreparedVersion(_owner, draft.VersionId, metadata, utc, checksum);
        if (draft.Reusable is { } reusable && (new HistoryPath(metadata.SourceRootId, metadata.RelativePath) != reusable.Path ||
            metadata.LastWriteTimeUtc != reusable.LastWriteUtc || checksum != reusable.Checksum))
            throw new ArgumentException("Reusable payload metadata/checksum binding differs.", nameof(metadata));
        if (!draft.Installed && File.Exists(_repository.GetPayloadPath(version.VersionId))) throw new IOException("Generated payload collision.");
        _prepared.Add(version); return version;
    }
    internal void InstallDraft(PayloadDraft draft)
    {
        CheckDraft(draft); ValidatePrepared(draft.Prepared!);
        if (_pending is null || !_pending.Versions.Contains(draft.Prepared!)) throw new InvalidOperationException("Prepare this draft's mutation before installation.");
        if (draft.Installed)
        {
            if (draft.Reusable?.State != ReusablePayloadState.TransactionBound) throw new InvalidOperationException("Installed adoption lacks exact transaction binding.");
            RepositoryLayout.RequireFile(_repository.GetPayloadPath(draft.VersionId)); return;
        }
        _ = RepositoryLayout.NormalizeDirectory(Path.GetDirectoryName(draft.StagingPath)!); RepositoryLayout.RequireFile(draft.StagingPath);
        Format.Require(new FileInfo(draft.StagingPath).Length == PayloadPhysicalLength(draft.Length), "staging payload length");
        var final = _repository.GetPayloadPath(draft.VersionId); var bucket = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(bucket); _ = RepositoryLayout.NormalizeDirectory(bucket);
        PayloadObserve?.Invoke(PayloadStage.BeforeInstall, draft);
        PayloadFiles.MoveNew(draft.StagingPath, final);
        RepositoryLayout.RequireFile(final); Format.Require(new FileInfo(final).Length == PayloadPhysicalLength(draft.Length), "installed payload length");
        PayloadObserve?.Invoke(PayloadStage.Installed, draft);
    }
    internal void ReleaseDraft(PayloadDraft draft) { _payloadDrafts.Remove(draft); }
    private void CleanupPayloadDirectories()
    {
        if (_payloadDirectory is null) return;
        try { Directory.Delete(_payloadDirectory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (!_createdStagingRoot) return;
        try { Directory.Delete(Path.GetDirectoryName(_payloadDirectory)!, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
