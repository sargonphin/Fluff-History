using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class LiveCapacityMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory]
    [Trait("Category", "MetadataDiagnostic")]
    [InlineData("adequate")]
    [InlineData("pressure-before-copy")]
    [InlineData("forever-blocked")]
    [InlineData("reactivation-race")]
    [InlineData("byte-bound")]
    [InlineData("large-single")]
    public void ModerateRealSource(string scenario)
    {
        using var f = new LiveCapacityFixture(); StoredVersion? retired = null;
        if (scenario is "pressure-before-copy" or "reactivation-race")
        {
            retired = f.Seed(current: false);
            if (scenario == "pressure-before-copy") f.File(content: "changed content", day: 1);
            f.Probe.Before = (_, call) => { if (call == 2) f.Probe.Free = 0; };
        }
        else if (scenario == "byte-bound")
            for (var i = 0; i < 5; i++) SizedFile(f, $"{i:D2}", 24 * 1024 * 1024);
        else if (scenario == "large-single")
        { SizedFile(f, "a", 1024); SizedFile(f, "b", 96 * 1024 * 1024); SizedFile(f, "c", 1024); }
        else
            for (var i = 0; i < 64; i++) f.File($"file{i:D3}", new string('x', 16 * 1024));
        if (scenario == "forever-blocked") f.Probe.Free = 0;
        var engine = f.Engine(scenario == "forever-blocked" ? RetentionPolicy.Forever : RetentionPolicy.UntilSaturation);
        engine.ConfigureWriter = writer => writer.PayloadCleanupObserve = (stage, _) =>
        { if (stage == PayloadCleanupStage.Deleted) f.Probe.Free = 900_000_000; };
        f.Access.BeforeOpen = _ =>
        {
            Assert.True(f.Probe.Calls > 0);
            if (retired is not null) { using var r = f.Repository.OpenReader(); Assert.Null(r.GetVersion(retired.Identity)); }
        };
        using var initial = f.Repository.OpenReader(); var before = RetentionTests.Inventory(f.Repository);
        var result = Measure(scenario, () => f.Scan(engine), initial.Authority.Generation, "injected repository-bound capacity and 4096-byte geometry; real Windows source/repository");
        Assert.Equal(1, result.WriterAcquisitions); Assert.Equal(1, result.CurrentComparisonScans);
        if (scenario == "forever-blocked")
        {
            Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome); Assert.Equal(0, result.ContentStreamsOpened);
            Assert.Equal(0, result.SourceSkips); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
        }
        else
        {
            Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(result.FilesEnumerated, result.VersionsCreated);
            Assert.Equal(result.SourceBytesRead, result.PayloadBytesWritten);
            Assert.Equal(retired is null ? 0 : 1, result.Capacity.HistoryScans);
            Assert.Equal(retired is null ? 0 : 1, result.HistoricalReaderRefreshes);
            if (scenario == "byte-bound")
            { Assert.Equal(3, result.BatchesCommitted); Assert.Equal(2, result.PeakPendingPayloadCount); Assert.Equal(48 * 1024 * 1024, result.PeakPendingPayloadBytes); }
            if (scenario == "large-single")
            { Assert.Equal(3, result.BatchesCommitted); Assert.Equal(1, result.PeakPendingPayloadCount); Assert.Equal(96 * 1024 * 1024, result.PeakPendingPayloadBytes); }
            if (scenario == "reactivation-race") { Assert.Equal(0, result.Reactivations); Assert.True(result.HistoricalLookups >= 2); }
        }
    }

    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void Real250kNoChangeWithInitialReserveProbe()
    {
        const int count = 250_000;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var source = temp.Child("source");
        MetadataDiagnosticFixture.Populate(repo, count);
        string? previousDirectory = null; SourceRootConfiguration config;
        using (var reader = repo.OpenReader())
        {
            config = reader.GetSourceRoot(FoundationTests.Root)!; var ordinal = 0;
            foreach (var item in reader.EnumerateCurrentComparison())
            {
                var path = Path.Combine(source, MetadataDiagnosticFixture.Name(ordinal++).Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(path)!;
                if (directory != previousDirectory) { Directory.CreateDirectory(directory); previousDirectory = directory; }
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1)) file.SetLength(item.Length);
                if (OperatingSystem.IsWindows()) { File.SetCreationTimeUtc(path, FoundationTests.Time.UtcDateTime); File.SetAttributes(path, FileAttributes.Archive); }
                File.SetLastWriteTimeUtc(path, item.LastWriteTimeUtc.UtcDateTime);
            }
        }
        var before = RetentionTests.Inventory(repo); var probe = new LiveProbe();
        var engine = new FileHistoryEngine(repo, new(Capacity: new(RetentionPolicy.UntilSaturation)), new NoContent(), probe, new AllocationProbe());
        using var initial = repo.OpenReader();
        var result = Measure("no-change250k", () => engine.Scan([new(config, source)]), initial.Authority.Generation,
            "Distributed two-pass metadata fixture, real 250k source files; metadata fixture has no historical payload files; injected adequate capacity/geometry");
        Assert.Equal(count, result.FilesEnumerated); Assert.Equal(count, result.ComparisonEntries); Assert.Equal(count, result.Unchanged);
        Assert.Equal(1, probe.Calls); Assert.Equal(0, result.Capacity.HistoryScans); Assert.Equal(0, result.HistoricalLookups);
        Assert.Equal(1, result.WriterAcquisitions); Assert.Equal(1, result.CurrentComparisonScans); Assert.Equal(0, result.HistoricalReaderRefreshes);
        Assert.Equal(0, result.BatchesCommitted); Assert.Equal(0, result.MetadataBytesAppended); Assert.Equal(before, RetentionTests.Inventory(repo));
        Assert.Equal(initial.Authority, result.Authority); Assert.Equal(0, result.ContentStreamsOpened);
    }

    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void NativeSeparateWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var workspace = Environment.GetEnvironmentVariable("FLUFF_HISTORY_CAPACITY_WORKSPACE_ROOT");
        if (workspace is null) { output.WriteLine("Native separate-volume diagnostic requires FLUFF_HISTORY_CAPACITY_WORKSPACE_ROOT."); return; }
        using var f = new LiveCapacityFixture(); f.File();
        var geometry = f.Repository.ObserveAllocationGeometry(workspace);
        output.WriteLine("SCAN_CAPACITY_NATIVE_GEOMETRY " + JsonSerializer.Serialize(geometry));
        var engine = new FileHistoryEngine(f.Repository, new(WorkspaceRoot: workspace, Capacity: new(RetentionPolicy.UntilSaturation)));
        using var initial = f.Repository.OpenReader();
        var result = Measure("native-separate-workspace", () => f.Scan(engine), initial.Authority.Generation, "native Windows capacity and allocation/volume probes; separate filesystem workspace");
        Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(1, result.VersionsCreated); Assert.Equal(0, result.Capacity.HistoryScans);
    }

    private ScanResult Measure(string scenario, Func<ScanResult> scan, long generation, string fixture)
    {
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var result = scan(); watch.Stop();
        using var process = Process.GetCurrentProcess();
        output.WriteLine("SCAN_CAPACITY_JSON " + JsonSerializer.Serialize(new
        {
            scenario, fixture, result, sourceGenerations = result.BatchesCommitted, capacityGenerations = result.Capacity.RemovalBatches,
            generationDelta = result.Authority.Generation - generation, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            managedLiveBytes = GC.GetTotalMemory(false), peakWorkingSet = process.PeakWorkingSet64, elapsedMs = watch.ElapsedMilliseconds
        }));
        return result;
    }
    private static void SizedFile(LiveCapacityFixture f, string name, long bytes)
    { var path = f.File(name, ""); using var file = new FileStream(path, FileMode.Open, FileAccess.Write); file.SetLength(bytes); }
    private sealed class NoContent : SourceAccess
    { internal override Stream OpenRead(string path) => throw new InvalidOperationException("No-change must never read source content."); }
}
