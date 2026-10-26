using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class SourcePipelineMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory]
    [Trait("Category", "MetadataDiagnostic")]
    [InlineData(1_000)]
    [InlineData(4_096)]
    [InlineData(25_000)]
    public void RealSourceTreeInitialUnchangedAndChanged(int paths)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var source = temp.Child("source");
        Directory.CreateDirectory(source); long bytes = 0;
        for (var i = 0; i < paths; i++)
        {
            var path = Path.Combine(source, MetadataDiagnosticFixture.Name(i)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = Encoding.UTF8.GetBytes($"payload-{i:D6}"); File.WriteAllBytes(path, content); bytes += content.Length;
            File.SetLastWriteTimeUtc(path, FoundationTests.Time.UtcDateTime);
        }
        var selection = new SourceSelection(new(FoundationTests.Root, source), source);
        var initial = Measure(repo, selection, "initial", paths);
        Assert.Equal(paths, initial.VersionsCreated); Assert.Equal(bytes, initial.SourceBytesRead); Assert.Equal(bytes, initial.PayloadBytesWritten);
        Assert.True(initial.BatchesCommitted > 1); Assert.False(Directory.Exists(Path.Combine(repo.DirectoryPath, "staging")));
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var unchanged = Measure(repo, selection, "unchanged", paths, prohibitContent: true);
        Assert.Equal(paths, unchanged.Unchanged); AssertNoChange(repo, before, authority, unchanged);
        for (var i = 0; i < 37; i++) File.AppendAllText(Path.Combine(source, MetadataDiagnosticFixture.Name(i * (paths / 37))), "changed");
        var changed = Measure(repo, selection, "changed37", paths);
        Assert.Equal(37, changed.VersionsCreated); Assert.Equal(changed.SourceBytesRead, changed.PayloadBytesWritten);
        Assert.Equal(1, changed.BatchesCommitted); Assert.Equal(0, changed.SourceSkips);
    }

    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void Real250kSourceNoChangeUsesDistributedProductionComparison()
    {
        const int count = 250_000;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var source = temp.Child("source");
        MetadataDiagnosticFixture.Populate(repo, count);
        string? previousDirectory = null; SourceRootConfiguration config;
        using (var reader = repo.OpenReader())
        {
            config = reader.GetSourceRoot(FoundationTests.Root)!;
            var ordinal = 0;
            foreach (var item in reader.EnumerateCurrentComparison())
            {
                var path = Path.Combine(source, MetadataDiagnosticFixture.Name(ordinal++).Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(path)!;
                if (directory != previousDirectory) { Directory.CreateDirectory(directory); previousDirectory = directory; }
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1)) file.SetLength(item.Length);
                if (OperatingSystem.IsWindows())
                { File.SetCreationTimeUtc(path, FoundationTests.Time.UtcDateTime); File.SetAttributes(path, FileAttributes.Archive); }
                File.SetLastWriteTimeUtc(path, item.LastWriteTimeUtc.UtcDateTime);
            }
        }
        var inventory = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var result = Measure(repo, new(config, source), "unchanged250k", count, prohibitContent: true);
        Assert.Equal(count, result.FilesEnumerated); Assert.Equal(count, result.ComparisonEntries); Assert.Equal(count, result.Unchanged);
        AssertNoChange(repo, inventory, authority, result);
    }

    private ScanResult Measure(Repository repo, SourceSelection source, string phase, int paths, bool prohibitContent = false)
    {
        var engine = new FileHistoryEngine(repo, new(), prohibitContent ? new NoContent() : new SourceAccess());
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = engine.Scan([source]); watch.Stop(); using var process = Process.GetCurrentProcess();
        output.WriteLine("M13_JSON " + JsonSerializer.Serialize(new
        {
            paths,
            phase,
            result,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            managedLiveBytes = GC.GetTotalMemory(false),
            peakWorkingSet = process.PeakWorkingSet64,
            elapsedMs = watch.ElapsedMilliseconds,
            persistentRepositoryBytesWritten = result.BatchesCommitted == 0 && result.PayloadBytesWritten == 0 ? 0L : (long?)null
        }));
        return result;
    }
    private sealed class NoContent : SourceAccess
    { internal override Stream OpenRead(string path) => throw new InvalidOperationException("No-change source content must never be opened."); }
    private static Dictionary<string, (long Length, DateTime Modified)> Inventory(Repository repo) =>
        Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
    private static void AssertNoChange(Repository repo, Dictionary<string, (long, DateTime)> before, byte[] authority, ScanResult result)
    {
        Assert.Equal(0, result.BatchesCommitted); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, result.SourceBytesRead);
        Assert.Equal(0, result.HistoricalLookups);
        Assert.Equal(0, result.PayloadBytesWritten); Assert.Equal(before, Inventory(repo));
        Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        Assert.Empty(Directory.GetFiles(repo.DirectoryPath, "state.*.tmp"));
        Assert.False(Directory.Exists(Path.Combine(repo.DirectoryPath, "staging")));
    }
}
