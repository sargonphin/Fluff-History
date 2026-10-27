using System.Diagnostics;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

/// <summary>Real version records and production-sized segments, without 250k irrelevant payload files.</summary>
public sealed class CurrentCatalogMeasurementTests(ITestOutputHelper output)
{
    private const int FileCount = 250_000;
    private static string Name(int i) => MetadataDiagnosticFixture.Name(i);

    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void DistributedCurrentVersionsMeasureEnumerationAndSparseMutation()
    {
        using var temp = new TemporaryDirectory();
        var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, FileCount);
        var segments = Directory.GetFiles(Path.Combine(temp.Child(), "metadata"), "*.seg");
        output.WriteLine($"Fixture: current={FileCount}, historical versions={2 * FileCount}, segments={segments.Length}, metadata bytes={segments.Sum(p => new FileInfo(p).Length)}; production rotation={Format.MaximumSegmentBytes}.");
        Assert.True(segments.Length > 8, "The measurement must exceed the existing segment-handle cache.");
        var before = Inventory(temp.Child());
        var state = File.ReadAllBytes(Path.Combine(temp.Child(), "state.json"));
        MeasureCurrent(repo, "before sparse changes");
        using (var writer = repo.OpenWriter())
        using (var reader = repo.OpenReader())
        {
            Assert.False(writer.Apply(new(), reader.Authority).Changed);
            Assert.Null(writer.LastWriteStatistics);
        }
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(temp.Child(), "state.json")));
        Assert.Equal(before, Inventory(temp.Child()));
        Assert.False(Directory.Exists(Path.Combine(temp.Child(), "staging")));

        // Exercise real mutation preparation/publication for a small change set against the large catalog.
        using (var writer = repo.OpenWriter())
        using (var reader = repo.OpenReader())
        {
            var changes = Enumerable.Range(0, 16).Select(i => FoundationTests.Prepare(writer, Name(i * 15_001))).ToArray();
            Assert.True(writer.Apply(new(changes), reader.Authority).Changed);
            var stats = writer.LastWriteStatistics!;
            Assert.InRange(stats.PagesWritten, 1, 160);
            output.WriteLine($"Sparse mutation: versions=16, pages written={stats.PagesWritten}, frames written={stats.FramesWritten}, metadata bytes written={stats.BytesWritten}, new segments={stats.SegmentsCreated}.");
        }
        using (var reader = repo.OpenReader())
        {
            for (var i = 0; i < 16; i++)
            {
                var path = new HistoryPath(FoundationTests.Root, Name(i * 15_001));
                Assert.Equal(1, reader.GetCurrent(path)!.Metadata.Length);
                Assert.Equal(3, reader.EnumerateVersions(path).Count());
            }
            Report("16 sparse current/history queries", reader.Segments.Statistics);
        }
        MeasureCurrent(repo, "after sparse changes");
        MeasureCurrent(repo, "after sparse changes (local comparison)", local: true);
    }

    private void MeasureCurrent(Repository repo, string label, bool local = false)
    {
        using var reader = local ? repo.OpenComparisonReader() : repo.OpenReader();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var count = 0;
        HistoryPath? previous = null;
        foreach (var version in reader.EnumerateCurrent())
        {
            Assert.Equal(Name(count), version.Metadata.RelativePath);
            Assert.True(previous is null || previous.CompareTo(version.Identity.Path) < 0);
            previous = version.Identity.Path;
            count++;
        }
        timer.Stop();
        Assert.Equal(FileCount, count);
        Assert.Equal(FileCount, reader.Segments.Statistics.SemanticReads);
        Assert.InRange(reader.Segments.Statistics.PeakHandles, 1, 8);
        Report(label, reader.Segments.Statistics);
        using var process = Process.GetCurrentProcess();
        output.WriteLine($"{label}: elapsed diagnostic ms={timer.ElapsedMilliseconds}, process allocated delta={GC.GetTotalAllocatedBytes(true) - allocated}, managed live bytes={GC.GetTotalMemory(false)}, process peak working set={process.PeakWorkingSet64}.");
        if (local)
        {
            var workspace = reader.Workspace!; var fetched = workspace.DestinationSegmentFetches;
            Assert.InRange(fetched, 1, Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg").Length);
            output.WriteLine($"{label}: destination segment materializations={fetched}, destination bytes materialized={workspace.DestinationBytesFetched}; excludes bounded authority/control validation.");
            var opens = reader.Segments.Statistics.SegmentOpens; timer.Restart();
            Assert.Equal(FileCount, reader.EnumerateCurrent().Count()); timer.Stop();
            Assert.Equal(fetched, workspace.DestinationSegmentFetches);
            output.WriteLine($"{label}: repeat enumeration fetched zero additional destination segments; local opens={reader.Segments.Statistics.SegmentOpens - opens}, diagnostic ms={timer.ElapsedMilliseconds}.");
        }
    }

    private void Report(string label, IoStatistics stats) => output.WriteLine(
        $"{label}: pages read={stats.PagesRead}, semantic reads={stats.SemanticReads}, segment opens={stats.SegmentOpens}, evictions={stats.HandleEvictions}, peak cached handles={stats.PeakHandles}, logical metadata bytes read={stats.BytesRead}.");

    private static Dictionary<string, (long Length, DateTime Modified)> Inventory(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(directory, p), p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));

}
