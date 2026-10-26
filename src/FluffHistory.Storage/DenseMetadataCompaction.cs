using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed partial class RepositoryWriter
{
    private AuthorityIdentity CompactDense(MaintenanceLimits limits, CancellationToken token)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        if (LocalCompactionCheckInterval is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(LocalCompactionCheckInterval));
        SemanticHistoryFold.ValidateLimits(limits);
        using var workspace = new MetadataWorkspace(_repository, _workspaceRoot, token, streamMetadataReads: _streamMetadataReads) { MaximumMaterializedBytes = limits.MaximumMaterializedBytes };
        using var baseline = _repository.OpenWorkingReader(workspace); var prior = baseline.Captured.Root;
        using var fold = new SemanticHistoryFold(baseline, workspace, limits, token, HistoryFoldBufferEntries);
        LastHistoryFoldStatistics = fold.Statistics;
        var epoch = Guid.NewGuid();
        using var append = workspace.CreateAppender(baseline.Captured, epoch, RotationBytes);
        append.MaximumOutputBytes = limits.MaximumOutputBytes;
        var root = BuildDenseBase(baseline, fold, epoch, append.Append, limits, token);
        return Publish(baseline.Captured, root, append, workspace, token).Identity;
    }

    // Assessment and real compaction share the exact semantic/index emitter. Assessment replaces
    // only the output sink: no compressed blocks, candidates, payloads or authority are written.
    private MetadataRoot BuildDenseBase(RepositoryReader baseline, SemanticHistoryFold fold, Guid epoch,
        Func<FrameKind, byte[], FrameAddress> emit, MaintenanceLimits limits, CancellationToken token)
    {
        fold.ValidateIndexes();
        var prior = baseline.Captured.Root;
        var decisions = fold.Decisions; var configs = fold.Configurations; var versionCount = fold.Statistics.Survivors;
        var selectedCount = fold.Current.Count;
        var currentIndex = new OrderedIndex(IndexKind.Current, baseline.Read, emit: emit);
        var versionIndex = new OrderedIndex(IndexKind.Versions, baseline.Read, emit: emit);
        var sourceIndex = new OrderedIndex(IndexKind.Sources, baseline.Read, emit: emit);
        FrameAddress? journal = null, retired = null;
        FrameAddress Add(SemanticRecord record)
        {
            token.ThrowIfCancellationRequested();
            journal = emit(FrameKind.Semantic, MetadataCodec.EncodeSemantic(record with { Previous = journal })); return journal;
        }
        var sources = sourceIndex.BuildSorted(configs.Values.Select(s => (Key: PathKeys.Source(s.SourceRootId), Source: s))
            .OrderBy(p => p.Key, PathKeys.Comparer).Select(p => new PageEntry(p.Key, Add(new(SemanticKind.SourceRoot, null, Source: p.Source)))));
        var remapped = new SortedDictionary<byte[], FrameAddress>(PathKeys.Comparer);
        long copied = 0;
        var versions = versionIndex.BuildSorted(CopyVersions());
        Format.Require(copied == versionCount, "extra/missing indexed versions");
        Format.Require(remapped.Count == selectedCount, "membership outside committed versions");
        foreach (var entry in remapped)
            Add(new(SemanticKind.CurrentUpsert, null, Path: PathKeys.DecodePath(entry.Key), VersionAddress: entry.Value));
        var current = currentIndex.BuildSorted(remapped.Select(p => new PageEntry(p.Key, p.Value)));
        var segments = new HashSet<Guid>();
        for (var p = prior.Retired; p is not null;)
        {
            var old = MetadataCodec.DecodeRetired(baseline.Read(p));
            Format.Require(old.Previous is null || old.Previous.Serial < p.Serial, "retirement cycle");
            Retire(old); p = old.Previous;
        }
        var id = baseline.Captured.Address.SegmentId; var ordinal = baseline.Captured.Address.SegmentOrdinal;
        while (id != Guid.Empty)
        {
            var header = baseline.Segments.Header(id, ordinal);
            Retire(new(header.SegmentId, header.Epoch, header.Ordinal, null)); id = header.PreviousId; ordinal = header.PreviousOrdinal;
        }
        token.ThrowIfCancellationRequested();
        return NextRoot(baseline.Captured, prior.Sequence, epoch, journal, current, versions, sources, retired);

        IEnumerable<PageEntry> CopyVersions()
        {
            var localWork = 0;
            foreach (var (group, value) in fold.ReadVersions())
            {
                token.ThrowIfCancellationRequested();
                if (localWork++ == 0) Check();
                if (localWork == LocalCompactionCheckInterval) localWork = 0;
                if (group.Removal is not null)
                {
                    if (!PayloadConclusivelyAbsent(group.Identity))
                        Add(new(SemanticKind.VersionRemoved, null, RemovedVersion: group.Identity));
                    continue;
                }
                var version = value!;
                var pointer = Add(new(SemanticKind.VersionAdded, null, Version: version)); copied++;
                if (decisions.TryGetValue(version.Identity.Path, out var selected) && selected == group.Addition)
                    remapped.Add(PathKeys.Current(version.Identity.Path), pointer);
                yield return new(group.Key, pointer);
            }
        }
        void Retire(RetiredSegment old)
        {
            token.ThrowIfCancellationRequested();
            if (!segments.Add(old.Id)) return;
            if (segments.Count > limits.MaximumRecords) throw new IOException("Maintenance segment budget exceeded.");
            retired = emit(FrameKind.Retired, MetadataCodec.EncodeRetired(old with { Previous = retired }));
        }
    }
}
