using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Trait("Category", "MetadataDiagnostic")]
public sealed class Format4ProductionMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticFact]
    public void ProjectStressWithoutPhysicalPopulation()
    {
        var p = SmbScaleProjectionTests.Project(250_000, 5_000_000, 0);
        var result = new { Phase = "stress-structural-only", p.Paths, p.Survivors, p.CurrentEntriesPerPage, p.VersionEntriesPerPage,
            p.CurrentPages, p.VersionPages, p.CurrentDepth, p.VersionDepth, p.SemanticBytes, p.IndexBytes,
            LogicalLowerBytes = p.DenseLowerBytes, LogicalUpperBytes = p.DenseUpperBytes,
            p.SegmentsLower, p.SegmentsUpper, Note = "No physical 5M population; physical compression may be projected separately from measured v4 Baseline ratio." };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Format4DesignGateTests.Root, "phase-b-stress.json"), json); output.WriteLine(json);
    }
    [MetadataDiagnosticFact]
    public void MeasureBaselineProduction()
    {
        var root = Format4DesignGateTests.Root; var path = Path.Combine(root, "v4-final-production"); Directory.CreateDirectory(root);
        var results = new List<object>(); Repository repository;
        if (!File.Exists(Path.Combine(root, "v4-final-production.complete")))
        {
            Assert.False(Directory.Exists(path), "Inspect incomplete fixture before rebuilding.");
            var reference = Path.Combine(root, "v4-dirty-reference");
            if (Environment.GetEnvironmentVariable("FLUFF_HISTORY_M191_USE_DIRTY_REFERENCE") == "1" && Directory.Exists(reference))
            {
                SmbNetworkMeasurementTests.CopyTree(reference, path); repository = Repository.Open(path);
                results.Add(new { Phase = "dirty-production-reference", BuildExcluded = true, Footprint = Footprint(path) });
            }
            else
            {
                repository = Repository.Create(path);
                var build = Format4DesignGateTests.Metric(() => SmbDiagnosticFixture.Populate(repository, 30_000, 250_000));
                results.Add(new { Phase = "dirty-build", Metric = build, Footprint = Footprint(path) });
            }
            using var writer = repository.OpenWriter(Path.Combine(root, "v4-work"));
            var compact = Format4DesignGateTests.Metric(() => writer.CompactMetadata(SmbNetworkMeasurementTests.Limits));
            writer.CleanupRetiredMetadata(); results.Add(new { Phase = "compaction", Metric = compact, Footprint = Footprint(path) });
            File.WriteAllText(Path.Combine(root, "v4-final-production-build.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            File.WriteAllText(Path.Combine(root, "v4-final-production.complete"), "complete");
        }
        else foreach (var item in JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(root, "v4-final-production-build.json")))!) results.Add(item);
        repository = Repository.Open(path); var authority = repository.ReadAuthority();
        using (var reader = repository.OpenReader())
        {
            long count = 0; var metric = Format4DesignGateTests.Metric(() => { foreach (var item in reader.EnumerateCurrentComparison()) count++; });
            Assert.Equal(30_000, count); results.Add(new { Phase = "production-current-local", Metric = metric, Count = count, Io = Snapshot(reader.LastComparisonStatistics!.Io) });
        }
        using (var reader = repository.OpenReader())
        {
            var metric = Format4DesignGateTests.Metric(() => Assert.Equal(128, reader.GetTimelinePage(new(FoundationTests.Root)).Items.Count));
            results.Add(new { Phase = "production-cold-timeline-local", Metric = metric, Io = Snapshot(reader.LastTimelineStatistics!.Reads.Io) });
        }
        foreach (var ahead in new[] { 0, SegmentReader.NetworkReadWindowBytes })
        {
            var stats = new IoStatistics();
            using var segments = new SegmentReader(path, repository.Identity, authority.Root.Epoch, authority.Address, statistics: stats, readAheadBytes: ahead);
            using var candidate = new MetadataReadCandidate(repository, authority, "v4-container", root, segments.Read);
            var current = Format4DesignGateTests.Metric(() => Assert.Equal(30_000, candidate.Current().Count));
            results.Add(new { Phase = "production-reader-current", LogicalReadAhead = ahead, Metric = current, Io = Snapshot(stats) });
        }
        FrameAddress late;
        using (var reader = repository.OpenReader()) late = reader.VersionIndex.Range(authority.Root.Versions).Select(e => e.Value).MaxBy(a => a.Offset)!;
        var randomStats = new IoStatistics();
        using (var segment = new SegmentReader(path, repository.Identity, authority.Root.Epoch, authority.Address, statistics: randomStats))
        {
            var metric = Format4DesignGateTests.Metric(() => segment.Read(late));
            results.Add(new { Phase = "production-random-late-frame", Metric = metric, Io = Snapshot(randomStats) });
        }
        var sparsePath = Path.Combine(root, "v4-sparse-" + Guid.NewGuid().ToString("N"));
        SmbNetworkMeasurementTests.CopyTree(path, sparsePath); var sparseRepository = Repository.Open(sparsePath);
        using (var writer = sparseRepository.OpenWriter(Path.Combine(root, "v4-sparse-work")))
        using (var reader = sparseRepository.OpenReader())
        {
            var before = Footprint(sparsePath); PreparedMutation? prepared = null;
            var prepare = Format4DesignGateTests.Metric(() => prepared = writer.PrepareMutation(new([FoundationTests.Prepare(writer, "new-sparse-file")]), reader.Authority));
            using (prepared!)
            {
                var fetched = prepared!.Workspace.DestinationBytesFetched; var append = prepared.Transfers.Sum(t => t.FinalLength - t.OriginalLength);
                var commit = Format4DesignGateTests.Metric(() => prepared.Commit());
                results.Add(new { Phase = "production-one-file-publication", Prepare = prepare, Commit = commit, WorkspaceFetched = fetched,
                    WorkspaceDecodedRam = prepared.Workspace.DecodedCacheBytes, WorkspaceDecodedRamPeak = prepared.Workspace.DecodedCachePeakBytes,
                    WorkspacePhysicalRangeDiskBytes = fetched, PhysicalAppend = append,
                    Writer = Snapshot(writer.LastWriteStatistics!), Before = before, After = Footprint(sparsePath) });
            }
        }
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        File.WriteAllText(Path.Combine(root, "phase-b.json"), json); output.WriteLine(json);
    }
    internal static object Snapshot(IoStatistics s) => new { s.BytesRead, s.BytesWritten, s.PhysicalBytesRead, s.PhysicalReads,
        s.BlocksDecoded, s.DecodedBytes, s.DecodedCacheReadBytes, s.PeakDecodedBytes, s.PhysicalBytesWritten, s.BlocksWritten, s.SegmentOpens, s.PeakHandles };
    internal static object Footprint(string path)
    {
        long logical = 0, physical = 0, blocks = 0;
        foreach (var name in Directory.GetFiles(Path.Combine(path, "metadata"), "*.seg"))
        {
            using var stream = File.OpenRead(name); physical += stream.Length; logical += Format.SegmentHeaderBytes;
            var bytes = new byte[160]; stream.ReadExactly(bytes); var segment = MetadataCodec.DecodeSegment(bytes);
            byte[] Read(long offset, int count) { stream.Position = offset; var result = new byte[count]; stream.ReadExactly(result); return result; }
            for (long at = 160; at < stream.Length;)
            { var h = MetadataBlock.ReadHeader(Read, segment, at, stream.Length); logical += h.DecodedLength; blocks++; at = h.End; }
        }
        return new { LogicalBytes = logical, PhysicalBytes = physical, Blocks = blocks };
    }
}
