using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class RetentionMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void MeasureMixedAgePreviewAndExecution()
    {
        const int paths = 1024, deleted = 256;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter())
        {
            for (var pass = 0; pass < 3; pass++)
            foreach (var group in Enumerable.Range(0, paths).Chunk(128))
            {
                var versions = group.Select(i => Prepare(writer, $"Protected/f{i:D5}", pass == 0 ? i % 16 == 0 ? 100 : 900 : pass == 1 ? i % 4 == 0 ? 100 : 975 : 50)).ToArray();
                using var reader = repo.OpenReader(); writer.Apply(new(versions, sourceRoots: [new(FoundationTests.Root, "missing")]), reader.Authority);
            }
            foreach (var group in Enumerable.Range(0, deleted).Chunk(128))
            {
                var versions = group.Select(i => Prepare(writer, $"Deleted/f{i:D5}", i % 2 == 0 ? 100 : 990)).ToArray();
                using (var reader = repo.OpenReader()) writer.Apply(new(versions), reader.Authority);
                using var fresh = repo.OpenReader(); writer.Apply(new(currentRemovals: versions.Select(v => v.Path)), fresh.Authority);
            }
        }
        var inventory = RetentionTests.Inventory(repo); var engine = new RetentionEngine(repo);
        foreach (var policy in new[] { RetentionPolicy.TwoYears, RetentionPolicy.OneMonth })
        {
            var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            var preview = engine.Preview(policy, RetentionTests.Evaluation); watch.Stop();
            Assert.True(preview.Complete); Assert.Equal(3328, preview.Examined); Assert.Equal(1024, preview.Protected);
            Assert.Equal(policy == RetentionPolicy.OneMonth ? 1408 : 448, preview.Eligible);
            var sample = Measure(preview, watch.ElapsedMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated);
            output.WriteLine("M152_PREVIEW " + JsonSerializer.Serialize(new { fixture = "mixed-real-payload", policy = policy.ToString(), sample }));
        }
        Assert.Equal(inventory, RetentionTests.Inventory(repo));
        var complete = engine.Preview(RetentionPolicy.OneMonth, RetentionTests.Evaluation);
        using (var reader = repo.OpenReader())
        {
            RetentionCursor? cursor = null; long candidates = 0, maxCount = 0, maxBytes = 0;
            do
            {
                var page = engine.GetCandidates(reader, complete.Evaluation, cursor: cursor); candidates += page.Items.Count;
                maxCount = Math.Max(maxCount, page.Items.Count); maxBytes = Math.Max(maxBytes, page.ChargedCandidateBytes); cursor = page.Continuation;
            } while (cursor is not null);
            Assert.Equal(complete.Eligible, candidates); output.WriteLine("M152_PAGES " + JsonSerializer.Serialize(new { candidates, maxCount, maxBytes }));
        }
        // Hold four exact eligible payloads on Windows; independently omit one retired payload
        // to demonstrate already-absent accounting without any preview content reads.
        VersionIdentity[] eligible;
        using (var reader = repo.OpenReader()) eligible = engine.GetCandidates(reader, complete.Evaluation).Items.Take(5).Select(v => v.Identity).ToArray();
        File.Delete(repo.GetPayloadPath(eligible[4].VersionId)); var held = new List<VersionPayload>();
        if (OperatingSystem.IsWindows()) using (var reader = repo.OpenReader()) held.AddRange(eligible.Take(4).Select(id => reader.OpenVersionPayload(id)!));
        try
        {
            var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var result = engine.Execute(complete); watch.Stop();
            Assert.True(result.Complete); Assert.Equal(1408, result.Removed); Assert.Equal(11, result.BatchesCommitted); Assert.Equal(1, result.AgeScans);
            Assert.Equal(3328, result.Reads!.VersionsExamined); Assert.Equal(1, result.Cleanup.AlreadyAbsent); Assert.Equal(held.Count, result.Cleanup.Deferred); Assert.Equal(0, result.Cleanup.Failed);
            var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
            long survivors; using (var reader = repo.OpenReader()) survivors = reader.EnumerateRetentionVersions().LongCount(); Assert.Equal(1920, survivors);
            output.WriteLine("M152_EXECUTION " + JsonSerializer.Serialize(new { result, survivors, cumulativeAllocatedBytes = allocation,
                liveManagedBytes = GC.GetTotalMemory(false), elapsedMs = watch.ElapsedMilliseconds, processLifetimePeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 }));
        }
        finally { foreach (var handle in held) handle.Dispose(); }
        using var recovery = repo.OpenWriter(); Assert.Equal(held.Count, recovery.CleanupRetiredPayloads().Result.Deleted); recovery.RebuildIndexes(); recovery.CompactMetadata();
    }

    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData(250_000)]
    public void MeasureLargePreviewOnly(int paths)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, paths, storedTime: (pass, i) => FoundationTests.Time.AddDays(pass == 0 ? i % 100 == 0 ? 100 : 900 : i % 10 == 0 ? 50 : 990));
        var inventory = RetentionTests.Inventory(repo); var files = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg");
        output.WriteLine("M152_LARGE_FIXTURE " + JsonSerializer.Serialize(new { paths, versions = paths * 2, segments = files.Length, metadataBytes = files.Sum(p => new FileInfo(p).Length), payloads = 0 }));
        foreach (var policy in new[] { RetentionPolicy.TwoYears, RetentionPolicy.OneMonth })
        {
            var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            using var reader = repo.OpenComparisonReader(temp.Child("unused"));
            var preview = new RetentionEngine(repo).Preview(reader, new(policy, RetentionTests.Evaluation)); watch.Stop();
            Assert.True(preview.Complete); Assert.Equal(paths * 2, preview.Examined); Assert.Equal(paths, preview.Protected);
            Assert.Equal(policy == RetentionPolicy.OneMonth ? paths : paths / 100, preview.Eligible);
            Assert.Null(reader.Workspace!.CreatedDirectory); Assert.InRange(preview.Reads!.PeakRetainedHandles, 1, 8);
            output.WriteLine("M152_LARGE_PREVIEW " + JsonSerializer.Serialize(new { policy = policy.ToString(), sample = Measure(preview, watch.ElapsedMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated) }));
        }
        Assert.Equal(inventory, RetentionTests.Inventory(repo));
    }
    private static object Measure(RetentionPreview preview, long elapsedMs, long allocation) => new { preview,
        cumulativeAllocatedBytes = allocation, liveManagedBytes = GC.GetTotalMemory(false), processLifetimePeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
        elapsedMs, persistentBytesWritten = 0, workspaceBytes = 0, payloadBytesRead = 0 };
    private static PreparedVersion Prepare(RepositoryWriter writer, string path, int day)
    {
        var version = writer.PrepareVersion(new(FoundationTests.Root, path, 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
            FoundationTests.Time.AddDays(day), new("D24EC4F1A98C6E5B"));
        var payload = writer.GetPreparedPayloadPath(version); Directory.CreateDirectory(Path.GetDirectoryName(payload)!); File.WriteAllBytes(payload, [42]); return version;
    }
}
