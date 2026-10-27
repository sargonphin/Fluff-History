using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class ComparisonReadMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory]
    [Trait("Category", "MetadataDiagnostic")]
    [InlineData(1_000)]
    [InlineData(25_000)]
    [InlineData(250_000)]
    public void MeasureComparisonWindows(int paths)
    {
        using var temp = new TemporaryDirectory();
        var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, paths);
        if (paths == 250_000)
        {
            // Match the sparse Current comparison boundary, using the public writer.
            using var writer = repo.OpenWriter(); using var reader = repo.OpenReader();
            var changes = Enumerable.Range(0, 16).Select(i => FoundationTests.Prepare(writer, MetadataDiagnosticFixture.Name(i * 15_001))).ToArray();
            writer.Apply(new(changes), reader.Authority);
            var stats = writer.LastWriteStatistics!;
            output.WriteLine($"Sparse mutation: pages={stats.PagesWritten}, frames={stats.FramesWritten}, bytes={stats.BytesWritten}");
        }
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var segmentFiles = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg");
        output.WriteLine($"Fixture: paths={paths}, versions={paths * 2 + (paths == 250_000 ? 16 : 0)}, segments={segmentFiles.Length}, metadata bytes={segmentFiles.Sum(p => new FileInfo(p).Length)}");
        string expectedDigest;
        using (var reader = repo.OpenComparisonReader(temp.Child("workspace")))
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
            var count = 0;
            foreach (var item in reader.EnumerateCurrent())
            { Append(digest, item.Identity, item.Metadata.Length, item.Metadata.LastWriteTimeUtc); count++; }
            timer.Stop(); expectedDigest = Convert.ToHexString(digest.GetHashAndReset());
            Assert.Equal(paths, count);
            var s = reader.Segments.Statistics;
            output.WriteLine("COMPARISON_BASELINE " + JsonSerializer.Serialize(new
            {
                paths,
                count,
                pages = s.PagesRead,
                frames = s.SemanticReads,
                opens = s.SegmentOpens,
                evictions = s.HandleEvictions,
                logicalBytes = s.BytesRead,
                destinationOpens = reader.Workspace!.DestinationSegmentFetches,
                destinationBytes = reader.Workspace.DestinationBytesFetched,
                temporaryBytes = reader.Workspace.DestinationBytesFetched,
                allocations = GC.GetTotalAllocatedBytes(true) - allocated,
                liveBytes = GC.GetTotalMemory(false),
                elapsedMs = timer.ElapsedMilliseconds
            }));
        }
        foreach (var size in new[] { 4096, 16384, 65536 })
        {
            using var comparison = repo.OpenComparisonReader(temp.Child("comparison"));
            var budget = new ComparisonReadBudget(size, size == 65536 ? 16 * 1024 * 1024 : 4 * 1024 * 1024);
            var allocatedBefore = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;
            foreach (var item in comparison.EnumerateCurrentComparison(budget))
            {
                Assert.InRange(item.Length, 1, 4096); Assert.Equal(FoundationTests.Root, item.Path.SourceRootId);
                Append(digest, item.Identity, item.Length, item.LastWriteTimeUtc);
                count++;
            }
            watch.Stop(); Assert.Equal(paths, count);
            Assert.Equal(expectedDigest, Convert.ToHexString(digest.GetHashAndReset()));
            var stats = comparison.LastComparisonStatistics!; var io = stats.Io;
            Assert.Equal(paths, io.SemanticReads); Assert.InRange(stats.MaximumBufferedEntries, 1, size);
            Assert.InRange(stats.MaximumBufferedKeyBytes, 1, budget.MaximumKeyBytes);
            Assert.Null(comparison.Workspace!.CreatedDirectory);
            using var process = Process.GetCurrentProcess();
            output.WriteLine("COMPARISON_GROUPED " + JsonSerializer.Serialize(new
            {
                paths,
                size,
                count,
                pages = io.PagesRead,
                frames = io.SemanticReads,
                opens = io.SegmentOpens,
                evictions = io.HandleEvictions,
                destinationBytes = stats.DestinationBytesRequested,
                requests = stats.DestinationReadRequests,
                frameBytes = stats.LogicalFrameBytes,
                temporaryBytes = 0,
                windows = stats.Windows,
                maximumEntries = stats.MaximumBufferedEntries,
                maximumKeyBytes = stats.MaximumBufferedKeyBytes,
                allocations = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
                indexAllocations = stats.IndexAllocatedBytes,
                resolutionAllocations = stats.ResolutionAllocatedBytes,
                liveBytes = GC.GetTotalMemory(false),
                peakWorkingSet = process.PeakWorkingSet64,
                elapsedMs = watch.ElapsedMilliseconds
            }));
        }
        var pathAllocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) _ = new HistoryPath(FoundationTests.Root, MetadataDiagnosticFixture.Name(i));
        output.WriteLine($"PATH_ALLOCATION per 10000 constructions including fixture names: {GC.GetAllocatedBytesForCurrentThread() - pathAllocated}");
        Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        Assert.Equal(before, Inventory(repo));
    }

    private static void Append(IncrementalHash hash, VersionIdentity identity, long length, DateTimeOffset lastWrite)
    {
        Span<byte> fields = stackalloc byte[64 + HistoryPath.MaximumPathLength * 2];
        identity.Path.SourceRootId.TryWriteBytes(fields, bigEndian: true, out _);
        identity.VersionId.TryWriteBytes(fields[16..], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64LittleEndian(fields[32..], identity.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(fields[40..], identity.Ordinal);
        BinaryPrimitives.WriteInt64LittleEndian(fields[44..], length);
        BinaryPrimitives.WriteInt64LittleEndian(fields[52..], lastWrite.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(fields[60..], identity.Path.CanonicalPath.Length);
        var bytes = Format.Utf16.GetBytes(identity.Path.CanonicalPath, fields[64..]);
        hash.AppendData(fields[..(64 + bytes)]);
    }
    private static Dictionary<string, (long, DateTime)> Inventory(Repository repo) => Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
}
