using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed class RetentionReadStatistics
{
    internal ComparisonReadStatistics Reads { get; } = new();
    internal long RetainedEntries, RetainedKeyBytes;
    public long VersionsExamined { get; internal set; }
    public long CurrentEntriesExamined { get; internal set; }
    public long ProtectedVersions { get; internal set; }
    public long VersionPagesRead { get; internal set; }
    public long CurrentPagesRead { get; internal set; }
    public long SemanticRecordsValidated { get; internal set; }
    public long SourceRecordsValidated { get; internal set; }
    public long VersionWindows { get; internal set; }
    public long CurrentWindows { get; internal set; }
    public long MaximumRetainedEntries { get; internal set; }
    public long MaximumRetainedKeyBytes { get; internal set; }
    public long IndexPagesRead => Reads.Io.PagesRead;
    public long LogicalReadRequests => Reads.DestinationReadRequests;
    public long MetadataBytesRequested => Reads.DestinationBytesRequested;
    public long SegmentOpens => Reads.Io.SegmentOpens;
    public long PeakRetainedHandles => Reads.Io.PeakHandles;
    public long GroupedWindows => Reads.Windows;
    public long MaximumBufferedEntries => Reads.MaximumBufferedEntries;
    public long MaximumBufferedKeyBytes => Reads.MaximumBufferedKeyBytes;
}

public sealed partial class RepositoryReader
{
    public RetentionReadStatistics? LastRetentionStatistics { get; private set; }
    internal Action? RetentionWindowCompleted { get; set; }

    /// <summary>Repository-wide path-major merge. No writer, workspace, payload reads or age ordering.
    /// Optional exact position resumes after it within this capture. Two bounded projection windows
    /// share one segment-handle pool and frame buffer. Not thread safe.</summary>
    public IEnumerable<RetentionVersion> EnumerateRetentionVersions(VersionIdentity? after = null,
        CancellationToken cancellationToken = default) => EnumerateRetentionVersions(new(), after, cancellationToken);

    internal IEnumerable<RetentionVersion> EnumerateRetentionVersions(ComparisonReadBudget budget, VersionIdentity? after = null,
        CancellationToken cancellationToken = default)
    {
        budget.Validate();
        if (after is not null) { ArgumentNullException.ThrowIfNull(after.Path); if (after.VersionId == Guid.Empty) throw new ArgumentException("Exact position required.", nameof(after)); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Workspace?.CancellationToken ?? default);
        var token = linked.Token;
        void Check() { token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); }
        Check(); var stats = new RetentionReadStatistics(); LastRetentionStatistics = stats; var reads = stats.Reads;
        using var segments = new SegmentReader(_repository.DirectoryPath, _repository.Identity, Captured.Root.Epoch,
            Captured.Address, statistics: reads.Io, comparison: reads, crypto: _repository.Crypto);
        _comparisonSessions.Add(segments);
        try
        {
            var frame = new byte[Format.FrameHeaderBytes + Format.MaximumFrameBytes];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] ReadPage(FrameAddress address) { Check(); return segments.Read(address); }
            byte[] VersionPage(FrameAddress address) { stats.VersionPagesRead++; return ReadPage(address); }
            byte[] CurrentPage(FrameAddress address) { stats.CurrentPagesRead++; return ReadPage(address); }
            var versions = new OrderedIndex(IndexKind.Versions, VersionPage);
            var current = new OrderedIndex(IndexKind.Current, CurrentPage);
            var sources = new OrderedIndex(IndexKind.Sources, ReadPage);
            using var currents = Grouped(current, Captured.Root.Current, after is null ? null : PathKeys.Current(after.Path), false).GetEnumerator();
            var hasCurrent = currents.MoveNext();
            HistoryPath? path = null; RetentionProjection? selected = null; bool matched = false, first = true;
            SourceRootConfiguration? source = null;
            foreach (var entry in Grouped(versions, Captured.Root.Versions, after is null ? null : PathKeys.Version(after), true))
            {
                Check(); var version = entry.Version;
                if (first && after is not null && version.Identity != after) throw new ArgumentException("Position is not an exact surviving version.", nameof(after));
                first = false;
                if (path != version.Identity.Path)
                {
                    FinishPath(); path = version.Identity.Path; selected = null; matched = false;
                    Format.Require(!hasCurrent || currents.Current.Version.Identity.Path.CompareTo(path) >= 0, "current path missing from surviving history");
                    if (hasCurrent && currents.Current.Version.Identity.Path == path)
                    {
                        selected = currents.Current; stats.CurrentEntriesExamined++;
                        if (source?.SourceRootId != path.SourceRootId)
                        {
                            var address = sources.Find(Captured.Root.Sources, PathKeys.Source(path.SourceRootId));
                            Format.Require(address is not null, "current source missing"); MetadataCodec.RequireKind(address, FrameKind.Semantic);
                            var configuration = MetadataCodec.DecodeSemantic(segments.ReadComparison(address!, frame, hash).ToArray());
                            MetadataCodec.RequireKind(configuration.Previous, FrameKind.Semantic);
                            if (configuration.Previous is { } previous) { segments.CheckAddress(previous); Format.Require(previous.Serial < address!.Serial, "semantic forward/cycle"); }
                            Format.Require(configuration.Kind == SemanticKind.SourceRoot && configuration.Source!.SourceRootId == path.SourceRootId, "retention source binding");
                            source = configuration.Source; stats.SourceRecordsValidated++; stats.SemanticRecordsValidated++;
                        }
                        Format.Require(source!.IsProtected && selected.Version.Length <= source.MaximumFileSize &&
                            !source.ExcludedDirectories.Any(e => path.CanonicalPath.StartsWith(e + "/", StringComparison.Ordinal)), "current outside protection configuration");
                        if (after is not null && path == after.Path)
                        {
                            // One seek only at a continuation boundary where current may precede 'after'.
                            Format.Require(versions.Find(Captured.Root.Versions, PathKeys.Version(selected.Version.Identity)) == selected.Address, "current not surviving"); matched = true;
                        }
                    }
                }
                var isCurrent = selected?.Version.Identity == version.Identity;
                if (isCurrent) { Format.Require(entry.Address == selected!.Address, "current/version address mismatch"); matched = true; }
                if (after is not null && version.Identity == after) continue;
                if (isCurrent) stats.ProtectedVersions++;
                stats.VersionsExamined++; yield return isCurrent ? version with { IsCurrent = true } : version;
            }
            if (first && after is not null) throw new ArgumentException("Position is not an exact surviving version.", nameof(after));
            FinishPath(); Format.Require(!hasCurrent, "current path missing from surviving history");
            void FinishPath()
            {
                if (selected is null) return;
                Format.Require(matched, "current version not surviving"); hasCurrent = currents.MoveNext();
            }

