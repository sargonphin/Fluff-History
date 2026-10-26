using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

// Phase A alternatives live in tests. Shared physical fixtures can be retained explicitly
// on disposable storage so final production measurements do not rebuild the large fixture.
public sealed class TimelineResponsivenessMeasurementTests(ITestOutputHelper output)
{
    internal static string Name(int i) => $"{(i % 10 == 0 ? "Pictures" : "Documents")}/area{i / 10000:D2}/folder{i / 1000:D3}/document{i:D6}.txt";
    internal static DateTimeOffset Time(string shape, int paths, int pass, int i) => shape switch
    {
        "unique" => FoundationTests.Time.AddTicks(pass == 0 ? i * 2L + 1 : (paths - i) * 2L),
        "retained" => FoundationTests.Time.AddDays(pass == 0 ? i % 11 == 0 ? 65 + i % 7 : i % 45 : i % 17 == 0 ? 8 : 60 + i % 12),
        _ => FoundationTests.Time.AddDays(pass == 1 && i % 3 == 0 ? -1 : i % 5)
    };

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void MeasureBackupMaintenanceAlternatives()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var source = temp.Child("source"); Directory.CreateDirectory(source);
        for (var i = 0; i < 64; i++) File.WriteAllText(Path.Combine(source, $"file{i:D3}"), "first");
        var selection = new SourceSelection(new(FoundationTests.Root, source), source);
        var engine = new FileHistoryEngine(repo); var initial = engine.Scan([selection]);
        using var capture = repo.OpenReader(); var prototype = new Prototype();
        prototype.Build(capture, new(FoundationTests.Root));
        var before = Inventory(repo); var watch = Stopwatch.StartNew(); var noChange = engine.Scan([selection]); watch.Stop();
        Assert.Equal(before, Inventory(repo)); Assert.Equal(initial.Authority, noChange.Authority);
        Assert.Equal(0, noChange.BatchesCommitted); Assert.Equal(0, noChange.PayloadBytesWritten);
        var noChangeMs = watch.Elapsed.TotalMilliseconds;
        File.WriteAllText(Path.Combine(source, "file000"), "changed-longer");
        watch.Restart(); var changed = engine.Scan([selection]); watch.Stop(); var changedMs = watch.Elapsed.TotalMilliseconds;
        Assert.NotEqual(noChange.Authority, changed.Authority);
        using var fresh = repo.OpenReader(); var allocation = GC.GetTotalAllocatedBytes(true); watch.Restart();
        prototype.Build(fresh, new(FoundationTests.Root)); watch.Stop();
        output.WriteLine("M18_BACKUP_MODEL " + JsonSerializer.Serialize(new { paths = 64, noChangeMs, changedMs,
            noChangeHistoryTraversals = 0, noChange.BatchesCommitted, noChange.PayloadBytesWritten,
            transientMaintenanceWrites = 0, transientMaintenanceHistoryTraversals = 0,
            eagerDerivedRebuild = new { elapsedMs = watch.Elapsed.TotalMilliseconds, allocations = GC.GetTotalAllocatedBytes(true) - allocation,
                versions = fresh.LastTimelineStatistics!.ValidatedVersions, bytes = fresh.LastTimelineStatistics.Reads.DestinationBytesRequested,
                opens = fresh.LastTimelineStatistics.Reads.Io.SegmentOpens,
                note = "extra measured post-backup rebuild for an eager persistent-maintenance alternative; lazy invalidation avoids this tax" } }));
    }

    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData("small", 100)] [InlineData("moderate", 25_000)]
    [InlineData("large", 250_000)] [InlineData("unique", 25_000)] [InlineData("retained", 4_000)]
    public void CompareArchitectures(string shape, int paths)
    {
        using var temporary = new TemporaryDirectory();
        var repo = OpenFixture(temporary, shape, paths); var directory = repo.DirectoryPath;
        var expected = Expected(shape, paths);
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(directory, "state.json"));
        var root = new HistoryScope(FoundationTests.Root);
        var steps = new List<object>();
        using (var baseline = repo.OpenReader())
        {
            HistoryCursor? cursor = null;
            for (var page = 0; page < 3; page++)
            {
                var p = Measure("A-grouped", "next-" + page, baseline, root, expected, steps,
                    () => baseline.ReadTimelinePage(new(), root, 2, HistoryDirection.NewestFirst, cursor));
                cursor = p.Continuation; if (cursor is null) break;
            }
            Measure("A-grouped", "reverse", baseline, root, expected, steps,
                () => baseline.ReadTimelinePage(new(), root, 2, HistoryDirection.OldestFirst));
            Measure("A-grouped", "return-first", baseline, root, expected, steps,
                () => baseline.ReadTimelinePage(new(), root, 2));
        }
        var prototype = new Prototype();
        using (var r = repo.OpenReader())
        {
            var cold = Measure("B-transient-prototype", "build", r, root, expected, steps, () =>
            {
                prototype.Build(r, root); return prototype.Page(2, HistoryDirection.NewestFirst);
            }, prototype);
            Assert.NotNull(cold.Continuation);
            Measure("B-transient-prototype", "second", r, root, expected, steps,
                () => prototype.Page(2, HistoryDirection.NewestFirst, cold.Continuation!.Time), prototype);
            Measure("B-transient-prototype", "reverse", r, root, expected, steps,
                () => prototype.Page(2, HistoryDirection.OldestFirst), prototype);
            Measure("B-transient-prototype", "return-first", r, root, expected, steps,
                () => prototype.Page(2, HistoryDirection.NewestFirst), prototype);
            Measure("B-transient-prototype", "page-size", r, root, expected, steps,
                () => prototype.Page(128, HistoryDirection.NewestFirst), prototype);

            // Actual diagnostic-only scope-file write/read, outside the repository. A pure
            // root timestamp file cannot answer arbitrary folder membership; model that separately.
            var ticksFile = temporary.Child("derived-ticks");
            var watch = Stopwatch.StartNew();
            using (var file = new FileStream(ticksFile, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(new byte[128]); // Explicit model allowance, not a proposed durable codec.
                foreach (var tick in prototype.Ticks) writer.Write(tick);
            }
            watch.Stop(); var writeMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); var bytes = File.ReadAllBytes(ticksFile); watch.Stop();
            Assert.Equal(128 + 8L * expected.Length, bytes.LongLength);
            output.WriteLine("M18_MODEL " + JsonSerializer.Serialize(new { shape, candidate = "D-scope-file",
                actualDerivedFileBytes = bytes.Length, writeMs, reloadMs = watch.Elapsed.TotalMilliseconds,
                rebuildHistoryTraversalsPerAuthority = 1, eagerMaintenanceBackupTax = "one measured full scope build per changed authority; zero on unchanged authority",
                arbitraryFolderMembership = "needs separate per-scope files or per-version root/path membership; root ticks alone are insufficient",
                repositoryWideRowBytesLowerModel = 2L * Enumerable.Range(0, paths).Sum(i => 28L + System.Text.Encoding.UTF8.GetByteCount(Name(i))),
                invalidation = "all backup/removal/rebuild/compaction authority changes discard exact-bound file; no format/root change" }));
        }
        // Owner's one-active-scope flow. Returning to root deliberately rebuilds.
        using (var r = repo.OpenReader())
        {
            foreach (var folder in new[] { "Documents", "Documents/area00", "Documents/area00/folder000", "Pictures/area00", "" })
            {
                var scope = new HistoryScope(FoundationTests.Root, folder); var exact = Expected(shape, paths, scope);
                Measure("A-grouped", "folder-" + folder, r, scope, exact, steps,
                    () => r.ReadTimelinePage(new(), scope, 2));
                Measure("B-transient-prototype", "folder-" + folder, r, scope, exact, steps, () =>
                { prototype.Build(r, scope); return prototype.Page(2, HistoryDirection.NewestFirst); }, prototype);
            }
        }
        var views = new List<object>();
        using (var r = repo.OpenReader())
        {
            foreach (var folder in new[] { "", "Documents/area00/folder000", "Pictures/area00", "" })
            {
                var scope = new HistoryScope(FoundationTests.Root, folder);
                foreach (var at in new DateTimeOffset?[] { null, expected[^1], expected[expected.Length / 2] })
                {
                    var prior = r.HistoryStatistics; var allocation = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                    var page = r.GetScopeHistoryPage(scope, new(at), 128); watch.Stop(); var after = r.HistoryStatistics;
                    views.Add(new { folder, at, returned = page.Items.Count, elapsedMs = watch.Elapsed.TotalMilliseconds,
                        allocations = GC.GetTotalAllocatedBytes(true) - allocation,
                        pages = after.IndexPagesRead - prior.IndexPagesRead, frames = after.SemanticFramesResolved - prior.SemanticFramesResolved,
                        opens = after.SegmentOpens - prior.SegmentOpens, bytes = after.BytesRequested - prior.BytesRequested });
                }
            }
            var watchPath = Stopwatch.StartNew(); var pathPage = r.GetPathHistoryPage(new(FoundationTests.Root, Name(paths / 2)));
            watchPath.Stop(); views.Add(new { exactPath = Name(paths / 2), returned = pathPage.Items.Count, elapsedMs = watchPath.Elapsed.TotalMilliseconds });
        }
        Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(directory, "state.json")));
        output.WriteLine("M18_PHASE_A " + JsonSerializer.Serialize(new { shape, paths,
            survivingVersions = ExpectedCount(shape, paths), distinctTimes = expected.Length,
            segments = before.Count(p => p.Key.EndsWith(".seg", StringComparison.Ordinal)),
            metadataBytes = before.Where(p => p.Key.EndsWith(".seg", StringComparison.Ordinal)).Sum(p => p.Value.Length),
            expectedDigest = TimelineReadMeasurementTests.Digest(expected), steps, scopeViews = views, persistentRepositoryBytesWritten = 0 }));
    }

    private static Repository OpenFixture(TemporaryDirectory temporary, string shape, int paths)
    {
        var shared = Environment.GetEnvironmentVariable("FLUFF_M18_FIXTURES");
        var version = shape == "retained" ? 2 : 1;
        var directory = shared is null ? temporary.Child() : Path.Combine(shared, $"m18-v{version}-" + shape);
        var marker = directory + ".complete";
        if (shared is not null && File.Exists(marker))
        { Assert.Equal($"{shape}|{paths}|{version}", File.ReadAllText(marker)); return Repository.Open(directory); }
        var repo = Repository.Create(directory);
        MetadataDiagnosticFixture.Populate(repo, paths, storedTime: (p, i) => Time(shape, paths, p, i), name: Name);
        if (shape == "retained") ShapeRetention(repo, paths);
        if (shared is not null) File.WriteAllText(marker, $"{shape}|{paths}|{version}");
        return repo;
    }

    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData("small", 100)] [InlineData("moderate", 25_000)]
    [InlineData("large", 250_000)] [InlineData("unique", 25_000)] [InlineData("retained", 4_000)]
    public void MeasureSelectedSnapshotAndBrowserFlow(string shape, int paths)
    {
        using var temporary = new TemporaryDirectory(); var repo = OpenFixture(temporary, shape, paths);
        var inventory = Inventory(repo); var state = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var expected = Expected(shape, paths); var steps = new List<object>(); var views = new List<object>();
        var folders = new[] { "Documents/area00", "Documents/area00/folder000", "Pictures/area00", "" };
        var oracles = folders.ToDictionary(f => new HistoryScope(FoundationTests.Root, f).Folder,
            f => (Times: Expected(shape, paths, new(FoundationTests.Root, f)), Count: ExpectedVersionCount(shape, paths, new(FoundationTests.Root, f))));
        double timelineMs = 0, viewMs = 0; long backendAllocations = 0;
        using var reader = repo.OpenReader();
        var root = new HistoryScope(FoundationTests.Root); var allocations = GC.GetTotalAllocatedBytes(true); var total = Stopwatch.StartNew();
        var page = Timeline("next-0", root, 2); View("open-root-current", root, null);
        var cursor = page.Continuation;
        for (var i = 1; i < 3 && cursor is not null; i++)
        {
            page = Timeline("next-" + i, root, 2, cursor: cursor); cursor = page.Continuation;
            View("view-next-" + i, root, page.Items[0]);
        }
        page = Timeline("reverse", root, 2, HistoryDirection.OldestFirst); View("view-reverse", root, page.Items[0]);
        page = Timeline("return-first", root, 2); View("view-return-first", root, page.Items[0]);
        var fivePageTimelineMs = timelineMs;
        _ = Timeline("page-size", root, 128);
        foreach (var folder in folders)
        {
            var scope = new HistoryScope(FoundationTests.Root, folder); var exact = oracles[scope.Folder].Times;
            var first = Timeline("folder-" + folder, scope, 2); View("folder-view-" + folder, scope, first.Items.FirstOrDefault());
            _ = Timeline("folder-repeat-" + folder, scope, 2, HistoryDirection.OldestFirst);
            Assert.Equal(exact.Length, reader.TimelineCacheStatistics.RetainedTimestamps);
        }
        var pathAllocation = GC.GetTotalAllocatedBytes(true); var pathWatch = Stopwatch.StartNew(); var path = reader.GetPathHistoryPage(new(FoundationTests.Root, Name(paths / 2))); pathWatch.Stop();
        backendAllocations += GC.GetTotalAllocatedBytes(true) - pathAllocation;
        total.Stop(); var allocated = GC.GetTotalAllocatedBytes(true) - allocations;
        Assert.Null(reader.Workspace); Assert.Equal(inventory, Inventory(repo));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        Assert.Equal(5, reader.TimelineCacheStatistics.Builds); Assert.Equal(4, reader.TimelineCacheStatistics.ScopeChanges);
        output.WriteLine("M18_PHASE_B " + JsonSerializer.Serialize(new { shape, paths, survivingVersions = ExpectedCount(shape, paths),
            distinctTimes = expected.Length, pageSize = 2, expectedDigest = TimelineReadMeasurementTests.Digest(expected), steps,
            browserScopeViews = views, pathHistory = new { returned = path.Items.Count, elapsedMs = pathWatch.Elapsed.TotalMilliseconds },
            fivePageTimelineMs, fivePageHistoryTraversals = 1,
            totalBrowserFlowMs = timelineMs + viewMs + pathWatch.Elapsed.TotalMilliseconds, cumulativeFlowAllocations = backendAllocations,
            harnessElapsedIncludingOraclesMs = total.Elapsed.TotalMilliseconds, harnessAllocationsIncludingOracles = allocated,
            timelineQueryMs = timelineMs, scopeViewQueryMs = viewMs,
            builds = reader.TimelineCacheStatistics.Builds, hits = reader.TimelineCacheStatistics.Hits,
            misses = reader.TimelineCacheStatistics.Misses, scopeChanges = reader.TimelineCacheStatistics.ScopeChanges,
            retainedCacheBytes = reader.TimelineCacheStatistics.RetainedTimestampBytes,
            rawBytesPerDistinctTimestamp = sizeof(long), timestampArrayHeaderBytesX64Model = 24,
            controlChecks = reader.TimelineCacheStatistics.EpochChecks,
            controlReadModel = "one existing validated state.json read, bounded8192 bytes, per hit/completed build; excluded from historical range counters",
            persistentRepositoryBytesWritten = 0, workspaceBytesWritten = 0 }));

        HistoryPage<DateTimeOffset> Timeline(string action, HistoryScope scope, int size,
            HistoryDirection direction = HistoryDirection.NewestFirst, HistoryCursor? cursor = null)
        {
            var s = reader.TimelineCacheStatistics; var builds = s.Builds; var hits = s.Hits; var misses = s.Misses;
            var changes = s.ScopeChanges; var prior = reader.HistoryStatistics; var memory = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            var result = reader.GetTimelinePage(scope, size, direction, cursor); watch.Stop(); var allocatedNow = GC.GetTotalAllocatedBytes(true) - memory;
            timelineMs += watch.Elapsed.TotalMilliseconds; backendAllocations += allocatedNow;
            var after = reader.HistoryStatistics; var reads = reader.LastTimelineStatistics!;
            var exact = oracles[scope.Folder].Times.AsEnumerable();
            if (cursor?.Time is { } boundary) exact = exact.Where(t => direction == HistoryDirection.OldestFirst ? t > boundary : t < boundary);
            if (direction == HistoryDirection.NewestFirst) exact = exact.Reverse();
            Assert.Equal(exact.Take(size), result.Items);
            if (s.Hits != hits)
            { Assert.Equal(prior, after); Assert.Equal(0, reads.Reads.DestinationReadRequests); Assert.Equal(0, reads.MatchingVersions); }
            else
            { Assert.Equal(oracles[scope.Folder].Count, reads.ValidatedVersions); Assert.Equal(reads.ValidatedVersions, reads.MatchingVersions); }
            using var process = Process.GetCurrentProcess();
            steps.Add(new { action, folder = scope.Folder, pageSize = size, direction, returned = result.Items.Count,
                digest = TimelineReadMeasurementTests.Digest(result.Items), elapsedMs = watch.Elapsed.TotalMilliseconds, allocations = allocatedNow,
                completeHistoryTraversals = s.Hits != hits ? 0 : 1, builds = s.Builds - builds, hits = s.Hits - hits,
                misses = s.Misses - misses, scopeChanges = s.ScopeChanges - changes,
                matchingVersions = reads.MatchingVersions, validatedFrames = after.SemanticFramesResolved - prior.SemanticFramesResolved,
                indexPages = after.IndexPagesRead - prior.IndexPagesRead, opens = after.SegmentOpens - prior.SegmentOpens,
                metadataBytes = after.BytesRequested - prior.BytesRequested, readRequests = reads.Reads.DestinationReadRequests,
                groupingWindows = reads.Reads.Windows, maximumReadWindowEntries = reads.Reads.MaximumBufferedEntries,
                maximumReadWindowKeyBytes = reads.Reads.MaximumBufferedKeyBytes,
                buildAllocations = s.Builds != builds ? allocatedNow : 0, retainedCacheBytes = s.RetainedTimestampBytes,
                retainedTimestamps = s.RetainedTimestamps, peakTemporaryTimestampBytes = s.Builds != builds ? s.LastBuildTemporaryTimestampBytes : 0,
                collectedAfterWindowDedup = s.Builds != builds ? s.LastBuildCollectedTicks : 0,
                sortMs = s.Builds != builds ? s.LastBuildSortMilliseconds : 0, dedupMs = s.Builds != builds ? s.LastBuildDeduplicateMilliseconds : 0,
                managedLiveBytes = GC.GetTotalMemory(false), lifetimePeakWorkingSet = process.PeakWorkingSet64, persistentBytesWritten = 0 });
            return result;
        }
        void View(string action, HistoryScope scope, DateTimeOffset? at)
        {
            var prior = reader.HistoryStatistics; var allocatedBefore = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            var view = reader.GetScopeHistoryPage(scope, new(at), 128); watch.Stop(); var allocatedNow = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            viewMs += watch.Elapsed.TotalMilliseconds; backendAllocations += allocatedNow;
            var after = reader.HistoryStatistics;
            views.Add(new { action, folder = scope.Folder, at, returned = view.Items.Count, elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocations = allocatedNow, indexPages = after.IndexPagesRead - prior.IndexPagesRead,
                validatedFrames = after.SemanticFramesResolved - prior.SemanticFramesResolved,
                opens = after.SegmentOpens - prior.SegmentOpens, metadataBytes = after.BytesRequested - prior.BytesRequested });
        }
    }

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void Measure500kDistinctConstructionAndExactOverflow()
    {
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        using var collector = new TimelineTickCollector(1_048_576, 16_384);
        for (var i = 0; i < 500_000; i++)
        { collector.Add(500_000 - i); if ((i + 1) % 16_384 == 0) collector.EndWindow(_ => { }); }
        collector.Complete(_ => { }); var ticks = collector.TakeResult(); watch.Stop(); var allocationDelta = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.Equal(500_000, ticks.Length); Assert.Equal(1, ticks[0]); Assert.Equal(500_000, ticks[^1]);
        for (var i = 1; i < ticks.Length; i++) Assert.Equal(ticks[i - 1] + 1, ticks[i]);
        var memory = new { distinctTimestamps = ticks.Length, retainedTimestampBytes = ticks.LongLength * 8,
            retainedArrayHeaderBytesX64Model = 24, rawBytesPerTimestamp = 8, peakTemporaryTimestampBytes = collector.MaximumTemporaryBytes,
            allocations = allocationDelta, sortMs = collector.SortMilliseconds, dedupMs = collector.DeduplicateMilliseconds,
            elapsedMs = watch.Elapsed.TotalMilliseconds, managedLiveBytes = GC.GetTotalMemory(false),
            note = "actual production collector over500k synthetic unique ticks; not a second physical history fixture or metadata-read measurement" };
        using var temporary = new TemporaryDirectory(); var repo = OpenFixture(temporary, "unique", 25_000); var before = Inventory(repo);
        using var reader = repo.OpenReader(); allocated = GC.GetTotalAllocatedBytes(true); watch.Restart();
        var page = reader.ReadCachedTimelinePage(new(64), new(FoundationTests.Root), 128, HistoryDirection.OldestFirst);
        watch.Stop(); var reads = reader.LastTimelineStatistics!;
        Assert.Equal(Expected("unique", 25_000).Take(128), page.Items); Assert.Equal(50_000, reads.ValidatedVersions);
        Assert.Equal(1, reader.TimelineCacheStatistics.Overflows); Assert.Equal(0, reader.TimelineCacheStatistics.Builds);
        Assert.Equal(0, reader.TimelineCacheStatistics.RetainedTimestamps); Assert.Equal(before, Inventory(repo));
        output.WriteLine("M18_BOUNDS " + JsonSerializer.Serialize(new { memory500k = memory,
            overflow = new { configuredCollectedTickLimit = 64, completeHistoryTraversals = 1, reads.ValidatedVersions,
                reads.MatchingVersions, indexPages = reads.Reads.Io.PagesRead, opens = reads.Reads.Io.SegmentOpens,
                metadataBytes = reads.Reads.DestinationBytesRequested, requests = reads.Reads.DestinationReadRequests,
                groupingWindows = reads.Reads.Windows, retainedCacheBytes = 0, redundantSecondTraversals = 0,
                digest = TimelineReadMeasurementTests.Digest(page.Items), elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocations = GC.GetTotalAllocatedBytes(true) - allocated, persistentBytesWritten = 0 } }));
    }

    private static int ExpectedVersionCount(string shape, int paths, HistoryScope scope) => Enumerable.Range(0, 2).Sum(p =>
        Enumerable.Range(0, paths).Count(i => Survives(shape, paths, p, i) && Matches(scope, i)));

    private static void ShapeRetention(Repository repo, int paths)
    {
        using (var w = repo.OpenWriter())
        using (var r = repo.OpenReader())
            w.Apply(new(currentRemovals: Enumerable.Range(0, paths / 10).Select(i => new HistoryPath(FoundationTests.Root, Name(i)))) , r.Authority);
        var engine = new RetentionEngine(repo);
        var result = engine.Execute(engine.Preview(RetentionPolicy.OneMonth, FoundationTests.Time.AddDays(90)));
        Assert.True(result.Complete);
    }
    internal static bool Survives(string shape, int paths, int pass, int i) => shape != "retained" ||
        pass == 1 && i >= paths / 10 || Time(shape, paths, pass, i) >= FoundationTests.Time.AddDays(60);
    internal static int ExpectedCount(string shape, int paths) => Enumerable.Range(0, 2).Sum(p => Enumerable.Range(0, paths).Count(i => Survives(shape, paths, p, i)));
    internal static DateTimeOffset[] Expected(string shape, int paths, HistoryScope? scope = null) =>
        Enumerable.Range(0, 2).SelectMany(p => Enumerable.Range(0, paths).Where(i => Survives(shape, paths, p, i) &&
            Matches(scope, i)).Select(i => Time(shape, paths, p, i))).Distinct().Order().ToArray();
    private static bool Matches(HistoryScope? scope, int i) => scope is null || scope.SourceRootId == FoundationTests.Root &&
        (scope.Folder.Length == 0 || scope.Contains(new(FoundationTests.Root, Name(i))));
    internal static Dictionary<string, (long Length, DateTime Mtime)> Inventory(Repository repo) =>
        Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(repo.DirectoryPath, p), p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));

    private static HistoryPage<DateTimeOffset> Measure(string candidate, string action, RepositoryReader r, HistoryScope scope,
        DateTimeOffset[] expected, List<object> results, Func<HistoryPage<DateTimeOffset>> run, Prototype? prototype = null)
    {
        var before = r.HistoryStatistics; var priorStats = r.LastTimelineStatistics;
        var allocation = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var page = run(); watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocation; var after = r.HistoryStatistics;
        var oldest = action == "reverse";
        var offset = action == "second" ? 2 : action.StartsWith("next-", StringComparison.Ordinal) ? int.Parse(action[5..], System.Globalization.CultureInfo.InvariantCulture) * 2 : 0;
        Assert.Equal((oldest ? expected : expected.Reverse()).Skip(offset).Take(action == "page-size" ? 128 : 2), page.Items);
        var s = r.LastTimelineStatistics; var traversed = !ReferenceEquals(s, priorStats);
        using var process = Process.GetCurrentProcess();
        results.Add(new { candidate, action, folder = scope.Folder, returned = page.Items.Count,
            digest = TimelineReadMeasurementTests.Digest(page.Items), elapsedMs = watch.Elapsed.TotalMilliseconds, allocations = allocated,
            completeHistoryTraversals = traversed ? 1 : 0, matchingVersions = traversed ? s!.MatchingVersions : 0,
            validatedFrames = after.SemanticFramesResolved - before.SemanticFramesResolved, indexPages = after.IndexPagesRead - before.IndexPagesRead,
            opens = after.SegmentOpens - before.SegmentOpens, metadataBytes = after.BytesRequested - before.BytesRequested,
            readRequests = traversed ? s!.Reads.DestinationReadRequests : 0, windows = traversed ? s!.Reads.Windows : 0,
            buildAllocations = traversed ? allocated : 0, sortMs = prototype?.SortMs ?? 0,
            retainedTimestampBytes = prototype?.Ticks.LongLength * 8 ?? 0, retainedArrayHeaderBytesX64 = prototype is null ? 0 : 24,
            temporaryTickCapacityBytes = prototype?.TemporaryBytes ?? 0, managedLiveBytes = GC.GetTotalMemory(false),
            lifetimePeakWorkingSet = process.PeakWorkingSet64, persistentBytesWritten = 0 });
        return page;
    }

    internal sealed class Prototype
    {
        internal long[] Ticks { get; private set; } = [];
        internal double SortMs { get; private set; }
        internal long TemporaryBytes { get; private set; }
        private AuthorityIdentity? _authority;
        internal void Build(RepositoryReader reader, HistoryScope scope)
        {
            Ticks = []; var ticks = new List<long>();
            reader.TimelineProjected = t => ticks.Add(t.UtcTicks);
            try { _ = reader.ReadTimelinePage(new(), scope, 1); }
            finally { reader.TimelineProjected = null; }
            TemporaryBytes = ticks.Capacity * 8L;
            var watch = Stopwatch.StartNew(); ticks.Sort();
            var n = 0;
            for (var i = 0; i < ticks.Count; i++) if (n == 0 || ticks[i] != ticks[n - 1]) ticks[n++] = ticks[i];
            Ticks = ticks.Take(n).ToArray(); watch.Stop(); SortMs = watch.Elapsed.TotalMilliseconds; _authority = reader.Authority;
        }
        internal HistoryPage<DateTimeOffset> Page(int size, HistoryDirection direction, DateTimeOffset? after = null)
        {
            var at = after is null ? (direction == HistoryDirection.OldestFirst ? 0 : Ticks.Length - 1) : Array.BinarySearch(Ticks, after.Value.UtcTicks);
            if (after is not null) at += direction == HistoryDirection.OldestFirst ? 1 : -1;
            var result = new List<DateTimeOffset>();
            for (; at >= 0 && at < Ticks.Length && result.Count < size; at += direction == HistoryDirection.OldestFirst ? 1 : -1)
                result.Add(new(Ticks[at], TimeSpan.Zero));
            return new(result.AsReadOnly(), at >= 0 && at < Ticks.Length ? new(_authority!, "prototype", time: result[^1]) : null);
        }
    }
}
