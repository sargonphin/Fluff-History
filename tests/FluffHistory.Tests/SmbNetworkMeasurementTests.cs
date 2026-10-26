using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbNetworkMeasurementTests(ITestOutputHelper output)
{
    internal static string Root => Environment.GetEnvironmentVariable("FLUFF_HISTORY_M19_LOCAL_ROOT")
        ?? throw new InvalidOperationException("Set FLUFF_HISTORY_M19_LOCAL_ROOT to an owned disposable diagnostic directory.");
    internal static string Remote => Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_TEST_ROOT")
        ?? throw new InvalidOperationException("Set FLUFF_HISTORY_SMB_TEST_ROOT to an owned UNC directory.");
    internal static bool After => Environment.GetEnvironmentVariable("FLUFF_HISTORY_M19_PHASE") == "after";
    internal static string ResultDirectory => Path.Combine(Root, After ? "results-after" : "results");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    internal static MaintenanceLimits Limits => new(MaximumRecords: 4_000_000, MaximumOutputBytes: 2L * 1024 * 1024 * 1024,
        MaximumScratchBytes: 2L * 1024 * 1024 * 1024, MaximumScratchBytesWritten: 24L * 1024 * 1024 * 1024,
        MaximumMaterializedBytes: 32L * 1024 * 1024 * 1024);

    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline", 30_000, 250_000)]
    [InlineData("heavy", 80_000, 800_000)]
    public void BuildPhysicalFixture(string tier, int paths, int survivors)
    {
        Directory.CreateDirectory(Root);
        var marker = Path.Combine(Root, tier + "-dirty.complete");
        var target = Path.Combine(Root, tier + "-dirty");
        if (File.Exists(marker))
        {
            Assert.Equal($"v1:{paths}:{survivors}", File.ReadAllText(marker));
            _ = Repository.Open(target); return;
        }
        Assert.False(Directory.Exists(target), "Incomplete fixture must be inspected explicitly before rebuilding.");
        var watch = Stopwatch.StartNew();
        var repository = Repository.Create(target);
        SmbDiagnosticFixture.Populate(repository, paths, survivors);
        using (var reader = repository.OpenReader())
        {
            Assert.Equal(paths, reader.CurrentIndex.Range(reader.Captured.Root.Current).Count());
            Assert.Equal(survivors, reader.VersionIndex.Range(reader.Captured.Root.Versions).Count());
        }
        Save(tier + "-fixture", new { Paths = paths, Survivors = survivors, ElapsedMs = watch.Elapsed.TotalMilliseconds, Metadata = Inventory(target) });
        File.WriteAllText(marker, $"v1:{paths}:{survivors}");
    }

    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline", false)] [InlineData("heavy", false)]
    [InlineData("baseline", true)] [InlineData("heavy", true)]
    public void MeasureCompaction(string tier, bool remote)
    {
        Assert.True(File.Exists(Path.Combine(Root, tier + "-dirty.complete")));
        var label = tier + (remote ? "-remote" : "-local") + "-compaction" + (After ? "-after" : "");
        var marker = Path.Combine(Root, label + ".complete");
        if (File.Exists(marker)) return;
        var target = Path.Combine(remote ? Remote : Root, tier + "-dense" + (After ? "-after" : ""));
        Assert.False(Directory.Exists(target), "Incomplete compaction must be inspected explicitly.");
        var copy = Measure(() => CopyTree(Path.Combine(Root, tier + "-dirty"), target));
        var repository = Repository.Open(target);
        var before = Inventory(target);
        var workspace = Path.Combine(Root, "work");
        using var writer = repository.OpenWriter(workspace);
        object? fold = null;
        long copiedSourceBytes = 0, copyReadRequests = 0, workspaceBytesAtPrepared = 0;
        writer.Observe = stage =>
        {
            if (stage == PublicationStage.LocalPrepared && Directory.Exists(workspace))
                workspaceBytesAtPrepared = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        };
        MetadataWorkspace.CopyObserved = (input, destination, bytes) =>
        {
            if (input is FileStream file && destination is FileStream result &&
                file.Name.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !result.Name.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { copiedSourceBytes += bytes; copyReadRequests++; }
        };
        var protocolBefore = SmbServerCounters.Capture();
        Metric compact;
        try { compact = Measure(() => { writer.CompactMetadata(Limits); fold = Snapshot(writer.LastHistoryFoldStatistics); }); }
        finally { MetadataWorkspace.CopyObserved = null; }
        var protocol = SmbServerCounters.Difference(protocolBefore, SmbServerCounters.Capture());
        var retired = Inventory(target);
        MetadataCleanupResult? cleanupResult = null;
        var cleanup = Measure(() => cleanupResult = writer.CleanupRetiredMetadata());
        var after = Inventory(target);
        // Complete frame accounting is outside compaction/cleanup timing.
        var footprint = MetadataFrameAccounting.Capture(repository);
        Assert.Equal(before.Segments, cleanupResult!.DeletedSegments);
        Assert.Equal(0, cleanupResult.DeferredSegments);
        Assert.Equal(0, footprint.SupersededPageBytes);
        Save(label, new { FixtureTransfer = copy, Before = before, BeforeCleanup = retired, After = after,
            Compact = compact, Cleanup = cleanup, CleanupResult = cleanupResult, Fold = fold, Footprint = footprint, Smb = protocol,
            CopiedSourceBytes = copiedSourceBytes, CopyReadRequests = copyReadRequests, WorkspaceBytesAtPrepared = workspaceBytesAtPrepared,
            Writer = Snapshot(writer.LastWriteStatistics) });
        File.WriteAllText(marker, "complete");
    }

    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline")] [InlineData("heavy")]
    public void MeasureBatchedLocalCompactionControls(string tier)
    {
        var target = Path.Combine(Remote, tier + "-compaction-controls-" + Guid.NewGuid().ToString("N"));
        var prepare = Measure(() => CopyTree(Path.Combine(Root, tier + "-dirty"), target));
        var repository = Repository.Open(target); var before = Inventory(target);
        using var writer = repository.OpenWriter(Path.Combine(Root, "work")); writer.LocalCompactionCheckInterval = 4096;
        var counters = SmbServerCounters.Capture();
        var compact = Measure(() => writer.CompactMetadata(Limits));
        var protocol = SmbServerCounters.Difference(counters, SmbServerCounters.Capture());
        var cleanup = Measure(() => writer.CleanupRetiredMetadata());
        var footprint = MetadataFrameAccounting.Capture(repository);
        Assert.Equal(0, footprint.SupersededPageBytes);
        Assert.Equal(tier == "baseline" ? 250_000 : 800_000, footprint.Indexes["Versions"].Entries);
        Save(tier + "-compaction-local-controls", new { Preparation = prepare, Before = before, After = Inventory(target),
            Compact = compact, Cleanup = cleanup, Footprint = footprint, Smb = protocol, LocalCompactionCheckInterval = 4096,
            OwnershipProbe = "FileStreamInfo against retained handle",
            Fold = Snapshot(writer.LastHistoryFoldStatistics), Writer = Snapshot(writer.LastWriteStatistics) });
        writer.Dispose(); DeleteOwnedChild(Remote, target);
    }

    internal void Save(string name, object value)
    {
        var text = JsonSerializer.Serialize(value, Json);
        Directory.CreateDirectory(ResultDirectory);
        File.WriteAllText(Path.Combine(ResultDirectory, name + ".json"), text);
        output.WriteLine(name + " " + text);
    }
    internal sealed record Metric(double ElapsedMs, double CpuMs, long AllocatedBytes, long ManagedLiveBefore,
        long ManagedLiveAfter, long WorkingSetBefore, long WorkingSetAfter, long LifetimePeakWorkingSet,
        long SampledPeakManagedBytes, long SampledPeakWorkingSetBytes, long SampledAdditionalManagedBytes,
        long SampledAdditionalWorkingSetBytes, int PeakSampleIntervalMs);
    internal static object? Snapshot(IoStatistics? value) => value is null ? null : new
    { value.PagesRead, value.PagesWritten, value.SemanticReads, value.FramesWritten, value.BytesWritten, value.SegmentsCreated,
        value.BytesRead, value.SegmentOpens, value.HandleEvictions, value.PeakHandles };
    internal static object? Snapshot(HistoryFoldStatistics? value) => value is null ? null : new
    { value.Records, value.Additions, value.Removals, value.Survivors, value.ScratchBytesWritten, value.PeakScratchBytes,
        value.RunsCreated, value.Merges, value.PeakBufferedEntries, value.PeakBufferedKeyBytes, value.PeakMergeReaders };
    internal static Metric Measure(Action action)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var memory = GC.GetTotalMemory(false); var working = process.WorkingSet64;
        long peakManaged = memory, peakWorking = working;
        using var stopSampling = new ManualResetEventSlim();
        // Diagnostic sampling includes runtime/previous garbage and may miss peaks shorter
        // than 20ms. It distinguishes an operation's sampled increase from lifetime peaks.
        var sampler = Task.Run(() =>
        {
            using var sampledProcess = Process.GetCurrentProcess();
            do
            {
                sampledProcess.Refresh();
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                peakWorking = Math.Max(peakWorking, sampledProcess.WorkingSet64);
            } while (!stopSampling.Wait(20));
        });
        var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        try { action(); }
        finally { timer.Stop(); stopSampling.Set(); sampler.GetAwaiter().GetResult(); }
        process.Refresh();
        var managedAfter = GC.GetTotalMemory(false); var workingAfter = process.WorkingSet64;
        peakManaged = Math.Max(peakManaged, managedAfter); peakWorking = Math.Max(peakWorking, workingAfter);
        return new(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocated, memory, managedAfter, working,
            workingAfter, process.PeakWorkingSet64, peakManaged, peakWorking,
            Math.Max(0, peakManaged - memory), Math.Max(0, peakWorking - working), 20);
    }
    internal sealed record MetadataInventory(int Segments, long Bytes);
    internal static MetadataInventory Inventory(string root)
    {
        var files = new DirectoryInfo(Path.Combine(root, "metadata")).GetFiles("*.seg");
        return new(files.Length, files.Sum(f => f.Length));
    }
    internal static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
            using var result = new FileStream(Path.Combine(target, Path.GetRelativePath(source, path)), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024);
            input.CopyTo(result, 1024 * 1024); result.Flush(true);
        }
    }
    internal static void CopyMetadataTree(string source, string target)
    {
        Directory.CreateDirectory(target); Directory.CreateDirectory(Path.Combine(target, "metadata")); Directory.CreateDirectory(Path.Combine(target, "versions"));
        foreach (var name in new[] { "repository.json", "state.json", "writer.lock" }) File.Copy(Path.Combine(source, name), Path.Combine(target, name));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(source, "metadata"), "*.seg"))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(Path.Combine(target, "metadata", Path.GetFileName(path)), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024);
            input.CopyTo(output, 1024 * 1024); output.Flush(true);
        }
    }
    internal static void DeleteOwnedChild(string root, string child)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(Path.GetDirectoryName(full), expected, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(full).Length < 33 || !Guid.TryParseExact(Path.GetFileName(full)[^32..], "N", out _))
            throw new IOException("Diagnostic cleanup target is not an exact generated child of its configured root.");
        if (SmbDiagnosticCopies.DeleteServerCreatedChild(expected, full)) return;
        Directory.Delete(full, true);
    }
}