            IEnumerable<RetentionProjection> Grouped(OrderedIndex index, FrameAddress? root, byte[]? lower, bool history)
            {
                using var entries = index.Range(root, lower).GetEnumerator();
                var requests = new ComparisonRequest[budget.MaximumEntries]; var results = new RetentionProjection?[budget.MaximumEntries];
                PageEntry? pending = null; int retained = 0; long retainedBytes = 0;
                try
                {
                    while (true)
                    {
                        Check(); int count = 0; long keyBytes = 0;
                        while (count < requests.Length)
                        {
                            Check(); if (pending is null && !entries.MoveNext()) break;
                            var e = pending ?? entries.Current; pending = null;
                            if (e.Key.Length > budget.MaximumKeyBytes - keyBytes) { pending = e; break; }
                            requests[count] = new(e, count); keyBytes += e.Key.Length; count++;
                        }
                        if (count == 0) yield break;
                        retained = count; retainedBytes = keyBytes; stats.RetainedEntries += count; stats.RetainedKeyBytes += keyBytes;
                        stats.MaximumRetainedEntries = Math.Max(stats.MaximumRetainedEntries, stats.RetainedEntries);
                        stats.MaximumRetainedKeyBytes = Math.Max(stats.MaximumRetainedKeyBytes, stats.RetainedKeyBytes);
                        reads.Windows++; if (history) stats.VersionWindows++; else stats.CurrentWindows++;
                        reads.MaximumBufferedEntries = Math.Max(reads.MaximumBufferedEntries, count); reads.MaximumBufferedKeyBytes = Math.Max(reads.MaximumBufferedKeyBytes, keyBytes);
                        Array.Sort(requests, 0, count, ComparisonRequest.PhysicalOrder);
                        for (var i = 0; i < count; i++)
                        {
                            Check(); var request = requests[i]; var address = request.Entry.Value; MetadataCodec.RequireKind(address, FrameKind.Semantic);
                            var body = segments.ReadComparison(address, frame, hash);
                            RetentionVersion projection;
                            if (history)
                            {
                                var fields = TimelineVersionDecoder.DecodeFields(body, request.Entry.Key, address, segments, Captured.Root.Sequence);
                                projection = new(new(fields.Path, fields.Sequence, fields.Ordinal, fields.Id), fields.StoredUtc, fields.Length, false);
                            }
                            else
                            {
                                var fields = ComparisonVersionDecoder.Decode(body, request.Entry.Key, address, segments, Captured.Root.Sequence);
                                projection = new(fields.Identity, default, fields.Length, true); // Current's stored time is never used.
                            }
                            results[request.Position] = new(projection, address, request.Entry.Key.Length); stats.SemanticRecordsValidated++;
                        }
                        Array.Clear(requests, 0, count);
                        for (var i = 0; i < count; i++)
                        {
                            Check(); var result = results[i]!; results[i] = null;
                            retained--; retainedBytes -= result.KeyBytes; stats.RetainedEntries--; stats.RetainedKeyBytes -= result.KeyBytes;
                            yield return result;
                        }
                        RetentionWindowCompleted?.Invoke(); Check();
                    }
                }
                finally { stats.RetainedEntries -= retained; stats.RetainedKeyBytes -= retainedBytes; }
            }
        }
        finally { _comparisonSessions.Remove(segments); }
    }
    private sealed record RetentionProjection(RetentionVersion Version, FrameAddress Address, int KeyBytes);
}
