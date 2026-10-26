using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed partial class RepositoryWriter
{
    private PreparedMutation StagePublication(Authority baseline, MetadataRoot root, SegmentAppender append,
        MetadataWorkspace workspace, bool ownsWorkspace, IReadOnlyList<PreparedVersion> versions,
        Action<RepositoryReader>? confirm, CancellationToken token)
    {
        BindReusable(versions, baseline, root.TransactionId);
        var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root));
        append.FlushAndValidate(); LastWriteStatistics = append.Statistics; append.Dispose();
        var prepared = new PreparedMutation(this, baseline, new(_repository.Identity, root, address), workspace, ownsWorkspace, versions, confirm);
        _pending = prepared;
        Observe?.Invoke(PublicationStage.LocalPrepared); token.ThrowIfCancellationRequested();
        Check(); Format.Require(_repository.ReadAuthority() == baseline, "predecessor changed during preparation");
        foreach (var local in Directory.GetFiles(Path.Combine(workspace.DirectoryPath, "metadata"), "*.seg"))
        {
            token.ThrowIfCancellationRequested();
            using var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[Format.SegmentHeaderBytes]; source.ReadExactly(bytes); var header = MetadataCodec.DecodeSegment(bytes);
            var originalLength = workspace.OriginalLengths.GetValueOrDefault(header.SegmentId);
            var finalLength = originalLength == 0 ? source.Length : checked(originalLength + source.Length - Format.SegmentHeaderBytes);
            Format.Require(finalLength >= originalLength, "local metadata shrank");
            if (finalLength == originalLength) continue;
            var temporary = Path.Combine(_repository.DirectoryPath, "metadata", $"prepare.{root.TransactionId:N}.{header.SegmentId:N}.tmp");
            var transfer = new MetadataTransfer(header, originalLength, finalLength, temporary, "");
            source.Position = originalLength == 0 ? 0 : Format.SegmentHeaderBytes;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                // Register ownership only after CreateNew succeeds; a collision is not ours to delete.
                prepared.Transfers.Add(transfer);
                MetadataWorkspace.Copy(source, output, transfer.FinalLength - originalLength, token); output.Flush(true);
            }
            prepared.Transfers[^1] = transfer with { Sha256 = HashFile(temporary) };
        }
        AuthorityFiles.WriteNew(Path.Combine(_repository.DirectoryPath, prepared.ControlName), _repository.Crypto?.Protect(MetadataCodec.EncodeRoot(root), "prepared-root") ?? MetadataCodec.EncodeRoot(root));
        AuthorityFiles.WriteNew(Path.Combine(_repository.DirectoryPath, prepared.CandidateName), AuthorityFiles.StateBytes(prepared.Intended, _repository.Crypto));
        Format.Require(AuthorityFiles.ReadPrepared(_repository.DirectoryPath, _repository.Identity, prepared.CandidateName, prepared.ControlName, _repository.Crypto) == prepared.Intended,
            "staged candidate mismatch");
        Observe?.Invoke(PublicationStage.CandidateFlushed); token.ThrowIfCancellationRequested(); return prepared;
    }

    public MutationResult Commit(PreparedMutation prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        try
        {
            Check();
            if (!ReferenceEquals(_pending, prepared) || prepared.Completed) throw new InvalidOperationException("Publication is not pending in this session.");
            cancellationToken.ThrowIfCancellationRequested();
            Format.Require(_repository.ReadAuthority() == prepared.Baseline, "prepared predecessor changed");
            if (!prepared.Changed) { Complete(prepared); return new(prepared.IntendedAuthority, false); }
            VerifyPayloads(prepared);
            Format.Require(AuthorityFiles.ReadPrepared(_repository.DirectoryPath, _repository.Identity, prepared.CandidateName, prepared.ControlName, _repository.Crypto) == prepared.Intended,
                "staged candidate changed");
            Observe?.Invoke(PublicationStage.BeforeMetadataInstall); cancellationToken.ThrowIfCancellationRequested(); Check();
            foreach (var transfer in prepared.Transfers.OrderBy(t => t.Header.Ordinal)) Install(transfer, cancellationToken);
            Format.Require(AuthorityFiles.Read(_repository.DirectoryPath, _repository.Identity, prepared.CandidateName, _repository.Crypto) == prepared.Intended, "installed candidate mismatch");
            Observe?.Invoke(PublicationStage.MetadataFlushed); cancellationToken.ThrowIfCancellationRequested();
            Check(); Format.Require(_repository.ReadAuthority() == prepared.Baseline, "predecessor changed before publication");
            Observe?.Invoke(PublicationStage.BeforeReplace); cancellationToken.ThrowIfCancellationRequested(); Check();
            VerifyPayloads(prepared);
            Format.Require(_repository.ReadAuthority() == prepared.Baseline, "predecessor changed before replacement");
            var result = ReplaceAndConfirm(prepared);
            try
            {
                // Comparison/confirmation metadata is also materialized once locally.
                using var reader = _streamMetadataReads ? _repository.OpenReader() : _repository.OpenComparisonReader(_workspaceRoot);
                Format.Require(reader.Authority == result.Identity, "confirmation authority changed"); prepared.Confirm?.Invoke(reader);
            }
            catch (Exception error) { throw new PublicationUncertainException(result.Identity, error); }
            foreach (var version in prepared.Versions) _prepared.Remove(version);
            FinishReusable(prepared.Intended.Root.TransactionId, PublicationOutcome.Published);
            Complete(prepared); return new(result.Identity, true);
        }
        catch
        {
            _aborted = true; CleanupTransfers(prepared); prepared.CleanupLocal(); throw;
        }
    }
    private void VerifyPayloads(PreparedMutation prepared)
    {
        foreach (var version in prepared.Versions)
        {
            ValidatePrepared(version); var path = _repository.GetPayloadPath(version.VersionId); RepositoryLayout.RequireFile(path);
            Format.Require(new FileInfo(path).Length == _repository.EstimatePayloadPhysicalBytes(version.Metadata.Length), "prepared payload length");
        }
    }
    private void Install(MetadataTransfer transfer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RepositoryLayout.RequireFile(transfer.TemporaryPath);
        Format.Require(new FileInfo(transfer.TemporaryPath).Length == transfer.FinalLength - transfer.OriginalLength &&
            HashFile(transfer.TemporaryPath) == transfer.Sha256, "prepared metadata transfer changed");
        var destination = SegmentReader.SegmentPath(_repository.DirectoryPath, transfer.Header.SegmentId);
        if (transfer.OriginalLength == 0)
        {
            // Non-overwriting installation of a generated segment identity.
            File.Move(transfer.TemporaryPath, destination);
        }
        else
        {
            RepositoryLayout.RequireFile(destination);
            using var output = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Format.Require(output.Length == transfer.OriginalLength, "destination append extent changed");
            var header = new byte[Format.SegmentHeaderBytes]; output.ReadExactly(header);
            Format.Require(MetadataCodec.DecodeSegment(header) == transfer.Header, "destination segment changed");
            output.Position = transfer.OriginalLength;
            using var input = new FileStream(transfer.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            MetadataWorkspace.Copy(input, output, transfer.FinalLength - transfer.OriginalLength, token); output.Flush(true);
        }
        Format.Require(new FileInfo(destination).Length == transfer.FinalLength &&
            HashFile(destination, transfer.OriginalLength) == transfer.Sha256, "destination metadata readback");
    }
    private static string HashFile(string path, long offset = 0)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = offset; return Convert.ToHexString(SHA256.HashData(stream));
    }
    private Authority Publish(Authority baseline, MetadataRoot root, SegmentAppender append, MetadataWorkspace workspace, CancellationToken token)
    {
        try
        {
            using var prepared = StagePublication(baseline, root, append, workspace, false, [], null, token);
            _ = prepared.Commit(token); return prepared.Intended;
        }
        catch { _aborted = true; CleanupTransfers(_pending); throw; }
    }
    private void Complete(PreparedMutation prepared)
    {
        prepared.Completed = true; _pending = null; CleanupTransfers(prepared);
        if (prepared.Changed) TryDelete(Path.Combine(_repository.DirectoryPath, prepared.ControlName));
        prepared.CleanupLocal();
    }
    internal void Discard(PreparedMutation prepared)
    {
        if (prepared.Completed) return;
        if (ReferenceEquals(_pending, prepared))
        {
            if (!_aborted && !_disposed)
            {
                try
                {
                    Check();
                    if (prepared.Changed && _repository.ReadAuthority() == prepared.Baseline)
                    {
                        TryDelete(Path.Combine(_repository.DirectoryPath, prepared.CandidateName));
                        TryDelete(Path.Combine(_repository.DirectoryPath, prepared.ControlName));
                        // Ready is restored only after exact predecessor and conclusive candidate revocation.
                        try { _ = File.GetAttributes(Path.Combine(_repository.DirectoryPath, prepared.CandidateName)); }
                        catch (FileNotFoundException) { FinishReusable(prepared.Intended.Root.TransactionId, PublicationOutcome.Unpublished); }
                    }
                }
                catch (Exception) { /* Cleanup cannot decide an uncertain publication outcome. Preserve evidence. */ }
                if (prepared.Changed) _aborted = true;
            }
            _pending = null;
        }
        prepared.Completed = true; CleanupTransfers(prepared); prepared.CleanupLocal();
    }
    private static void CleanupTransfers(PreparedMutation? prepared)
    {
        if (prepared is null) return;
        foreach (var transfer in prepared.Transfers) TryDelete(transfer.TemporaryPath);
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
