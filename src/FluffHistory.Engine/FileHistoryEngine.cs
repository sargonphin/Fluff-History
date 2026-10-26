using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>Read-only source traversal and independent bounded file-version publication. No scheduling or run history.</summary>
public sealed class FileHistoryEngine
{
    private readonly Repository _repository;
    private readonly ScanOptions _options;
    private readonly SourceAccess _source;
    private readonly IRepositoryCapacityProbe _capacityProbe;
    private readonly IRepositoryAllocationProbe _allocationProbe;
    internal Action<RepositoryWriter>? ConfigureWriter { get; set; }
    internal Action<ScanResult>? BatchCommitted { get; set; }
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public FileHistoryEngine(Repository repository, ScanOptions? options = null, IRepositoryCapacityProbe? capacityProbe = null,
        IRepositoryAllocationProbe? allocationProbe = null) : this(repository, options ?? new(), new(), capacityProbe, allocationProbe) { }
    internal FileHistoryEngine(Repository repository, ScanOptions options, SourceAccess source,
        IRepositoryCapacityProbe? capacityProbe = null, IRepositoryAllocationProbe? allocationProbe = null)
    { ArgumentNullException.ThrowIfNull(repository); options.Validate(); _repository = repository; _options = options; _source = source;
        _capacityProbe = capacityProbe ?? new NativeRepositoryCapacityProbe(); _allocationProbe = allocationProbe ?? new NativeRepositoryAllocationProbe(); }

