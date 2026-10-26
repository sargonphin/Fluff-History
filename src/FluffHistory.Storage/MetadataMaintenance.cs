using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed partial class RepositoryWriter
{
    /// <summary>Explicit budgeted committed-history reconstruction; never invoked by ordinary opening.</summary>
    public AuthorityIdentity RebuildIndexes(MaintenanceLimits? limits = null, CancellationToken cancellationToken = default) => Rebuild(limits ?? new(), cancellationToken);
    /// <summary>Replace metadata with a semantic base in a new epoch; immutable payloads are untouched.</summary>
    public AuthorityIdentity CompactMetadata(MaintenanceLimits? limits = null, CancellationToken cancellationToken = default)
    {
        try { return CompactDense(limits ?? new(), cancellationToken); }
        catch { _aborted = true; throw; }
    }
    private AuthorityIdentity Rebuild(MaintenanceLimits limits, CancellationToken token)
    {
        try
        {
            Check(); RequireNoCandidates(); RequireIdle();
            SemanticHistoryFold.ValidateLimits(limits);
            using var workspace = new MetadataWorkspace(_repository, _workspaceRoot, token) { MaximumMaterializedBytes = limits.MaximumMaterializedBytes };
            using var baseline = _repository.OpenWorkingReader(workspace); var prior = baseline.Captured.Root;
            using var fold = new SemanticHistoryFold(baseline, workspace, limits, token, HistoryFoldBufferEntries);
            LastHistoryFoldStatistics = fold.Statistics;
            using var append = workspace.CreateAppender(baseline.Captured, prior.Epoch, RotationBytes);
            append.MaximumOutputBytes = limits.MaximumOutputBytes;
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? baseline.Read(p);
            var currentIndex = new OrderedIndex(IndexKind.Current, Read, append);
            var versionIndex = new OrderedIndex(IndexKind.Versions, Read, append);
            var sourceIndex = new OrderedIndex(IndexKind.Sources, Read, append);
            var versions = versionIndex.BuildSorted(fold.Groups().Where(g => g.Removal is null).Select(g => new PageEntry(g.Key, g.Addition!)));
            var current = currentIndex.BuildSorted(fold.Current.Select(p => new PageEntry(p.Key, p.Value)));
            var sources = sourceIndex.BuildSorted(fold.ConfigurationAddresses.Select(p => new PageEntry(PathKeys.Source(p.Key), p.Value)).OrderBy(e => e.Key, PathKeys.Comparer));
            token.ThrowIfCancellationRequested();
            var root = NextRoot(baseline.Captured, prior.Sequence, prior.Epoch, prior.Journal, current, versions, sources, prior.Retired);
            return Publish(baseline.Captured, root, append, workspace, token).Identity;
        }
        catch { _aborted = true; throw; }
    }
    /// <summary>Delete only exact retired metadata identities; unknown files and payloads are never considered.</summary>
    public MetadataCleanupResult CleanupRetiredMetadata(CancellationToken cancellationToken = default)
    {
        try
        {
            Check(); RequireNoCandidates(); RequireIdle(); using var reader = _repository.OpenReader(); int deleted = 0, deferred = 0;
            for (var p = reader.Captured.Root.Retired; p is not null;)
            {
                cancellationToken.ThrowIfCancellationRequested(); Check();
                var retired = MetadataCodec.DecodeRetired(reader.Read(p));
                Format.Require(retired.Epoch != reader.Captured.Root.Epoch && (retired.Previous is null || retired.Previous.Serial < p.Serial), "unsafe retirement chain");
                var path = SegmentReader.SegmentPath(_repository.DirectoryPath, retired.Id);
                try
                {
                    RepositoryLayout.RequireFile(path);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var bytes = new byte[Format.SegmentHeaderBytes]; stream.ReadExactly(bytes); var header = MetadataCodec.DecodeSegment(bytes);
                        Format.Require(header.RepositoryId == _repository.Identity.RepositoryId && header.SegmentId == retired.Id && header.Epoch == retired.Epoch && header.Ordinal == retired.Ordinal, "retired segment binding");
                    }
                    try { File.Delete(path); deleted++; }
                    catch (IOException e) when (OperatingSystem.IsWindows() && (e.HResult & 0xffff) is 32 or 33) { deferred++; }
                }
                catch (FileNotFoundException) { }
                p = retired.Previous;
            }
            return new(deleted, deferred);
        }
        catch { _aborted = true; throw; }
    }
}
