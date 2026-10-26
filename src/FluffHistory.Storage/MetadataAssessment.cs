using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Committed ordinary decoded frame bytes in the active epoch, excluding segment/block
/// envelopes, seals, abandoned suffixes, retired epochs, payloads and controls.</summary>
public sealed record ActiveMetadataSummary(AuthorityIdentity Authority, long DecodedBytes, int Segments, bool LatestRecordRemovesVersion);

/// <summary>Exact net decoded-frame reduction for the dense emitter at this authority. This is
/// not a prediction of compressed physical savings or filesystem free space.</summary>
public sealed record MetadataAssessment(ActiveMetadataSummary Active, long DenseDecodedBytes, long OutputFrames, long MaximumOutputBytes)
{
    public long ReclaimableBytes => Math.Max(0, Active.DecodedBytes - DenseDecodedBytes);
    public double DirtyRatio => Active.DecodedBytes == 0 ? 0 : (double)ReclaimableBytes / Active.DecodedBytes;
}

public sealed partial class RepositoryWriter
{
    /// <summary>O(active segments) clean-boundary prefilter. Does not enumerate arbitrary metadata files.</summary>
    public ActiveMetadataSummary InspectActiveMetadata(MaintenanceLimits? limits = null, CancellationToken cancellationToken = default)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        using var reader = _repository.OpenReader();
        return Inspect(reader, limits ?? new(), cancellationToken);
    }

    private static ActiveMetadataSummary Inspect(RepositoryReader reader, MaintenanceLimits limits, CancellationToken token)
    {
        SemanticHistoryFold.ValidateLimits(limits);
        long bytes = 0; int count = 0;
        var id = reader.Captured.Address.SegmentId; var ordinal = reader.Captured.Address.SegmentOrdinal;
        while (id != Guid.Empty)
        {
            token.ThrowIfCancellationRequested();
            if (++count > limits.MaximumRecords) throw new IOException("Maintenance segment budget exceeded.");
            var header = reader.Segments.Header(id, ordinal);
            bytes = checked(bytes + reader.Segments.CommittedLength(id, ordinal) - Format.SegmentHeaderBytes);
            id = header.PreviousId; ordinal = header.PreviousOrdinal;
        }
        return new(reader.Authority, bytes, count, reader.Captured.Root.Journal is { } head && reader.Semantic(head).Kind == SemanticKind.VersionRemoved);
    }

    /// <summary>Read-only, budgeted dense-output accounting under retained ownership. Shares semantic
    /// folding/index validation and exact emission with compaction, but writes only disposable scratch.</summary>
    public MetadataAssessment AssessMetadata(MaintenanceLimits? limits = null, CancellationToken cancellationToken = default)
        => AssessMetadataCore(limits, cancellationToken, _repository.IsEncrypted);
    internal MetadataAssessment AssessMetadataForEncryption(bool encrypted, MaintenanceLimits? limits, CancellationToken token)
        => AssessMetadataCore(limits, token, encrypted);
    private MetadataAssessment AssessMetadataCore(MaintenanceLimits? limits, CancellationToken cancellationToken, bool encrypted)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        var budget = limits ?? new(); SemanticHistoryFold.ValidateLimits(budget);
        using var workspace = new MetadataWorkspace(_repository, _workspaceRoot, cancellationToken, streamMetadataReads: true)
            { MaximumMaterializedBytes = budget.MaximumMaterializedBytes };
        using var reader = _repository.OpenWorkingReader(workspace);
        var active = Inspect(reader, budget, cancellationToken);
        using var fold = new SemanticHistoryFold(reader, workspace, budget, cancellationToken, HistoryFoldBufferEntries);
        long bytes = 0, frames = 0, serial = reader.Captured.Address.Serial;
        var output = new DenseOutputCounter(RotationBytes, encrypted);
        var epoch = Guid.NewGuid();
        FrameAddress Count(FrameKind kind, byte[] body)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes = checked(bytes + Format.FrameHeaderBytes + body.Length); frames++;
            if (bytes > budget.MaximumOutputBytes) throw new IOException("Maintenance assessment output budget exceeded.");
            output.Add(Format.FrameHeaderBytes + body.Length);
            return new(epoch, 1, Format.SegmentHeaderBytes, body.Length, kind, checked(++serial), new string('0', 64));
        }
        var root = BuildDenseBase(reader, fold, epoch, Count, budget, cancellationToken);
        Count(FrameKind.Root, MetadataCodec.EncodeRoot(root));
        Check(); Format.Require(_repository.ReadAuthority().Identity == active.Authority, "assessment authority changed");
        return new(active, bytes, frames, output.MaximumBytes);
    }

    // Mirrors only the appender's logical rotation and explicit flush geometry, taking NO
    // compression credit. Both encryption overhead and the appender's early reservation margin
    // are included. No physical bytes are attributed to individual live/dead input frames.
    private sealed class DenseOutputCounter(long rotation, bool encrypted)
    {
        private long _position, _bytes, _blocks, _pendingBytes;
        private int _blockBytes, _pendingFrames;
        internal void Add(int count)
        {
            checked
            {
                if (_position == 0 || _position + count + Format.SealBytes > rotation)
                {
                    if (_position != 0) { Flush(); _bytes += Format.SealBytes; _blocks++; }
                    _bytes += Format.SegmentHeaderBytes; _position = Format.SegmentHeaderBytes;
                }
                _position += count; _bytes += count;
                var remaining = count;
                while (remaining > 0)
                {
                    var part = Math.Min(remaining, MetadataBlock.MaximumDecodedBytes - _blockBytes);
                    _blockBytes += part; remaining -= part;
                    if (_blockBytes == MetadataBlock.MaximumDecodedBytes) Flush();
                }
                _pendingBytes += count;
                if (++_pendingFrames >= 4096 || _pendingBytes >= 8388608)
                { Flush(); _pendingFrames = 0; _pendingBytes = 0; }
            }
        }
        private void Flush() { if (_blockBytes != 0) { _blocks = checked(_blocks + 1); _blockBytes = 0; } }
        internal long MaximumBytes
        {
            get
            {
                Flush();
                return checked(_bytes + _blocks * (MetadataBlock.Overhead + (encrypted ? 96 : 0)) + Format.SegmentHeaderBytes + Format.SealBytes);
            }
        }
    }
}
