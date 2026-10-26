using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed partial class RepositoryWriter
{
    /// <summary>Remove exact noncurrent versions; commit visibility before optional bounded content cleanup. No age policy.</summary>
    public VersionRemovalResult RemoveVersions(IEnumerable<VersionIdentity> identities, AuthorityIdentity expected,
        bool cleanupPayloads = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities); var requested = identities.Take(MutationBatch.MaximumChanges + 1).ToArray();
        if (requested.Length > MutationBatch.MaximumChanges || requested.Any(id => id is null || id.Path is null || id.VersionId == Guid.Empty))
            throw new ArgumentException("Invalid bounded version-removal request.", nameof(identities));
        var keys = new SortedSet<byte[]>(PathKeys.Comparer);
        foreach (var id in requested) if (!keys.Add(PathKeys.Version(id))) throw new ArgumentException("Duplicate version-removal key.", nameof(identities));
        MetadataWorkspace? workspace = null;
        try
        {
            Check(); RequireNoCandidates();
            if (_pending is not null) throw new InvalidOperationException("Finish the pending publication first.");
            cancellationToken.ThrowIfCancellationRequested(); LastWriteStatistics = null;
            workspace = new(_repository, _workspaceRoot, cancellationToken, streamMetadataReads: _streamMetadataReads);
            using var baseline = _repository.OpenWorkingReader(workspace);
            if (baseline.Authority != expected) throw new InvalidOperationException("Stale expected authority.");
            var selected = new List<StoredVersion>(); var current = new Dictionary<HistoryPath, VersionIdentity?>();
            int absent = 0; long bytes = 0;
            foreach (var identity in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = PathKeys.Version(identity); var address = baseline.VersionIndex.Find(baseline.Captured.Root.Versions, key);
                if (address is null) { absent++; continue; }
                var version = baseline.Version(address, identity.Path, key);
                Format.Require(version.Identity == identity, "removal identity/UUID mismatch");
                var protectedIdentity = baseline.GetCurrent(identity.Path)?.Identity;
                if (protectedIdentity == identity) throw new InvalidOperationException("Cannot remove the exact current successful version.");
                current.TryAdd(identity.Path, protectedIdentity); selected.Add(version); bytes = checked(bytes + version.Metadata.Length);
            }
            if (selected.Count == 0) { baseline.Dispose(); workspace.Dispose(); return new(baseline.Authority, 0, absent, 0, EmptyCleanup()); }
            var prior = baseline.Captured.Root; var sequence = checked(prior.Sequence + 1);
            using var append = workspace.CreateAppender(baseline.Captured, prior.Epoch, RotationBytes);
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? baseline.Read(p);
            var index = new OrderedIndex(IndexKind.Versions, Read, append); var journal = prior.Journal;
            var committed = new List<(VersionIdentity Identity, FrameAddress Address)>(); var changes = new List<(byte[], FrameAddress?)>();
            foreach (var version in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, journal, RemovedVersion: version.Identity)));
                committed.Add((version.Identity, journal)); changes.Add((PathKeys.Version(version.Identity), null));
            }
            var root = NextRoot(baseline.Captured, sequence, prior.Epoch, journal, prior.Current,
                index.Update(prior.Versions, changes), prior.Sources, prior.Retired);
            using var prepared = StagePublication(baseline.Captured, root, append, workspace, true, [], Confirm, cancellationToken);
            baseline.Dispose();
            var publication = prepared.Commit(cancellationToken);
            VersionRemovalResult Result(PayloadCleanupResult value) => new(publication.Authority, selected.Count, absent, bytes, value)
            { VersionPagesWritten = LastWriteStatistics?.PagesWritten ?? 0, MetadataBytesAppended = LastWriteStatistics?.PhysicalBytesWritten ?? 0 };
            // Only a confirmed authority containing these exact semantic bytes authorizes this fast cleanup path.
            var cleanup = EmptyCleanup();
            if (cleanupPayloads)
            {
                try
                {
                    using var reader = _repository.OpenReader(); Format.Require(reader.Authority == publication.Authority, "cleanup authority changed");
                    cleanup = CleanupIdentities(reader, committed.Select(item =>
                    {
                        var record = reader.Semantic(item.Address);
                        Format.Require(record.Kind == SemanticKind.VersionRemoved && record.RemovedVersion == item.Identity, "committed removal confirmation");
                        return item.Identity;
                    }), cancellationToken, reportInterrupted: true);
                }
                catch (PayloadCleanupInterruptedException error) { throw new VersionRemovalCommittedException(Result(error.Result), error.InnerException!); }
                catch (Exception error) { throw new VersionRemovalCommittedException(Result(cleanup), error); }
            }
            return Result(cleanup);
            void Confirm(RepositoryReader reader)
            {
                foreach (var version in selected)
                    Format.Require(reader.VersionIndex.Find(reader.Captured.Root.Versions, PathKeys.Version(version.Identity)) is null, "removed version confirmation");
                foreach (var entry in current) Format.Require(reader.GetCurrent(entry.Key)?.Identity == entry.Value, "current changed during removal");
            }
        }
        catch { _aborted = true; CleanupTransfers(_pending); workspace?.Dispose(); throw; }
    }
    private static PayloadCleanupResult EmptyCleanup() => new(0, 0, 0, 0, 0, false, Array.Empty<PayloadCleanupProblem>());
}
