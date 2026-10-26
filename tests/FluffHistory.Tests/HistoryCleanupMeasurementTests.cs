using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class HistoryCleanupMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData(false)] [InlineData(true)]
    public void MeasureMixedRealPayloadCleanup(bool latest)
    {
        const int paths = 1280, protectedPaths = 1024;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var roots = new[] { FoundationTests.Root, Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333") };
        Guid Root(int i) => roots[i < protectedPaths ? 0 : i < 1152 ? 1 : 2];
        string Name(int i) => $"Documents/f{i:D5}";
        // Independent moderate oracle from exact public-publication results. Production never holds this array.
        var inputs = new List<StoredVersion>();
        using (var writer = repo.OpenWriter())
        {
            for (int pass = 0; pass < 3; pass++)
            foreach (var group in Enumerable.Range(0, paths).Chunk(128))
            {
                var versions = group.Select(i => Prepare(writer, Root(i), Name(i), pass,
                    pass == 0 ? 100 : pass == 1 ? i % 4 == 0 ? 975 : 100 : i < protectedPaths ? 50 : i % 2 == 0 ? 100 : 990)).ToArray();
                using (var reader = repo.OpenReader()) writer.Apply(new(versions, sourceRoots: roots.Select(r => new SourceRootConfiguration(r, "missing-live-source"))), reader.Authority);
                using var fresh = repo.OpenReader(); inputs.AddRange(versions.Select(v => fresh.GetCurrent(v.Path)!));
            }
            foreach (var group in Enumerable.Range(protectedPaths, paths - protectedPaths).Chunk(128))
            {
                using var reader = repo.OpenReader(); writer.Apply(new(currentRemovals: group.Select(i => new HistoryPath(Root(i), Name(i)))), reader.Authority);
            }
            using var final = repo.OpenReader(); writer.Apply(new(sourceRoots: [new(roots[2], "missing-live-source", false)]), final.Authority);
        }
        var evaluation = latest ? HistoryCleanupTests.Latest : HistoryCleanupTests.Month;
        var lastByPath = inputs.GroupBy(v => v.Identity.Path).ToDictionary(g => g.Key, g => g.MaxBy(v => (v.Identity.Sequence, v.Identity.Ordinal))!);
        bool Current(StoredVersion v) => v.Identity.Path.SourceRootId == roots[0] && lastByPath[v.Identity.Path] == v;
        var expected = inputs.Where(v => latest ? lastByPath[v.Identity.Path] != v : !Current(v) && v.StoredUtc < evaluation.CutoffUtc).Select(v => v.Identity).ToHashSet();
        Assert.Equal(latest ? 2560 : 2368, expected.Count);
        var engine = new HistoryCleanupEngine(repo); var inventory = RetentionTests.Inventory(repo);
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var preview = engine.Preview(evaluation); watch.Stop();
        var previewSample = new { preview, cumulativeAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            liveManagedBytes = GC.GetTotalMemory(false), elapsedMs = watch.ElapsedMilliseconds };
        Assert.True(preview.Complete); Assert.Equal(3840, preview.Examined); Assert.Equal(paths, preview.PathsExamined); Assert.Equal(protectedPaths, preview.Protected);
        Assert.Equal(expected.Count, preview.Eligible); Assert.Equal(paths, preview.AffectedPaths); Assert.Equal(expected.Count, preview.EligibleLogicalBytes);
        Assert.Equal(inventory, RetentionTests.Inventory(repo));
        long maxCount = 0, maxBytes = 0, pages = 0; var actual = new HashSet<VersionIdentity>();
        using (var reader = repo.OpenReader())
        {
            HistoryCleanupCursor? cursor = null;
            do
            {
                var page = engine.GetCandidates(reader, evaluation, cursor: cursor); pages++;
                foreach (var candidate in page.Items) Assert.True(actual.Add(candidate.Identity));
                maxCount = Math.Max(maxCount, page.Items.Count); maxBytes = Math.Max(maxBytes, page.ChargedCandidateBytes); cursor = page.Continuation;
            } while (cursor is not null);
        }
        Assert.True(expected.SetEquals(actual));
        var chosen = expected.OrderBy(i => i.Path).ThenBy(i => i.Sequence).ThenBy(i => i.Ordinal).Take(5).ToArray();
        File.Delete(repo.GetPayloadPath(chosen[4].VersionId)); var held = new List<VersionPayload>();
        if (OperatingSystem.IsWindows()) using (var reader = repo.OpenReader()) held.AddRange(chosen.Take(4).Select(id => reader.OpenVersionPayload(id)!));
        HistoryCleanupExecutionResult result; object executionSample;
        try
        {
            allocated = GC.GetTotalAllocatedBytes(true); watch.Restart(); result = engine.Execute(preview); watch.Stop();
            executionSample = new { result, cumulativeAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                liveManagedBytes = GC.GetTotalMemory(false), elapsedMs = watch.ElapsedMilliseconds, processLifetimePeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 };
            Assert.True(result.Complete); Assert.Equal(expected.Count, result.Removed); Assert.Equal(1, result.HistoryScans); Assert.Equal(3840, result.Reads!.VersionsExamined);
            Assert.Equal(1, result.Cleanup.AlreadyAbsent); Assert.Equal(held.Count, result.Cleanup.Deferred); Assert.Equal(0, result.Cleanup.Failed);
        }
        finally { foreach (var handle in held) handle.Dispose(); }
        long survivorCount, survivorPaths;
        using (var reader = repo.OpenReader())
        {
            var survivors = reader.EnumerateRetentionVersions().ToArray(); survivorCount = survivors.Length; survivorPaths = survivors.Select(v => v.Identity.Path).Distinct().LongCount();
            Assert.True(inputs.Where(v => !expected.Contains(v.Identity)).Select(v => v.Identity).ToHashSet().SetEquals(survivors.Select(v => v.Identity)));
            Assert.Equal(latest ? 1280 : 1472, survivorCount); Assert.Equal(latest ? 1280 : 1216, survivorPaths);
            var times = roots.SelectMany(r => reader.GetTimelinePage(new(r)).Items).Distinct().ToArray();
            Assert.DoesNotContain(RetentionTests.Evaluation, times); Assert.Equal(latest, times.Contains(FoundationTests.Time.AddDays(100)));
        }
        using (var writer = repo.OpenWriter())
        {
            Assert.Equal(held.Count, writer.CleanupRetiredPayloads().Result.Deleted); writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes();
        }
        using (var fresh = repo.OpenReader()) Assert.Equal(survivorCount, fresh.EnumerateRetentionVersions().LongCount());
        output.WriteLine("M153_MIXED " + JsonSerializer.Serialize(new { mode = evaluation.Mode.ToString(), fixture = new { paths, versions = inputs.Count, protectedPaths, deletedPaths = 128, unprotectedPaths = 128, realPayloads = inputs.Count },
            previewSample, pages = new { pages, candidates = actual.Count, maxCount, maxBytes, maximumPendingVersions = latest ? 1 : 0 }, executionSample,
            survivorCount, survivorPaths, globalSort = false, selectionScratchBytes = 0, payloadBytesReadForSelection = 0,
            note = "Logical bytes are not reclaimed capacity. Writer materialization is separate from selection scratch. Live samples are unforced; peak includes fixture construction. Moderate oracle collections are test-only." }));
    }

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void MeasureLargeKeepOnlyMostRecentPreview()
    {
        const int paths = 250_000;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, paths, storedTime: (pass, i) => FoundationTests.Time.AddDays(pass == 0 ? i % 7 + 100 : 1));
        var files = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg"); var inventory = RetentionTests.Inventory(repo);
        using var reader = repo.OpenComparisonReader(temp.Child("unused"));
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var preview = new HistoryCleanupEngine(repo).Preview(reader, HistoryCleanupTests.Latest); watch.Stop();
        Assert.True(preview.Complete); Assert.Equal(500_000, preview.Examined); Assert.Equal(paths, preview.PathsExamined); Assert.Equal(paths, preview.Protected);
        Assert.Equal(paths, preview.Eligible); Assert.Equal(paths, preview.AffectedPaths); Assert.Equal(paths * 4096L, preview.EligibleLogicalBytes);
        Assert.Equal(500_000, preview.Reads!.VersionsExamined); Assert.InRange(preview.Reads.PeakRetainedHandles, 1, 8);
        Assert.InRange(preview.Reads.MaximumRetainedEntries, 1, 32768); Assert.InRange(preview.Reads.MaximumRetainedKeyBytes, 1, 8 * 1024 * 1024);
        Assert.Null(reader.Workspace!.CreatedDirectory); Assert.Equal(inventory, RetentionTests.Inventory(repo));
        output.WriteLine("M153_LARGE " + JsonSerializer.Serialize(new { fixture = new { paths, versions = paths * 2, segments = files.Length, metadataBytes = files.Sum(p => new FileInfo(p).Length), payloads = 0 },
            preview, retainedPerPathPublications = 1, logicallyRetainedVersions = paths, historyTraversals = 1,
            globalSort = false, candidateArray = false, selectionScratchBytes = 0, workspaceBytes = 0, repositoryBytesWritten = 0, payloadBytesRead = 0,
            cumulativeAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, liveManagedBytes = GC.GetTotalMemory(false), elapsedMs = watch.ElapsedMilliseconds,
            processLifetimePeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            note = "Preview only; no removal/payload population. One undecided publication, bounded projection windows/caches. Cumulative allocation is not retained memory; unforced live sample and process-lifetime peak include runtime/test effects. VM time is secondary." }));
    }

    private static PreparedVersion Prepare(RepositoryWriter writer, Guid root, string path, int pass, int day)
    {
        var version = writer.PrepareVersion(new(root, path, 1, FoundationTests.Time.AddDays(900 - pass * 100), FoundationTests.Time.AddDays(800 - pass * 200), FileAttributes.Archive),
            FoundationTests.Time.AddDays(day), new("D24EC4F1A98C6E5B"));
        var payload = writer.GetPreparedPayloadPath(version); Directory.CreateDirectory(Path.GetDirectoryName(payload)!); File.WriteAllBytes(payload, [42]); return version;
    }
}