    /// <summary>Scan explicitly selected roots. Source skips are returned; repository/control failures throw.
    /// Cancellation returns committed progress with Cancelled=true. Omitted roots are left untouched.</summary>
    public ScanResult Scan(IEnumerable<SourceSelection> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var selections = sources.Take(1025).ToArray(); ValidateSelections(selections);
        var scanStoredUtc = Clock().ToUniversalTime();
        var result = new ScanResult(); var budget = new ScanBudget(_options, result);
        using var writer = _repository.OpenWriter(_options.WorkspaceRoot, streamMetadataReads: _options.Capacity is not null); ConfigureWriter?.Invoke(writer);
        result.Reuse = writer.ReusableStatistics;
        result.WriterAcquisitions = 1;
        if (writer.EnumeratePendingPublications().Any()) throw new IOException("Unresolved metadata publication; reconcile explicitly before scanning.");
        using (var starting = writer.OpenOwnedReader()) result.Authority = starting.Authority;
        RepositoryReader? baseline = null, history = null;
        ScanCapacitySession? capacity = null;
        var pending = new List<CopiedFile>(); var upserts = new List<VersionIdentity>(); var removes = new List<HistoryPath>();
        long pendingBytes = 0; bool publicationAttempted = false, destinationPayloadOperation = false;
        var buffer = new byte[128 * 1024];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.Capacity is not null)
            {
                capacity = new(_repository, writer, _options, result, _capacityProbe, _allocationProbe, RefreshHistory, cancellationToken);
                if (result.Capacity.ReclamationAuthorized) capacity.Owned(0);
            }
            baseline = _repository.OpenReader(); history = baseline; result.ComparisonAuthority = baseline.Authority;
            result.CurrentComparisonScans++;
            var current = new Dictionary<Guid, Dictionary<HistoryPath, CurrentState>>();
            foreach (var item in baseline.EnumerateCurrentComparison(cancellationToken: cancellationToken))
            {
                budget.Charge(item.Path.CanonicalPath.Length, 256);
                if (!current.TryGetValue(item.Path.SourceRootId, out var paths)) current.Add(item.Path.SourceRootId, paths = []);
                paths.Add(item.Path, new(item.Length, item.LastWriteTimeUtc)); result.ComparisonEntries++;
            }
            result.ComparisonBytesRequested = baseline.CurrentComparisonBytesRequested;
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var config = selection.Configuration; var root = selection.DirectoryPath;
                var excludedFiles = (selection.ExcludedFiles ?? []).Select(p => new HistoryPath(config.SourceRootId, p).CanonicalPath).ToHashSet(StringComparer.Ordinal);
                var safetyDirectories = (selection.SafetyExcludedDirectories ?? []).Select(p => new HistoryPath(config.SourceRootId, p).CanonicalPath).ToHashSet(StringComparer.Ordinal);
                var paths = current.GetValueOrDefault(config.SourceRootId) ?? [];
                // Protection changes end membership first, under the old permissive configuration.
                if (!config.Equals(baseline.GetSourceRoot(config.SourceRootId)))
                {
                    foreach (var pair in paths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Allowed(config, pair.Key, pair.Value.Length)) { Remove(pair.Key); pair.Value.Removed = true; }
                    }
                    Flush();
                    Commit(new(sourceRoots: [config]), 0, 0, 0);
                }
                if (!config.IsProtected) continue;
                var coverage = new Coverage(); var stack = new Stack<(string Physical, string Relative)>(); stack.Push((root, ""));
                try
                {
                    foreach (var ancestor in Ancestors(root).Reverse())
                    {
                        var attributes = Source(() => File.GetAttributes(ancestor));
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                            throw new SourceFailureException(new IOException("Source root/ancestor is not an ordinary directory."), SourceProblemKind.RootBoundary);
                    }
                }
                catch (SourceFailureException e) { result.Skip(config.SourceRootId, "", e.InnerException!, e.Kind); continue; }
                while (stack.TryPop(out var directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var names = new Dictionary<string, (string Display, bool Ambiguous)>(StringComparer.Ordinal);
                    try
                    {
                        var observed = Source(() => _source.Observe(directory.Physical));
                        if (!observed.Directory || observed.Unsupported) throw new SourceFailureException(new IOException("Unsupported directory boundary."), SourceProblemKind.DirectoryBoundary);
                        using var iterator = Source(() => _source.EnumerateNames(directory.Physical).GetEnumerator());
                        while (Source(iterator.MoveNext))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var name = iterator.Current; var relative = directory.Relative.Length == 0 ? name : directory.Relative + "/" + name;
                            budget.Charge(relative.Length);
                            try
                            {
                                var path = new HistoryPath(config.SourceRootId, relative);
                                var canonical = path.CanonicalPath;
                                if (names.TryGetValue(canonical, out var found)) names[canonical] = (found.Display, true);
                                else names.Add(canonical, (name, false));
                            }
                            catch (ArgumentException) { coverage.Block(directory.Relative, config.SourceRootId); result.Skip(config.SourceRootId, relative, "Unsupported logical name.", SourceProblemKind.UnsupportedName); }
                        }
                        result.DirectoriesEnumerated++;
                        if (directory.Relative.Length == 0) coverage.RootComplete = true;
                    }
                    catch (SourceFailureException e)
                    { coverage.Block(directory.Relative, config.SourceRootId); result.Skip(config.SourceRootId, directory.Relative, e.InnerException!, e.Kind); }
                    foreach (var entry in names)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = directory.Relative.Length == 0 ? entry.Value.Display : directory.Relative + "/" + entry.Value.Display;
                        var path = new HistoryPath(config.SourceRootId, relative);
                        // Policy barriers apply before attribute access/content opens, including inaccessible
                        // system metadata. They affect future traversal, never historical version deletion.
                        if (safetyDirectories.Contains(path.CanonicalPath) || excludedFiles.Contains(path.CanonicalPath))
                        { coverage.Block(relative, config.SourceRootId); continue; }
                        if (entry.Value.Ambiguous) { coverage.Block(relative, config.SourceRootId); result.Skip(config.SourceRootId, relative, "Ambiguous case alias.", SourceProblemKind.AmbiguousAlias); continue; }
                        paths.TryGetValue(path, out var previous);
                        var physical = Path.Combine(directory.Physical, entry.Value.Display);
                        SourceObservation observation;
                        try { observation = Source(() => _source.Observe(physical)); }
                        catch (SourceFailureException e) { coverage.Block(relative, config.SourceRootId); result.Skip(config.SourceRootId, relative, e.InnerException!, e.Kind); continue; }
                        // Positive ordinary-directory observation disproves the old exact file, even
                        // when a later policy barrier prevents traversal. Untrusted types prove nothing.
                        if (observation.Directory && !observation.Unsupported && previous is { Removed: false })
                        { Remove(path); previous.Removed = true; }
                        if (selection.MetadataExclusions?.Excludes(physical, observation.Attributes) == true)
                        { coverage.Block(relative, config.SourceRootId); continue; }
                        if (observation.Unsupported) { coverage.Block(relative, config.SourceRootId); result.Skip(config.SourceRootId, relative, "Unsupported/reparse/EFS boundary.", SourceProblemKind.UnsupportedBoundary); continue; }
                        if (observation.Directory)
                        {
                            if (config.ExcludedDirectories.Contains(path.CanonicalPath)) coverage.Block(relative, config.SourceRootId);
                            else stack.Push((physical, relative));
                            continue;
                        }
                        result.FilesEnumerated++;
                        if (previous is not null) previous.Seen = true;
                        ObserveFile(physical, path, relative, observation, previous, config, coverage);
                        if (pending.Count + upserts.Count + removes.Count >= _options.BatchSize) Flush();
                    }
                }
                Flush(); // Useful copies are independent of proof of absence.
                if (coverage.RootComplete)
                {
                    try
                    {
                        var end = Source(() => _source.Observe(root));
                        if (!end.Directory || end.Unsupported) throw new SourceFailureException(new IOException("Root became unsupported during traversal."), SourceProblemKind.RootBoundary);
                        foreach (var ancestor in Ancestors(root))
                            if ((Source(() => File.GetAttributes(ancestor)) & FileAttributes.ReparsePoint) != 0)
                                throw new SourceFailureException(new IOException("Root ancestor became a reparse boundary."), SourceProblemKind.RootBoundary);
                    }
                    catch (SourceFailureException e) { coverage.Block("", config.SourceRootId); result.Skip(config.SourceRootId, "", e.InnerException!, e.Kind); }
                }
                if (coverage.RootComplete)
                    foreach (var pair in paths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!pair.Value.Seen && !pair.Value.Removed && !coverage.Protects(pair.Key.CanonicalPath)) Remove(pair.Key);
                    }
                Flush();
            }
            result.Cancelled = cancellationToken.IsCancellationRequested;
            result.Outcome = result.Cancelled ? ScanOutcome.Cancelled : ScanOutcome.Completed; return result;
        }
        catch (ScanCapacityStop) { return result; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { result.Cancelled = true; result.Outcome = ScanOutcome.Cancelled; return result; }
        catch (Exception error) when (_options.Capacity is not null && destinationPayloadOperation && ScanCapacitySession.DiskFull(error))
        { result.Outcome = ScanOutcome.CapacityBlocked; result.ReconciliationMayBeRequired = publicationAttempted; return result; }
        catch (Exception error) when (_options.Capacity is not null)
        { result.Outcome = ScanOutcome.ControlError; result.ReconciliationMayBeRequired = publicationAttempted || error is CapacityRetentionException; throw new ScanCapacityException(result, error); }
        finally
        {
            result.ComparisonBytesRequested = baseline?.CurrentComparisonBytesRequested ?? 0;
            result.HistoryMetadataBytesRequested += baseline?.HistoryStatistics.BytesRequested ?? 0;
            if (history != baseline) result.HistoryMetadataBytesRequested += history?.HistoryStatistics.BytesRequested ?? 0;
            foreach (var file in pending) file.Draft.Dispose();
            if (history != baseline) history?.Dispose(); baseline?.Dispose();
        }

        void RefreshHistory()
        {
            if (baseline is null) return; // Initial retention precedes comparison capture.
            if (history != baseline)
            { result.HistoryMetadataBytesRequested += history?.HistoryStatistics.BytesRequested ?? 0; history?.Dispose(); history = null; }
            history = _repository.OpenReader(); result.HistoricalReaderRefreshes++;
        }

        void ObserveFile(string physical, HistoryPath path, string relative, SourceObservation observation,
            CurrentState? previous, SourceRootConfiguration config, Coverage coverage)
        {
            for (var attempt = 0; attempt < _options.MaximumSourceAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 0) result.RetryAttempts++;
                try
                {
                    if (attempt > 0) observation = Source(() => _source.Observe(physical));
                    while (true)
                    {
                        if (observation.Directory || observation.Unsupported || observation.Length > config.MaximumFileSize)
                        { coverage.Block(relative, config.SourceRootId); result.Skip(config.SourceRootId, relative, "Unsupported/oversized file.", SourceProblemKind.Oversized); return; }
                        if (previous is not null && !previous.Removed)
                        {
                            if (previous.Length == observation.Length && previous.Mtime == observation.LastWriteUtc) { result.Unchanged++; return; }
                        }
                        else
                        {
                            // Only a path lacking current membership seeks its own newest surviving history.
                            result.HistoricalLookups++;
                            var historical = history!.GetNewestVersion(path);
                            if (historical is not null && historical.Metadata.Length == observation.Length && historical.Metadata.LastWriteTimeUtc == observation.LastWriteUtc)
                            {
                                if (Admit(0, 1, 0, 0)) { observation = Source(() => _source.Observe(physical)); continue; }
                                upserts.Add(historical.Identity); return;
                            }
                        }
                        // Flush before selecting a candidate: later publication must not invalidate its proof or evict it.
                        if (HasPending() && (pending.Count + upserts.Count + removes.Count >= _options.BatchSize ||
                            observation.Length > _options.MaximumPendingPayloadBytes - pendingBytes))
                        {
                            var removalsBeforeFlush = result.Capacity.RemovalBatches; Flush();
                            if (removalsBeforeFlush != result.Capacity.RemovalBatches)
                            { observation = Source(() => _source.Observe(physical)); continue; }
                        }
                        using (var candidate = writer.FindReusablePayload(path, observation.Length, observation.LastWriteUtc, cancellationToken))
                        {
                            if (candidate is not null)
                            {
                                var fresh = Source(() => _source.Observe(physical));
                                if (fresh.Directory || fresh.Unsupported || fresh.Length != observation.Length || fresh.LastWriteUtc != observation.LastWriteUtc)
                                    throw new SourceFailureException(new IOException("Source changed during reusable local validation."), SourceProblemKind.SourceChanged);
                                observation = fresh; // Creation time/attributes are fresh restore metadata, never cache keys.
                                // Selected Ready state is vulnerable pending work: observe only, never retain history underneath it.
                                if (capacity is null || capacity.Assess(capacity.Estimate(Plan(1, 0, 0), 0).TotalBytes))
                                {
                                    var checksum = candidate.Checksum;
                                    if (writer.TryAdoptReusablePayload(candidate) is { } adopted)
                                    {
                                        PeakPending(observation.Length);
                                        var freshMetadata = new LogicalFileMetadata(config.SourceRootId, relative, observation.Length, observation.CreationUtc,
                                            observation.LastWriteUtc, observation.Attributes & LogicalFileMetadata.SupportedAttributes);
                                        pending.Add(new(adopted, freshMetadata, checksum)); pendingBytes = checked(pendingBytes + observation.Length);
                                        result.SourceContentBytesAvoided = checked(result.SourceContentBytesAvoided + observation.Length);
                                        if (pendingBytes >= _options.MaximumPendingPayloadBytes) Flush();
                                        return;
                                    }
                                }
                                // Release before ordinary admission: disposable bytes may be evicted, then copy only if space permits.
                            }
                        }
                        if (Admit(1, 0, 0, observation.Length)) { observation = Source(() => _source.Observe(physical)); continue; }
                        using var input = Source(() => _source.OpenRead(physical)); result.ContentStreamsOpened++;
                        if (!observation.Matches(Source(() => _source.ObserveHandle(input)))) throw new SourceFailureException(new IOException("Source changed before copy."), SourceProblemKind.SourceChanged);
                        destinationPayloadOperation = true;
                        var draft = writer.CreatePayloadDraft();
                        destinationPayloadOperation = false;
                        PeakPending(0);
                        try
                        {
                            var hash = new XxHash64(); long copied = 0;
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var wanted = (int)Math.Min(buffer.Length, observation.Length - copied + (copied == observation.Length ? 1 : 0));
                                int read;
                                try { read = _source.Read(input, buffer.AsSpan(0, wanted)); }
                                catch (Exception e) when (SourceError(e)) { throw new SourceFailureException(e); }
                                result.SourceBytesRead += read;
                                if (read == 0) break;
                                copied += read;
                                if (copied > observation.Length) throw new SourceFailureException(new IOException("Source grew during copy."), SourceProblemKind.SourceChanged);
                                hash.Append(buffer.AsSpan(0, read));
                                destinationPayloadOperation = true; draft.Write(buffer.AsSpan(0, read)); destinationPayloadOperation = false;
                                result.PayloadBytesWritten += read; PeakPending(copied);
                            }
                            if (copied != observation.Length || !observation.Matches(Source(() => _source.ObserveHandle(input))) ||
                                !observation.Matches(Source(() => _source.Observe(physical))))
                                throw new SourceFailureException(new IOException("Source changed during copy."), SourceProblemKind.SourceChanged);
                            destinationPayloadOperation = true; draft.CompleteCopy(copied); destinationPayloadOperation = false;
                            var metadata = new LogicalFileMetadata(config.SourceRootId, relative, copied, observation.CreationUtc,
                                observation.LastWriteUtc, observation.Attributes & LogicalFileMetadata.SupportedAttributes);
                            var checksum = new XxHash64Checksum(Convert.ToHexString(hash.GetCurrentHash()));
                            writer.MarkPayloadReusable(draft, path, observation.LastWriteUtc, checksum);
                            pending.Add(new(draft, metadata, checksum)); pendingBytes = checked(pendingBytes + copied);
                            // One file may exceed the normal target; publish it alone promptly.
                            if (pendingBytes >= _options.MaximumPendingPayloadBytes) Flush();
                            return;
                        }
                        catch { draft.Dispose(); throw; }
                    }
                }
                catch (SourceFailureException e)
                {
                    if (attempt + 1 == _options.MaximumSourceAttempts)
                    { coverage.Block(relative, config.SourceRootId); result.Skip(config.SourceRootId, relative, e.InnerException!, e.Kind); }
                }
            }
        }
        void PeakPending(long activeBytes)
        {
            result.PeakPendingPayloadCount = Math.Max(result.PeakPendingPayloadCount, pending.Count + 1L);
            result.PeakPendingPayloadBytes = Math.Max(result.PeakPendingPayloadBytes, checked(pendingBytes + activeBytes));
        }
        bool HasPending() => pending.Count + upserts.Count + removes.Count != 0;
        PublicationAllocationRequest Plan(int v = 0, int u = 0, int r = 0) => new(pending.Count + v, upserts.Count + u, removes.Count + r);
        // Return true if retention invalidated the source observation/history decision.
        bool Admit(int v, int u, int r, long nextPayload)
        {
            var removedBefore = result.Capacity.RemovalBatches;
            if (HasPending() && (pending.Count + upserts.Count + removes.Count >= _options.BatchSize ||
                nextPayload > _options.MaximumPendingPayloadBytes - pendingBytes)) Flush();
            if (capacity is not null)
            {
                var estimate = capacity.Estimate(Plan(v, u, r), nextPayload, newPayload: v != 0);
                if (HasPending())
                {
                    if (!capacity.Assess(estimate.TotalBytes))
                    {
                        Flush(); // Final observation may stop safely; no retention under pending state.
                        estimate = capacity.Estimate(Plan(v, u, r), nextPayload, newPayload: v != 0); capacity.Owned(estimate.TotalBytes);
                    }
                }
                else capacity.Owned(estimate.TotalBytes);
            }
            return removedBefore != result.Capacity.RemovalBatches;
        }
        void Remove(HistoryPath path) { _ = Admit(0, 0, 1, 0); removes.Add(path); if (removes.Count >= _options.BatchSize) Flush(); }
        void Flush()
        {
            if (pending.Count + upserts.Count + removes.Count == 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (capacity is not null) capacity.RequirePublication(capacity.Estimate(Plan(), 0).TotalBytes);
            var versions = pending.Select(f => f.Draft.PrepareVersion(f.Metadata, scanStoredUtc, f.Checksum)).ToArray();
            publicationAttempted = true;
            using var publication = writer.PrepareMutation(new(versions, upserts, removes), result.Authority, cancellationToken);
            foreach (var file in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destinationPayloadOperation = true; file.Draft.Install(); destinationPayloadOperation = false;
            }
            var mutation = publication.Commit(cancellationToken);
            Record(mutation, pending.Count, upserts.Count, removes.Count);
            foreach (var file in pending) file.Draft.Dispose();
            pending.Clear(); upserts.Clear(); removes.Clear();
            pendingBytes = 0; publicationAttempted = false;
            if (mutation.Changed) BatchCommitted?.Invoke(result);
            if (result.Capacity.ReclamationAuthorized) capacity!.Owned(0);
        }
        void Commit(MutationBatch batch, int versions, int reactivations, int removed)
        {
            if (capacity is not null)
            {
                var estimate = capacity.Estimate(new(batch.Versions.Count, batch.CurrentUpserts.Count, batch.CurrentRemovals.Count, batch.SourceRoots.Count), 0);
                capacity.Owned(estimate.TotalBytes);
                capacity.RequirePublication(capacity.Estimate(new(batch.Versions.Count, batch.CurrentUpserts.Count, batch.CurrentRemovals.Count, batch.SourceRoots.Count), 0).TotalBytes);
            }
            publicationAttempted = true;
            var mutation = writer.Apply(batch, result.Authority, cancellationToken); Record(mutation, versions, reactivations, removed);
            publicationAttempted = false;
            if (mutation.Changed) BatchCommitted?.Invoke(result);
            if (result.Capacity.ReclamationAuthorized) capacity!.Owned(0);
        }
        void Record(MutationResult mutation, int versions, int reactivations, int removed)
        {
            result.Authority = mutation.Authority;
            if (!mutation.Changed) return;
            result.MetadataBytesAppended = checked(result.MetadataBytesAppended + writer.LastPublicationMetadataBytes);
            result.BatchesCommitted++; result.VersionsCreated += versions; result.Reactivations += reactivations; result.Removals += removed;
        }
    }
    private static bool SourceError(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
    private static T Source<T>(Func<T> read)
    { try { return read(); } catch (Exception e) when (SourceError(e)) { throw new SourceFailureException(e); } }
    private static bool Allowed(SourceRootConfiguration config, HistoryPath path, long length) => config.IsProtected && length <= config.MaximumFileSize &&
        !config.ExcludedDirectories.Any(p => path.CanonicalPath.StartsWith(p + "/", StringComparison.Ordinal));
    private void ValidateSelections(SourceSelection[] selections)
    {
        if (selections.Length > 1024 || selections.Any(s => s is null || s.Configuration is null || !Path.IsPathFullyQualified(s.DirectoryPath)) ||
            selections.Select(s => s.Configuration.SourceRootId).Distinct().Count() != selections.Length)
            throw new ArgumentException("At most 1024 distinct configured roots with absolute runtime locations are required.");
        var locations = selections.Select(s => PhysicalPaths.Normalize(s.DirectoryPath)).ToArray();
        var keys = locations.Select(PhysicalPaths.OverlapKey).ToArray();
        var repository = PhysicalPaths.OverlapKey(PhysicalPaths.Normalize(_repository.DirectoryPath));
        for (var i = 0; i < locations.Length; i++)
        {
            if (selections[i].Configuration.IsProtected && PhysicalPaths.Overlaps(keys[i], repository)) throw new ArgumentException("Source/repository nesting is forbidden.");
            for (var j = 0; j < i; j++)
                if (selections[i].Configuration.IsProtected && selections[j].Configuration.IsProtected && PhysicalPaths.Overlaps(keys[i], keys[j]) &&
                    !ExcludedChild(i, j) && !ExcludedChild(j, i)) throw new ArgumentException("Overlapping source roots require an exact excluded child subtree.");
            if (selections[i].ExcludedFiles is { } files && (files.Count > 256 || files.Any(p => string.IsNullOrWhiteSpace(p)))) throw new ArgumentException("Invalid bounded file exclusions.");
            if (selections[i].SafetyExcludedDirectories is { } dirs && (dirs.Count > 256 || dirs.Any(p => string.IsNullOrWhiteSpace(p)))) throw new ArgumentException("Invalid bounded subtree exclusions.");
            if (selections[i].MetadataExclusions is { } metadata)
            {
                var folders = metadata.ManagedShortcutFolders.Concat(metadata.OneDriveFolders).Take(65).ToArray();
                if (folders.Length > 64 || folders.Any(p => !Path.IsPathFullyQualified(p))) throw new ArgumentException("Invalid bounded metadata policy.");
                selections[i] = selections[i] with { MetadataExclusions = new(metadata.ManagedShortcutFolders.Select(Path.GetFullPath).ToArray(), metadata.OneDriveFolders.Select(Path.GetFullPath).ToArray()) };
            }
            selections[i] = selections[i] with { DirectoryPath = locations[i] };
        }
        bool ExcludedChild(int parent, int child)
        {
            if (keys[parent].Equals(keys[child], StringComparison.OrdinalIgnoreCase) || !PhysicalPaths.Overlaps(keys[parent], keys[child])) return false;
            var relative = Path.GetRelativePath(locations[parent], locations[child]);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
            var canonical = new HistoryPath(selections[parent].Configuration.SourceRootId, relative.Replace(Path.DirectorySeparatorChar, '/')).CanonicalPath;
            return selections[parent].Configuration.ExcludedDirectories.Any(e => canonical == e || canonical.StartsWith(e + "/", StringComparison.Ordinal));
        }
    }
    private static IEnumerable<string> Ancestors(string path) { for (string? p = path; p is not null; p = Path.GetDirectoryName(p)) yield return p; }
    private sealed class CurrentState(long length, DateTimeOffset mtime)
    { internal long Length => length; internal DateTimeOffset Mtime => mtime; internal bool Seen, Removed; }
    private sealed record CopiedFile(PayloadDraft Draft, LogicalFileMetadata Metadata, XxHash64Checksum Checksum);
    private sealed class Coverage
    {
        internal bool RootComplete;
        private readonly HashSet<string> _barriers = new(StringComparer.Ordinal);
        internal void Block(string relative, Guid root) => _barriers.Add(relative.Length == 0 ? "" : new HistoryPath(root, relative).CanonicalPath);
        internal bool Protects(string canonical)
        {
            if (_barriers.Contains("")) return true;
            for (var path = canonical; ;)
            {
                if (_barriers.Contains(path)) return true;
                var slash = path.LastIndexOf('/'); if (slash < 0) return false; path = path[..slash];
            }
        }
    }
}
