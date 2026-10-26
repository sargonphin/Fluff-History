using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class StorageEfficiencyMeasurementTests(ITestOutputHelper output)
{
    private sealed record SparseResult(int Changes, long PagesWritten, long SemanticFramesWritten,
        long SemanticBytesWritten, long PageBytesWritten, long LogicalMetadataBytesWritten, long TotalMetadataBytesWritten, double BytesPerChange);
    private sealed record CompactionResult(long BytesBefore, long BytesWritten, long BytesBeforeCleanup,
        long BytesAfterCleanup, int DeletedSegments, int DeferredSegments, double ReductionPercent);
    private sealed record Measurement(int Paths, Dictionary<string, MetadataFootprint> Stages,
        List<SparseResult> Sparse, CompactionResult Compaction);
    private sealed record SemanticDigest(int CurrentCount, int VersionCount, string CurrentHash, string HistoryHash, string SourceHash);

    // Exercise accounting, real sparse publication, compaction and cleanup in ordinary CI,
    // while keeping all requested investigation sizes explicitly opt-in.
    [Fact]
    public void SmallFixtureAccountsForEveryByteAndPreservesHistory()
    {
        var result = Measure(32);
        Assert.Equal(64, result.Stages["B"].Physical["Semantic/VersionAdded"].Count);
        Assert.Equal(64, result.Stages["D-clean"].Physical["Semantic/VersionAdded"].Count);
        Assert.Equal(32, result.Stages["D-clean"].Physical["Semantic/CurrentUpsert"].Count);
    }

    [MetadataDiagnosticTheory]
    [Trait("Category", "MetadataDiagnostic")]
    [InlineData(1_000)]
    // Straddle the public 4096-change batch boundary to isolate its effect.
    [InlineData(4_096)]
    [InlineData(4_097)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    [InlineData(25_000)]
    public void MeasureStorageAndCowAmplification(int paths) => Measure(paths);

    private Measurement Measure(int paths)
    {
        using var temp = new TemporaryDirectory();
        var repository = Repository.Create(temp.Child());
        var stages = new Dictionary<string, MetadataFootprint>();
        MetadataDiagnosticFixture.Populate(repository, paths, population =>
        {
            var footprint = MetadataFrameAccounting.Capture(repository);
            Assert.Equal((long)paths * population, footprint.Physical["Semantic/VersionAdded"].Count);
            Assert.Equal(paths, footprint.Indexes["Current"].Entries);
            Assert.Equal((long)paths * population, footprint.Indexes["Versions"].Entries);
            Assert.Equal(1, footprint.Indexes["Sources"].Entries);
            Assert.Equal(footprint.PageBytes - footprint.SupersededPageBytes,
                footprint.Reachable.Where(p => p.Key.StartsWith("Page/", StringComparison.Ordinal)).Sum(p => p.Value.Bytes));
            stages.Add(population == 1 ? "A" : "B", footprint);
        });
        var expected = Digest(repository, paths);
        Assert.Equal(paths * 2, expected.VersionCount);
        var sparse = new List<SparseResult>();
        foreach (var changes in new[] { 1, 16, 100, 1_000 }.Where(n => n <= paths))
            sparse.Add(MeasureSparse(repository, stages["B"], paths, changes));

        // No copied sparse changes are applied to this original two-version fixture.
        // All captured readers are disposed before cleanup, including on Windows.
        using (var writer = repository.OpenWriter()) writer.CompactMetadata();
        var compacted = MetadataFrameAccounting.Capture(repository);
        stages.Add("D-retired", compacted);
        Assert.True(stages["B"].ActiveEpochBytes > 0);
        Assert.Equal(expected, Digest(repository, paths));
        MetadataCleanupResult cleanup;
        using (var writer = repository.OpenWriter()) cleanup = writer.CleanupRetiredMetadata();
        var clean = MetadataFrameAccounting.Capture(repository); stages.Add("D-clean", clean);
        Assert.Equal(0, clean.SupersededPageBytes);
        Assert.Equal(0, cleanup.DeferredSegments);
        Assert.Equal(stages["B"].Segments, cleanup.DeletedSegments);
        Assert.Equal(compacted.ActiveEpochBytes, clean.DiskBytes);
        Assert.Equal(compacted.ActiveEpochSegments, clean.Segments);
        Assert.Equal(expected, Digest(repository, paths));
        Assert.Equal(stages["B"].Physical["Semantic/VersionAdded"].Count, clean.Physical["Semantic/VersionAdded"].Count);
        Assert.Equal(paths, clean.Physical["Semantic/CurrentUpsert"].Count);
        var compaction = new CompactionResult(stages["B"].DiskBytes,
            compacted.DiskBytes - stages["B"].DiskBytes, compacted.DiskBytes, clean.DiskBytes,
            cleanup.DeletedSegments, cleanup.DeferredSegments,
            100.0 * (stages["B"].DiskBytes - clean.DiskBytes) / stages["B"].DiskBytes);
        var measurement = new Measurement(paths, stages, sparse, compaction);
        output.WriteLine("STORAGE_JSON " + JsonSerializer.Serialize(measurement));
        return measurement;
    }

    private static SparseResult MeasureSparse(Repository baseline, MetadataFootprint before, int paths, int changes)
    {
        using var temp = new TemporaryDirectory();
        CopyRepository(baseline.DirectoryPath, temp.Child());
        var repository = Repository.Open(temp.Child());
        IoStatistics stats;
        // Uniform spacing in the logical keyspace; every case starts at exactly stage B.
        var names = Enumerable.Range(0, changes).Select(i => MetadataDiagnosticFixture.Name((int)((long)i * paths / changes))).ToArray();
        using (var writer = repository.OpenWriter())
        using (var reader = repository.OpenReader())
        {
            var prepared = names.Select(n => FoundationTests.Prepare(writer, n)).ToArray();
            Assert.True(writer.Apply(new(prepared), reader.Authority).Changed);
            stats = writer.LastWriteStatistics!;
        }
        var after = MetadataFrameAccounting.Capture(repository);
        var semanticFrames = after.Physical.Where(p => p.Key.StartsWith("Semantic/", StringComparison.Ordinal)).Sum(p => p.Value.Count)
            - before.Physical.Where(p => p.Key.StartsWith("Semantic/", StringComparison.Ordinal)).Sum(p => p.Value.Count);
        var pageCount = after.Physical.Where(p => p.Key.StartsWith("Page/", StringComparison.Ordinal)).Sum(p => p.Value.Count)
            - before.Physical.Where(p => p.Key.StartsWith("Page/", StringComparison.Ordinal)).Sum(p => p.Value.Count);
        Assert.Equal(stats.PagesWritten, pageCount);
        Assert.Equal(stats.PhysicalBytesWritten, after.DiskBytes - before.DiskBytes);
        Assert.Equal(changes * 2, semanticFrames);
        using (var reader = repository.OpenReader())
            foreach (var name in names)
            {
                var path = new HistoryPath(FoundationTests.Root, name);
                Assert.Equal(1, reader.GetCurrent(path)!.Metadata.Length);
                Assert.Equal(3, reader.EnumerateVersions(path).Count());
            }
        return new(changes, stats.PagesWritten, semanticFrames, after.SemanticBytes - before.SemanticBytes,
            after.PageBytes - before.PageBytes, stats.BytesWritten, stats.PhysicalBytesWritten, (double)stats.PhysicalBytesWritten / changes);
    }

    private static void CopyRepository(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    private static SemanticDigest Digest(Repository repository, int paths)
    {
        using var reader = repository.OpenReader();
        using var currentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var historyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var currentCount = 0; var versionCount = 0;
        var perPath = new Dictionary<HistoryPath, int>();
        foreach (var version in reader.EnumerateCurrent())
        {
            Assert.Equal(MetadataDiagnosticFixture.Name(currentCount), version.Metadata.RelativePath);
            currentHash.AppendData(Encode(version)); currentCount++;
        }
        foreach (var entry in reader.VersionIndex.Range(reader.Captured.Root.Versions))
        {
            var version = reader.Version(entry.Value, versionKey: entry.Key);
            perPath[version.Identity.Path] = perPath.GetValueOrDefault(version.Identity.Path) + 1;
            historyHash.AppendData(Encode(version)); versionCount++;
        }
        Assert.Equal(paths, currentCount); Assert.Equal(paths, perPath.Count);
        Assert.All(perPath.Values, count => Assert.Equal(2, count));
        var source = reader.GetSourceRoot(FoundationTests.Root); Assert.NotNull(source);
        var sourceHash = Format.Hash(MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: source)));
        return new(currentCount, versionCount, Convert.ToHexString(currentHash.GetHashAndReset()), Convert.ToHexString(historyHash.GetHashAndReset()), sourceHash);
        static byte[] Encode(StoredVersion version) => MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, null, Version: version));
    }
}
