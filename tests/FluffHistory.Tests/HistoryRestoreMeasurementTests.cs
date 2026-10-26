using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class HistoryRestoreMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData(1_000)] [InlineData(25_000)] [InlineData(250_000)]
    public void ProductionFormatScopePagesAndExactTimeline(int paths)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, paths, storedTime: (pass, ordinal) =>
            FoundationTests.Time.AddDays(pass == 1 && ordinal % 3 == 0 ? -1 : ordinal % 5));
        if (paths <= 25_000)
        {
            // Real deletion/unprotection state mixed with surviving historical paths.
            using var w = repo.OpenWriter(); using var r = repo.OpenReader();
            w.Apply(new(currentRemovals: Enumerable.Range(paths - 37, 37).Select(i => new HistoryPath(FoundationTests.Root, MetadataDiagnosticFixture.Name(i)))), r.Authority);
            using var next = repo.OpenReader();
            w.Apply(new(currentRemovals: Enumerable.Range(0, Math.Min(1000, paths)).Select(i => new HistoryPath(FoundationTests.Root, MetadataDiagnosticFixture.Name(i))),
                sourceRoots: [new(FoundationTests.Root, "Source", excludedDirectories: ["Documents/folder000"])]), next.Authority);
        }
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var scope = new HistoryScope(FoundationTests.Root);
        foreach (var size in new[] { 128, 256, 1024 })
        {
            Measure(repo, paths, $"latest-first-{size}", r => { var p = r.GetScopeHistoryPage(scope, pageSize: size); return (p.Items.Count, p.Continuation is not null); });
            Measure(repo, paths, $"asof-first-{size}", r => { var p = r.GetScopeHistoryPage(scope, new(FoundationTests.Time.AddDays(2)), size); return (p.Items.Count, p.Continuation is not null); });
        }
        HistoryCursor? middle = null, last = null;
        using (var r = repo.OpenReader())
        {
            HistoryCursor? cursor = null; long count = 0; HistoryPath? previous = null;
            do
            {
                last = cursor; var page = r.GetScopeHistoryPage(scope, pageSize: 128, cursor: cursor);
                foreach (var item in page.Items)
                {
                    if (previous is not null) Assert.True(string.CompareOrdinal(previous.CanonicalPath, item.Version.Identity.Path.CanonicalPath) < 0);
                    previous = item.Version.Identity.Path; count++;
                }
                cursor = page.Continuation; if (middle is null && count >= paths / 2) middle = cursor;
            } while (cursor is not null);
            Assert.Equal(paths, count);
        }
        Measure(repo, paths, "latest-middle-128", r => { var p = r.GetScopeHistoryPage(scope, pageSize: 128, cursor: middle); return (p.Items.Count, p.Continuation is not null); });
        Measure(repo, paths, "latest-last-128", r => { var p = r.GetScopeHistoryPage(scope, pageSize: 128, cursor: last); return (p.Items.Count, p.Continuation is not null); });
        Measure(repo, paths, "timeline-distinct", r =>
        {
            var p = r.GetTimelinePage(scope, 128, HistoryDirection.OldestFirst);
            Assert.Equal(6, p.Items.Count); Assert.Null(p.Continuation); return (p.Items.Count, false);
        });
        Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        Assert.False(Directory.Exists(Path.Combine(repo.DirectoryPath, "staging")));
    }

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void ThousandVersionPathHasBoundedFirstMiddleLastPages()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); BuildPath(repo, 1_000);
        var path = new HistoryPath(FoundationTests.Root, "many.txt"); HistoryCursor? middle = null, last = null;
        using (var r = repo.OpenReader())
        {
            HistoryCursor? cursor = null; var count = 0;
            do
            {
                last = cursor; var p = r.GetPathHistoryPage(path, 128, HistoryDirection.OldestFirst, cursor);
                count += p.Items.Count; cursor = p.Continuation; if (middle is null && count >= 500) middle = cursor;
            } while (cursor is not null);
            Assert.Equal(1000, count);
        }
        foreach (var item in new[] { ("path-first", (HistoryCursor?)null), ("path-middle", middle), ("path-last", last) })
            Measure(repo, 1, item.Item1, r =>
            {
                var p = r.GetPathHistoryPage(path, 128, HistoryDirection.OldestFirst, item.Item2);
                Assert.InRange(r.HistoryStatistics.SemanticFramesResolved, 1, 128); return (p.Items.Count, p.Continuation is not null);
            });
    }

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void ManySmallFilesAndLargePayloadRestoreUseOneBoundedBuffer()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var source = temp.Child("source"); Directory.CreateDirectory(source);
        for (var i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(source, $"small{i:D4}"), new string('a', 96));
        var large = Path.Combine(source, "large.bin"); var block = new byte[64 * 1024]; new Random(17).NextBytes(block);
        using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write)) for (var i = 0; i < 512; i++) stream.Write(block);
        var scan = new FileHistoryEngine(repo).Scan([new(new(FoundationTests.Root, source), source)]); Assert.Equal(1001, scan.VersionsCreated);
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = new RestoreEngine(repo).RestoreScope(new(FoundationTests.Root), temp.Child("restore")); watch.Stop();
        using var process = Process.GetCurrentProcess();
        output.WriteLine("M14_JSON " + JsonSerializer.Serialize(new { shape = "restore-1000-small-plus-32MiB", result,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, managedLiveBytes = GC.GetTotalMemory(false),
            peakWorkingSet = process.PeakWorkingSet64, elapsedMs = watch.ElapsedMilliseconds, copyBufferBytes = 128 * 1024, persistentRepositoryBytesWritten = 0 }));
        Assert.Equal(1001, result.Restored); Assert.Equal(0, result.Failed); Assert.Equal(1001, result.ChecksumsVerified);
        Assert.Equal(32 * 1024 * 1024 + 96000, result.BytesRead); Assert.Equal(result.BytesRead, result.BytesWritten);
        Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        Assert.Equal(new FileInfo(large).Length, new FileInfo(Path.Combine(temp.Child("restore"), "large.bin")).Length);
    }

    private void Measure(Repository repo, int paths, string shape, Func<RepositoryReader, (int Count, bool More)> operation)
    {
        using var reader = repo.OpenReader(); var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = operation(reader); watch.Stop(); using var process = Process.GetCurrentProcess();
        output.WriteLine("M14_JSON " + JsonSerializer.Serialize(new { paths, shape, returned = result.Count, hasMore = result.More,
            io = reader.HistoryStatistics, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            managedLiveBytes = GC.GetTotalMemory(false), peakWorkingSet = process.PeakWorkingSet64,
            elapsedMs = watch.ElapsedMilliseconds, persistentRepositoryBytesWritten = 0 }));
        if (shape.StartsWith("latest-", StringComparison.Ordinal)) Assert.InRange(reader.HistoryStatistics.SemanticFramesResolved, 1, 1025);
    }
    private static Dictionary<string, (long, DateTime)> Inventory(Repository repo) => Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));

    private static void BuildPath(Repository repo, int count)
    {
        using var writer = repo.OpenWriter(); using var reader = repo.OpenReader();
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, reader.Captured);
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
        var index = new OrderedIndex(IndexKind.Versions, Read, append); var sources = new OrderedIndex(IndexKind.Sources, Read, append);
        var current = new OrderedIndex(IndexKind.Current, Read, append); FrameAddress? currentRoot = null;
        FrameAddress? journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source"))));
        var source = sources.Update(null, [(PathKeys.Source(FoundationTests.Root), journal)]); FrameAddress? versions = null;
        var prior = reader.Captured;
        for (var i = 0; i < count; i++)
        {
            var identity = new VersionIdentity(new(FoundationTests.Root, "many.txt"), i + 1, 0, Guid.NewGuid());
            var version = new StoredVersion(identity, new(FoundationTests.Root, "many.txt", 0, FoundationTests.Time, FoundationTests.Time, 0),
                FoundationTests.Time.AddMinutes(i), new("EF46DB3751D8E999"));
            journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, journal, Version: version)));
            versions = index.Update(versions, [(PathKeys.Version(identity), journal)]);
            currentRoot = current.Update(currentRoot, [(PathKeys.Current(identity.Path), journal)]);
            journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, journal, Path: identity.Path, VersionAddress: journal)));
            var root = new MetadataRoot(i + 1, i + 1, Guid.NewGuid(), prior.Root.Epoch, prior.Root.Generation,
                prior.Root.TransactionId, prior.Address.Sha256, journal, currentRoot, versions, source, null);
            var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); prior = new(repo.Identity, root, address);
        }
        append.FlushAndValidate(); AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(prior));
        AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
    }
}
