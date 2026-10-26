using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Counts of requested changes, before payload preparation. No durable allocation reservation.</summary>
public sealed record PublicationAllocationRequest(int Versions = 0, int Upserts = 0, int Removals = 0, int Sources = 0);

public sealed record PublicationAllocationEstimate(long SemanticBytes, long IndexBytes, long RootBytes,
    long RotationBytes, long TransferAndInstallBytes, long ControlBytes, long NamespaceAllowanceBytes,
    long TotalBytes, long MaximumPages);

public sealed partial class RepositoryWriter
{
    /// <summary>Read at a clean capacity boundary under this exact retained writer.</summary>
    public RepositoryReader OpenCapacityReader(Repository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (repository.Identity != _repository.Identity || !string.Equals(repository.DirectoryPath, _repository.DirectoryPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Capacity writer belongs to another repository.", nameof(repository));
        Check(); RequireNoCandidates(); RequireIdle();
        if (_payloadDrafts.Count != 0 || _prepared.Count != 0 || _reuseCandidates.Count != 0)
            throw new InvalidOperationException("Capacity retention requires a clean boundary with no unpublished payloads or prepared versions.");
        return _repository.OpenReader();
    }

    /// <summary>Conservative future headroom. Streaming workspace reads permit same-volume scratch.
    /// Existing staged payload bytes are excluded; caller adds only not-yet-allocated payload bytes.</summary>
    public PublicationAllocationEstimate EstimatePublicationAllocation(PublicationAllocationRequest request, long allocationUnit)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = checked((long)request.Versions + request.Upserts + request.Removals + request.Sources);
        if (request.Versions < 0 || request.Upserts < 0 || request.Removals < 0 || request.Sources < 0 || count > MutationBatch.MaximumChanges)
            throw new ArgumentOutOfRangeException(nameof(request));
        _ = AllocationGeometry.RoundUp(0, allocationUnit);
        using var reader = OpenOwnedReader();
        if (count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        checked
        {
            long Pages(IndexKind kind, FrameAddress? root, int changes)
            {
                if (changes == 0) return 0;
                var depth = root is null ? 0 : new OrderedIndex(kind, reader.Read).Page(root).Level + 1;
                // For every existing level <=changes nodes are touched. Greedy packing has
                // count and byte limits. With max entry 4208, every byte-full page exceeds
                // 12172 bytes; every count-full page holds128 entries. If child output <=6n,
                // output <= n + (16380n+4208*6n)/12172 + (128n+6n)/128 <6n.
                // Leaves satisfy the same bound. New root levels hold >=3 entries except
                // the last page, so ALL additional root pages total <=6n as well.
                var maximumEntry = 2 + Format.MaximumKeyBytes + Format.AddressBytes;
                Format.Require(3 * maximumEntry <= Format.PageBytes - 4 && Format.MaximumPageEntries >= 3,
                    "publication estimator packing assumptions");
                var capacity = Format.PageBytes - 4L; var byteFull = capacity - maximumEntry;
                Format.Require((capacity + 6L * maximumEntry) * Format.MaximumPageEntries + 6 * byteFull <=
                    4 * byteFull * Format.MaximumPageEntries, "publication estimator six-page induction");
                return 6L * changes * (depth + 1);
            }
            long Round(long bytes) => AllocationGeometry.RoundUp(bytes, allocationUnit);
            var pages = Pages(IndexKind.Versions, reader.Captured.Root.Versions, request.Versions) +
                Pages(IndexKind.Current, reader.Captured.Root.Current, request.Versions + request.Upserts + request.Removals) +
                Pages(IndexKind.Sources, reader.Captured.Root.Sources, request.Sources);
            // Fixed fields from the actual codec, plus worst-case UTF-8 path bytes.
            var dummy = new FrameAddress(Guid.NewGuid(), 1, Format.SegmentHeaderBytes, 0, FrameKind.Semantic, 1, new string('0', 64));
            var path = new HistoryPath(Guid.NewGuid(), "a");
            var version = new StoredVersion(new(path, 1, 0, Guid.NewGuid()),
                new(path.SourceRootId, "a", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Archive),
                DateTimeOffset.UnixEpoch, new("EF46DB3751D8E999"));
            var added = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, dummy, Version: version)).Length - 1 + 4L * HistoryPath.MaximumPathLength;
            var upsert = MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, dummy, Path: path, VersionAddress: dummy)).Length +
                Format.MaximumKeyBytes - PathKeys.Current(path).Length; // Version-key maximum also safely bounds Current.
            var removed = MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentRemoved, dummy, Path: path)).Length +
                Format.MaximumKeyBytes - PathKeys.Current(path).Length;
            var semantic = request.Versions * Round(Format.FrameHeaderBytes + added) +
                (request.Versions + request.Upserts) * Round(Format.FrameHeaderBytes + upsert) +
                request.Removals * Round(Format.FrameHeaderBytes + removed) +
                request.Sources * Round(Format.FrameHeaderBytes + Format.MaximumFrameBytes);
            var index = pages * Round(Format.FrameHeaderBytes + Format.PageBytes);
            var rootBytes = Round(Format.FrameHeaderBytes + AuthorityFiles.MaximumControlBytes);
            var frames = pages + 2L * request.Versions + request.Upserts + request.Removals + request.Sources + 1;
            // At most one new header and old seal per Append, plus an abandoned initial tail.
            var rotation = (frames + 1) * (Round(Format.SegmentHeaderBytes) + Round(Format.SealBytes));
            // No compression credit. Each frame can cause one partial flush and one
            // rotation seal; full blocks add ceil(logical/256KiB). Round each possible
            // envelope independently for pessimistic allocation-unit accounting.
            var logical = semantic + index + rootBytes + rotation;
            var blocks = 2 * (frames + 1) + (logical + MetadataBlock.MaximumDecodedBytes - 1) / MetadataBlock.MaximumDecodedBytes;
            var container = blocks * Round(MetadataBlock.Overhead + (_repository.IsEncrypted ? 96 : 0));
            // Charge both transfer and installed append even for renamed new segments.
            // A streaming workspace writes only bounded append extents. Charge its future
            // append copy as well as destination transfer + installation, even if workspace
            // happens to use another volume. Existing workspace bytes are already in F.
            var transferAndInstall = (_streamMetadataReads ? 3 : 2) * (logical + container);
            var controls = 3 * Round(AuthorityFiles.MaximumControlBytes + (_repository.IsEncrypted ? 64 : 0)) +
                3L * request.Versions * Round(ReusablePayloadDescriptor.MaximumBytes + (_repository.IsEncrypted ? 64 : 0)); // Ready, revocation rewrite, optional restoration
            // Allocation-unit bookkeeping allowance per possible file/directory name, not an
            // exact bound on undocumented filesystem internals. Native reserve/feedback remains required.
            var names = 2 * frames + 10L * request.Versions + 4; // bounded cache bucket/descriptor/transition names as well
            var nameBytes = names * allocationUnit;
            return new(semantic, index, rootBytes, rotation, transferAndInstall, controls, nameBytes,
                transferAndInstall + controls + nameBytes, pages);
        }
    }
}
