using FluffHistory.Core;

namespace FluffHistory.Storage;

internal sealed record FoldedVersion(byte[] Key, VersionIdentity Identity, FrameAddress? Addition, FrameAddress? Removal);

/// <summary>Explicit committed-history reconstruction. Replaceable indexes never decide survival.</summary>
internal sealed class SemanticHistoryFold : IDisposable
{
    private readonly RepositoryReader _reader;
    private readonly HistoryScratch _scratch;
    private readonly HistoryScratch.Run _keys;
    private readonly CancellationToken _token;
    internal Dictionary<HistoryPath, FrameAddress?> Decisions { get; } = [];
    internal Dictionary<Guid, SourceRootConfiguration> Configurations { get; } = [];
    internal Dictionary<Guid, FrameAddress> ConfigurationAddresses { get; } = [];
    internal SortedDictionary<byte[], FrameAddress> Current { get; } = new(PathKeys.Comparer);
    internal HistoryFoldStatistics Statistics => _scratch.Statistics;
    internal static readonly IComparer<HistoryEvent> KeyOrder = Comparer<HistoryEvent>.Create((a, b) =>
    {
        var order = PathKeys.Comparer.Compare(a.Key, b.Key); return order != 0 ? order : a.Address.Serial.CompareTo(b.Address.Serial);
    });
    private static readonly IComparer<HistoryEvent> IdOrder = Comparer<HistoryEvent>.Create((a, b) =>
    {
        var order = a.Id.CompareTo(b.Id); return order != 0 ? order : KeyOrder.Compare(a, b);
    });
    internal SemanticHistoryFold(RepositoryReader reader, MetadataWorkspace workspace, MaintenanceLimits limits,
        CancellationToken token, int bufferEntries = 4096)
    {
        ValidateLimits(limits); ArgumentOutOfRangeException.ThrowIfLessThan(bufferEntries, 1);
        _reader = reader; _token = token;
        _scratch = new(Path.Combine(workspace.DirectoryPath, "history-fold"), limits, token, workspace.Crypto);
        try
        {
            var keys = new HistoryRuns(_scratch, KeyOrder, bufferEntries); var ids = new HistoryRuns(_scratch, IdOrder, bufferEntries);
            for (var cursor = reader.Captured.Root.Journal; cursor is not null;)
            {
                token.ThrowIfCancellationRequested();
                if (++Statistics.Records > limits.MaximumRecords) throw new IOException("Maintenance semantic-record budget exceeded.");
                var record = reader.Semantic(cursor);
                switch (record.Kind)
                {
                    case SemanticKind.VersionAdded:
                        Add(record.Version!.Identity, SemanticKind.VersionAdded); Statistics.Additions++; break;
                    case SemanticKind.VersionRemoved:
                        Add(record.RemovedVersion!, SemanticKind.VersionRemoved); Statistics.Removals++; break;
                    case SemanticKind.CurrentUpsert:
                        _ = reader.Version(record.VersionAddress!, record.Path);
                        Decisions.TryAdd(record.Path!, record.VersionAddress); break;
                    case SemanticKind.CurrentRemoved: Decisions.TryAdd(record.Path!, null); break;
                    case SemanticKind.SourceRoot:
                        if (Configurations.TryAdd(record.Source!.SourceRootId, record.Source)) ConfigurationAddresses.Add(record.Source.SourceRootId, cursor);
                        break;
                }
                if (Decisions.Count > limits.MaximumCurrentPaths || Configurations.Count > limits.MaximumCurrentPaths)
                    throw new IOException("Maintenance membership budget exceeded.");
                cursor = record.Previous;
                void Add(VersionIdentity identity, SemanticKind kind)
                {
                    var item = new HistoryEvent(PathKeys.Version(identity), identity.VersionId, kind, cursor);
                    keys.Add(item); ids.Add(item);
                }
            }
            _keys = keys.Finish(); var uuidRun = ids.Finish();
            using (var uuids = new HistoryScratch.Reader(uuidRun, IdOrder, token))
            {
                HistoryEvent? previous = null;
                while (uuids.MoveNext())
                {
                    var item = uuids.Current!;
                    Format.Require(previous is null || previous.Id != item.Id || previous.Key.AsSpan().SequenceEqual(item.Key), "version UUID aliases unrelated keys");
                    previous = item;
                }
            }
            _scratch.Delete(uuidRun);
            foreach (var group in Groups())
            {
                if (group.Addition is not null && group.Removal is null) Statistics.Survivors++;
                if (!Decisions.TryGetValue(group.Identity.Path, out var selected) || selected is null || selected != group.Addition) continue;
                Format.Require(group.Removal is null, "current version was removed");
                var version = reader.Version(selected, group.Identity.Path, group.Key);
                Format.Require(Configurations.TryGetValue(group.Identity.Path.SourceRootId, out var source) && source.IsProtected &&
                    version.Metadata.Length <= source.MaximumFileSize &&
                    !source.ExcludedDirectories.Any(e => group.Identity.Path.CanonicalPath.StartsWith(e + "/", StringComparison.Ordinal)), "current outside protection configuration");
                Current.Add(PathKeys.Current(group.Identity.Path), selected);
            }
            Format.Require(Current.Count == Decisions.Count(p => p.Value is not null), "current references a non-surviving/outside-chain version");
        }
        catch { _scratch.Dispose(); throw; }
    }
    internal static void ValidateLimits(MaintenanceLimits limits)
    {
        if (limits.MaximumRecords <= 0 || limits.MaximumCurrentPaths <= 0 || limits.MaximumOutputBytes <= 0 ||
            limits.MaximumScratchBytes <= 0 || limits.MaximumScratchBytesWritten <= 0 || limits.MaximumMaterializedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    internal IEnumerable<FoldedVersion> Groups()
    {
        using var input = new HistoryScratch.Reader(_keys, KeyOrder, _token);
        HistoryEvent? first = null; FrameAddress? addition = null, removal = null;
        while (input.MoveNext())
        {
            var item = input.Current!;
            if (first is not null && !first.Key.AsSpan().SequenceEqual(item.Key))
            {
                yield return Result(); first = null; addition = null; removal = null;
            }
            first ??= item;
            Format.Require(first.Id == item.Id, "conflicting UUID at version key");
            if (item.Kind == SemanticKind.VersionAdded)
            {
                Format.Require(addition is null && removal is null, "duplicate addition or removal before addition"); addition = item.Address;
            }
            else { Format.Require(removal is null, "duplicate version removal"); removal = item.Address; }
        }
        if (first is not null) yield return Result();
        FoldedVersion Result() => new(first!.Key, PathKeys.DecodeVersion(first.Key, first.Id), addition, removal);
    }
    internal void ValidateIndexes()
    {
        using var indexed = _reader.VersionIndex.Range(_reader.Captured.Root.Versions).GetEnumerator();
        foreach (var (group, version) in ReadVersions())
        {
            if (group.Removal is not null) continue;
            Format.Require(indexed.MoveNext() && indexed.Current.Key.AsSpan().SequenceEqual(group.Key) && indexed.Current.Value == group.Addition,
                "journal/version index survivor disagreement");
            Format.Require(version!.Identity == group.Identity, "survivor UUID binding");
        }
        Format.Require(!indexed.MoveNext(), "unexplained indexed version");
        using var current = _reader.CurrentIndex.Range(_reader.Captured.Root.Current).GetEnumerator();
        foreach (var entry in Current)
            Format.Require(current.MoveNext() && current.Current.Key.AsSpan().SequenceEqual(entry.Key) && current.Current.Value == entry.Value, "journal/current index disagreement");
        Format.Require(!current.MoveNext(), "extra indexed current");
        long sources = 0;
        foreach (var source in _reader.EnumerateSourceRoots())
            Format.Require(++sources <= Configurations.Count && Configurations.TryGetValue(source.SourceRootId, out var expected) && expected.Equals(source),
                "journal/source index disagreement");
        Format.Require(sources == Configurations.Count, "missing indexed source configuration");
    }

    // Path-major order can scatter semantic frames across many segments. Decode one bounded
    // window in physical order, then yield in the original exact key order. Every frame and
    // key/UUID binding is still validated; this is not a new graph or persistent cache.
    internal IEnumerable<(FoldedVersion Group, StoredVersion? Version)> ReadVersions()
    {
        var window = new List<FoldedVersion>(); var bytes = 0;
        foreach (var group in Groups())
        {
            _token.ThrowIfCancellationRequested();
            if (window.Count == 4096 || bytes + group.Key.Length > 4 * 1024 * 1024)
            {
                foreach (var value in ReadWindow()) yield return value;
                window.Clear(); bytes = 0;
            }
            window.Add(group); bytes += group.Key.Length;
        }
        foreach (var value in ReadWindow()) yield return value;

        IEnumerable<(FoldedVersion, StoredVersion?)> ReadWindow()
        {
            var versions = new StoredVersion?[window.Count];
            foreach (var item in window.Select((group, position) => (group, position))
                .Where(p => p.group.Removal is null)
                .OrderBy(p => p.group.Addition!.SegmentOrdinal).ThenBy(p => p.group.Addition!.Offset))
            {
                _token.ThrowIfCancellationRequested();
                var version = _reader.Version(item.group.Addition!, versionKey: item.group.Key);
                Format.Require(version.Identity == item.group.Identity, "survivor UUID binding");
                versions[item.position] = version;
            }
            for (var i = 0; i < window.Count; i++) { _token.ThrowIfCancellationRequested(); yield return (window[i], versions[i]); }
        }
    }
    public void Dispose() => _scratch.Dispose();
}
