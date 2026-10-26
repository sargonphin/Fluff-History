using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class CapacityMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void MeasureMixedRealPayloadPressure()
    {
        const int paths = 1024, deleted = 256;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var eligible = new List<StoredVersion>();
        using (var writer = repo.OpenWriter())
        {
            for (int pass = 0; pass < 3; pass++)
            foreach (var chunk in Enumerable.Range(0, paths).Chunk(128))
            {
                var versions = chunk.Select(i => Prepare(writer, $"Protected/f{i:D5}", pass == 0 ? i % 5 : pass == 1 ? -(i % 7) : -100)).ToArray();
                using (var reader = repo.OpenReader()) writer.Apply(new(versions, sourceRoots: [new(FoundationTests.Root, "missing")]), reader.Authority);
                if (pass != 2) using (var reader = repo.OpenReader()) eligible.AddRange(versions.Select(v => reader.GetVersion(new(v.Path, reader.Authority.MutationSequence, Array.IndexOf(versions, v), v.VersionId))!));
            }
            foreach (var chunk in Enumerable.Range(0, deleted).Chunk(128))
            {
                var versions = chunk.Select(i => Prepare(writer, $"Deleted/f{i:D5}", -8)).ToArray();
                using (var reader = repo.OpenReader()) writer.Apply(new(versions), reader.Authority);
                using (var reader = repo.OpenReader())
                {
                    eligible.AddRange(versions.Select(v => reader.GetCurrent(v.Path)!));
                    writer.Apply(new(currentRemovals: versions.Select(v => v.Path)), reader.Authority);
                }
            }
        }
        var expected = eligible.OrderBy(v => v.StoredUtc).ThenBy(v => v.Identity.Sequence).ThenBy(v => v.Identity.Ordinal).ToArray();
        File.Delete(repo.GetPayloadPath(expected[4].Identity.VersionId)); var held = new List<VersionPayload>();
        if (OperatingSystem.IsWindows()) using (var reader = repo.OpenReader()) held.AddRange(expected.Take(4).Select(v => reader.OpenVersionPayload(v.Identity)!));
        var probe = new ScriptedCapacityProbe(new CapacityObservation(100000, 4000), new CapacityObservation(100000, 4000), new CapacityObservation(100000, 4000), new CapacityObservation(100000, 4000),
            new CapacityObservation(100000, 3900), new CapacityObservation(120000, 4300), new CapacityObservation(120000, 6100), new CapacityObservation(120000, 6300));
        var engine = new CapacityRetentionEngine(repo, probe: probe); long compared = 0;
        engine.OrderReady = order =>
        {
            using var actual = order.Candidates().GetEnumerator();
            foreach (var item in expected) { Assert.True(actual.MoveNext()); Assert.Equal(item.Identity, actual.Current.Identity); compared++; }
            Assert.False(actual.MoveNext());
        };
        try
        {
            var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var result = engine.EnsureCapacity(200); watch.Stop();
            var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
            Assert.Equal(CapacityRetentionOutcome.SatisfiedAfterRetention, result.Outcome); Assert.Equal(1, result.HistoryScans);
            Assert.Equal(5, result.BatchesCommitted); Assert.Equal(3328, result.Examined); Assert.Equal(1024, result.Protected); Assert.Equal(2304, result.Eligible);
            Assert.Equal(compared, result.Eligible); Assert.Equal(1, result.Cleanup.AlreadyAbsent); Assert.Equal(held.Count, result.Cleanup.Deferred); Assert.Equal(0, result.Cleanup.Failed);
            Assert.Equal(6200, result.Latest!.RequiredFree); Assert.Equal(6300, result.LatestObservation!.Free);
            using (var reader = repo.OpenReader())
            {
                foreach (var item in expected.Take((int)result.Removed)) Assert.Null(reader.GetVersion(item.Identity));
                foreach (var item in expected.Skip((int)result.Removed)) Assert.NotNull(reader.GetVersion(item.Identity));
                Assert.Equal(1024, reader.EnumerateCurrentComparison().Count());
            }
            output.WriteLine("M161_MIXED " + JsonSerializer.Serialize(new { result, survivors = 3328 - result.Removed, compared,
                cumulativeAllocatedBytes = allocation, liveManagedBytes = GC.GetTotalMemory(false), processLifetimePeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, elapsedMs = watch.ElapsedMilliseconds }));
        }
        finally { foreach (var handle in held) handle.Dispose(); }
        using var recovery = repo.OpenWriter(); Assert.Equal(held.Count, recovery.CleanupRetiredPayloads().Result.Deleted); recovery.RebuildIndexes(); recovery.CompactMetadata();
    }

    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData(1)] [InlineData(16)] [InlineData(32)] [InlineData(128)]
    public void MeasureBatchTradeoff(int batch)
    {
        using var f = new HistoryFixture();
        using (var writer = f.Repository.OpenWriter())
        {
            var versions = Enumerable.Range(0, 256).Select(i => Prepare(writer, $"old/{i:D3}", 0)).ToArray();
            using (var reader = f.Repository.OpenReader()) writer.Apply(new(versions, sourceRoots: [new(FoundationTests.Root, "missing")]), reader.Authority);
            using var current = f.Repository.OpenReader(); writer.Apply(new(currentRemovals: versions.Select(v => v.Path)), current.Authority);
        }
        var probe = new BatchModelProbe(batch); var result = new CapacityRetentionEngine(f.Repository, new(BatchSize: batch), probe).EnsureCapacity(32);
        Assert.True(result.Satisfied); Assert.Equal((37 + batch - 1) / batch * batch, result.Removed);
        output.WriteLine("M161_BATCH " + JsonSerializer.Serialize(new { batch, minimumModelRemovals = 37, extraModelRemovals = result.Removed - 37, result.Removed,
            result.BatchesCommitted, result.CapacityObservations, result.VersionPagesWritten, result.MetadataBytesAppended, result.MaximumBatchCount }));
    }
    private sealed class BatchModelProbe(int batch) : IRepositoryCapacityProbe
    {
        private int _calls;
        public CapacityObservation Observe(Repository repository) => new(100, _calls++ < 3 ? 0 : Math.Min((_calls - 3) * batch, 100));
    }

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void MeasureLargePressureOrderAndCheapAssessment()
    {
        const int paths = 250_000;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, paths, storedTime: (pass, i) => FoundationTests.Time.AddDays(pass == 0 ? i % 7 : i % 3 == 0 ? -2 : 20));
        var inventory = RetentionTests.Inventory(repo); var segments = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg");
        var cheap = new CapacityRetentionEngine(repo, probe: new ScriptedCapacityProbe(new CapacityObservation(100, 5))).EnsureCapacity(0);
        Assert.Equal(0, cheap.HistoryScans); Assert.Equal(0, cheap.Sort.RunsCreated); Assert.Equal(inventory, RetentionTests.Inventory(repo));
        output.WriteLine("M161_LARGE_FIXTURE " + JsonSerializer.Serialize(new { paths, versions = paths * 2, segments = segments.Length,
            metadataBytes = segments.Sum(p => new FileInfo(p).Length), payloads = 0, cheap.HistoryScans, cheap.CapacityObservations, cheap.Sort.RunsCreated }));
        var probe = new ScriptedCapacityProbe(new CapacityObservation(100000, 0), new CapacityObservation(100000, 0), new CapacityObservation(100000, 0), new CapacityObservation(100000, 0), new CapacityObservation(100000, 5000));
        var engine = new CapacityRetentionEngine(repo, new(WorkspaceRoot: temp.Child("work")), probe); long compared = 0;
        engine.OrderReady = order =>
        {
            using var actual = order.Candidates().GetEnumerator();
            // Independent fixture oracle, without an all-history oracle array. First pass has
            // original sequence2+floor(i/4096), ordinal i%4096; second pass is protected current.
            for (int day = 0; day < 7; day++)
            for (int i = day; i < paths; i += 7)
            {
                Assert.True(actual.MoveNext()); var item = actual.Current;
                Assert.Equal(FoundationTests.Time.AddDays(day), item.StoredUtc); Assert.Equal(2 + i / 4096, item.Identity.Sequence);
                Assert.Equal(i % 4096, item.Identity.Ordinal); Assert.Equal(new HistoryPath(FoundationTests.Root, MetadataDiagnosticFixture.Name(i)), item.Identity.Path); compared++;
            }
            Assert.False(actual.MoveNext());
        };
        var allocation = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var result = engine.EnsureCapacity(0); watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocation;
        Assert.True(result.Satisfied); Assert.Equal(1, result.HistoryScans); Assert.Equal(2, result.BatchesCommitted); Assert.Equal(500000, result.Examined);
        Assert.Equal(250000, compared); Assert.Equal(250000, result.Protected); Assert.Equal(250000, result.Eligible); Assert.Equal(result.Removed, result.Cleanup.AlreadyAbsent);
        Assert.InRange(result.Sort.PeakChunkEntries, 1, 4096); Assert.InRange(result.Sort.PeakChunkBytes, 1, 4 * 1024 * 1024); Assert.InRange(result.Sort.PeakOpenFiles, 1, 10);
        output.WriteLine("M161_LARGE " + JsonSerializer.Serialize(new { result, compared, survivors = 500000 - result.Removed,
            cumulativeAllocatedBytes = allocated, liveManagedBytes = GC.GetTotalMemory(false), processLifetimePeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, elapsedMs = watch.ElapsedMilliseconds }));
        Assert.Empty(Directory.GetDirectories(temp.Child("work"), "FluffHistory-capacity-*"));
    }
    private static PreparedVersion Prepare(RepositoryWriter writer, string path, int day)
    {
        var version = writer.PrepareVersion(new(FoundationTests.Root, path, 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
            FoundationTests.Time.AddDays(day), new("D24EC4F1A98C6E5B"));
        var payload = writer.GetPreparedPayloadPath(version); Directory.CreateDirectory(Path.GetDirectoryName(payload)!); File.WriteAllBytes(payload, [42]); return version;
    }
}
