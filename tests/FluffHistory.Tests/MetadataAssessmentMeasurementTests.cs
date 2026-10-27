using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Application;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Trait("Category", "MetadataDiagnostic")]
public sealed class MetadataAssessmentMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticFact]
    public void MeasureBaselineAssessmentAndDenseEquivalence()
    {
        // Admission precedes all directory creation, repository opening and population,
        // including reuse: an existing fixture does not waive storage safety.
        var root = LargeDiagnosticRoot.FromEnvironment(["baseline", "work"]);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "baseline");
        var reuse = Environment.GetEnvironmentVariable("FLUFF_HISTORY_METADATA_REUSE") == "1";
        Assert.True(reuse || !Directory.Exists(path), "Use a fresh owned root; never overwrite earlier measurements.");
        using var repo = reuse ? Repository.Open(path) : Repository.Create(path);
        var build = Stopwatch.StartNew();
        if (!reuse) SmbDiagnosticFixture.Populate(repo, 30_000, 250_000);
        build.Stop();
        void Stage(string phase) => File.AppendAllText(Path.Combine(root, "progress.log"), DateTimeOffset.UtcNow.ToString("O") + " " + phase + "\n");
        Stage("population available; begin bounded grouped oracle");
        var before = Digest(repo);
        Stage("oracle complete; inspect");
        using var writer = repo.OpenWriter(Path.Combine(root, "work"));
        var cheap = Stopwatch.StartNew(); var summary = writer.InspectActiveMetadata(); cheap.Stop();
        Stage("inspect complete; assess");
        var clock = Stopwatch.StartNew(); var assessment = writer.AssessMetadata(); clock.Stop();
        Stage("assessment complete");
        Assert.Equal(summary, assessment.Active);
        Assert.True(MetadataMaintenancePolicy.Eligible(summary.DecodedBytes, assessment.ReclaimableBytes));
        Assert.Equal(before, Digest(repo));
        var compact = Stopwatch.StartNew(); writer.CompactMetadata(); compact.Stop();
        Stage("compaction complete");
        var written = writer.LastWriteStatistics!;
        Assert.Equal(assessment.DenseDecodedBytes, written.BytesWritten - written.SegmentsCreated * Format.SegmentHeaderBytes - (written.SegmentsCreated - 1) * Format.SealBytes);
        Assert.Equal(before, Digest(repo));
        var cleanup = writer.CleanupRetiredMetadata();
        var after = writer.AssessMetadata();
        Assert.False(MetadataMaintenancePolicy.Eligible(after.Active.DecodedBytes, after.ReclaimableBytes));
        Assert.Equal(before, Digest(repo));
        var result = new { Paths = 30_000, Versions = 250_000, ReusedPopulation = reuse, BuildMs = build.ElapsedMilliseconds,
            PrefilterMs = cheap.Elapsed.TotalMilliseconds, AssessmentMs = clock.Elapsed.TotalMilliseconds,
            Assessment = assessment, CompactionMs = compact.ElapsedMilliseconds, PhysicalOutputBytes = written.PhysicalBytesWritten,
            FloorBytes = MetadataMaintenancePolicy.MinimumReclaimableBytes, Cleanup = cleanup, After = after,
            Digest = before, PayloadFiles = 0, Note = "Metadata-only production-format Baseline; no physical payload population or bare-metal timing claim." };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "assessment.json"), json); output.WriteLine(json);
    }

    private static string Digest(Repository repo)
    {
        using var workspace = new MetadataWorkspace(repo, null, streamMetadataReads: true);
        using var reader = repo.OpenWorkingReader(workspace); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var root in reader.EnumerateSourceRoots()) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(root));
        void Fold(IEnumerable<PageEntry> entries, int expected)
        {
            var count = 0;
            foreach (var window in entries.Chunk(4096))
            {
                var values = new FluffHistory.Core.StoredVersion[window.Length];
                foreach (var item in window.Select((entry, position) => (entry, position)).OrderBy(p => p.entry.Value.SegmentOrdinal).ThenBy(p => p.entry.Value.Offset))
                    values[item.position] = reader.Version(item.entry.Value);
                foreach (var value in values) { hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(value)); count++; }
            }
            Assert.Equal(expected, count);
        }
        Fold(reader.CurrentIndex.Range(reader.Captured.Root.Current), 30_000);
        Fold(reader.VersionIndex.Range(reader.Captured.Root.Versions), 250_000);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
