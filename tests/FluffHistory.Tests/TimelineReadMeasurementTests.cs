using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class TimelineReadMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData(25_000)] [InlineData(250_000)]
    public void MeasureTimelineWindows(int paths)
    {
        // Exactly one fixture per selected case, shared by every candidate and continuation.
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, paths, storedTime: (pass, i) =>
            FoundationTests.Time.AddDays(pass == 1 && i % 3 == 0 ? -1 : i % 5));
        if (paths == 25_000)
        {
            // Match the historical M14 25k membership/configuration boundary.
            using var w = repo.OpenWriter(); using var r = repo.OpenReader();
            w.Apply(new(currentRemovals: Enumerable.Range(paths - 37, 37).Select(i =>
                new FluffHistory.Core.HistoryPath(FoundationTests.Root, MetadataDiagnosticFixture.Name(i)))), r.Authority);
            using var next = repo.OpenReader();
            w.Apply(new(currentRemovals: Enumerable.Range(0, 1000).Select(i =>
                new FluffHistory.Core.HistoryPath(FoundationTests.Root, MetadataDiagnosticFixture.Name(i))),
                sourceRoots: [new(FoundationTests.Root, "Source", excludedDirectories: ["Documents/folder000"])]), next.Authority);
        }
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var files = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg");
        output.WriteLine("M141_FIXTURE " + JsonSerializer.Serialize(new { paths, versions = paths * 2, segments = files.Length,
            metadataBytes = files.Sum(p => new FileInfo(p).Length) }));
        var scope = new HistoryScope(FoundationTests.Root);
        // Independent fixture oracle, derived from both input populations rather than the projection.
        var expected = Enumerable.Range(0, 2).SelectMany(pass => Enumerable.Range(0, paths).Select(i =>
            FoundationTests.Time.AddDays(pass == 1 && i % 3 == 0 ? -1 : i % 5))).Distinct().Order().ToArray();
        var expectedDigest = Digest(expected);
        // Full reader parity is covered by ordinary tests; do not rerun the old slow 500k read path.
        foreach (var size in new[] { 4096, 16384, 65536 })
        {
            var budget = new ComparisonReadBudget(size, size == 4096 ? 1024 * 1024 : size == 16384 ? 4 * 1024 * 1024 : 16 * 1024 * 1024);
            using var reader = repo.OpenComparisonReader(temp.Child("unused-workspace"));
            var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            var page = reader.ReadTimelinePage(budget, scope, 128, HistoryDirection.OldestFirst);
            watch.Stop(); var allocationDelta = GC.GetTotalAllocatedBytes(true) - allocated;
            Assert.Equal(expected, page.Items); Assert.Null(page.Continuation);
            var digest = Digest(page.Items); Assert.Equal(expectedDigest, digest);
            var s = reader.LastTimelineStatistics!; var r = s.Reads; var io = r.Io;
            Assert.Equal(paths * 2, s.MatchingVersions); Assert.Equal(paths * 2, s.ValidatedVersions);
            Assert.InRange(r.MaximumBufferedEntries, 1, size); Assert.InRange(r.MaximumBufferedKeyBytes, 1, budget.MaximumKeyBytes);
            Assert.InRange(io.PeakHandles, 1, 8); Assert.Null(reader.Workspace!.CreatedDirectory);
            Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
            using var process = Process.GetCurrentProcess();
            output.WriteLine("M141_JSON " + JsonSerializer.Serialize(new { paths, size, keyByteLimit = budget.MaximumKeyBytes,
                returned = page.Items.Count, digest, expectedDigest, matchingVersions = s.MatchingVersions,
                semanticFrames = s.ValidatedVersions, indexPages = io.PagesRead, opens = io.SegmentOpens,
                requests = r.DestinationReadRequests, requestedBytes = r.DestinationBytesRequested, frameBytes = r.LogicalFrameBytes,
                windows = r.Windows, maximumEntries = r.MaximumBufferedEntries, maximumKeyBytes = r.MaximumBufferedKeyBytes,
                maximumTimestampCandidates = s.MaximumTimestampCandidates, peakRetainedHandles = io.PeakHandles,
                allocations = allocationDelta, indexAllocations = r.IndexAllocatedBytes, resolutionAllocations = r.ResolutionAllocatedBytes,
                liveBytes = GC.GetTotalMemory(false), peakWorkingSet = process.PeakWorkingSet64, elapsedMs = watch.ElapsedMilliseconds,
                workspaceBytes = 0, persistentBytesWritten = 0 }));
        }
        // Public default and complete continuation in both directions on the same immutable fixture.
        foreach (var direction in Enum.GetValues<HistoryDirection>())
        {
            using var reader = repo.OpenReader(); HistoryCursor? cursor = null; var actual = new List<DateTimeOffset>();
            do { var p = reader.GetTimelinePage(scope, 2, direction, cursor); actual.AddRange(p.Items); cursor = p.Continuation; } while (cursor is not null);
            Assert.Equal(direction == HistoryDirection.OldestFirst ? expected : expected.Reverse(), actual);
        }
        Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
    }

    internal static string Digest(IEnumerable<DateTimeOffset> times)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); Span<byte> bytes = stackalloc byte[8];
        foreach (var time in times) { BinaryPrimitives.WriteInt64LittleEndian(bytes, time.UtcTicks); hash.AppendData(bytes); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static Dictionary<string, (long, DateTime)> Inventory(Repository repo) => Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
}
