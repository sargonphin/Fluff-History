using System.IO.Compression;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static FluffHistory.Tests.SmbNetworkMeasurementTests;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbBackupPolicyMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline")] [InlineData("heavy")]
    public void CompareIncrementalAndEveryChangedBackupCompaction(string tier)
    {
        var baseline = Repository.Open(Path.Combine(Root, tier + "-dense"));
        var source = Path.Combine(Root, tier + "-source"); var marker = source + ".complete";
        StoredVersion[] current;
        using (var reader = baseline.OpenReader()) current = reader.EnumerateCurrent().ToArray();
        if (!File.Exists(marker))
        {
            Assert.False(Directory.Exists(source), "Inspect incomplete source fixture before rebuilding.");
            Directory.CreateDirectory(source); var buffer = new byte[4096];
            foreach (var version in current)
            {
                var path = Path.Combine(source, version.Metadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, buffer);
                File.SetLastWriteTimeUtc(path, version.Metadata.LastWriteTimeUtc.UtcDateTime);
            }
            File.WriteAllText(marker, "complete");
        }
        var results = new List<object>();
        foreach (var changes in new[] { 0, 1, 16, 128, 1024 })
        foreach (var compactAfterChange in new[] { false, true })
        {
            var label = $"{tier}-backup-{changes}-{compactAfterChange}";
            if (File.Exists(Path.Combine(ResultDirectory, label + ".json"))) continue;
            var changed = Enumerable.Range(0, changes).Select(i => current[(int)((long)i * current.Length / Math.Max(1, changes))]).ToArray();
            using var resetSource = new SourceReset(source, changed);
            foreach (var version in changed)
            {
                var path = Path.Combine(source, version.Metadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllBytes(path, new byte[4097]); File.SetLastWriteTimeUtc(path, version.Metadata.LastWriteTimeUtc.AddYears(5).UtcDateTime);
            }
            var remote = Path.Combine(Remote, label + "-" + Guid.NewGuid().ToString("N"));
            var preparation = Measure(() =>
            {
                if (SmbDiagnosticCopies.CreateServerClone(tier + "-dense", Path.GetFileName(remote)) is null)
                    CopyTree(baseline.DirectoryPath, remote);
            });
            var repo = Repository.Open(remote); var before = Inventory(remote); var starting = repo.ReadAuthority().Identity;
            var startingEpoch = repo.ReadAuthority().Root.Epoch;
            var exactState = File.ReadAllBytes(Path.Combine(remote, "state.json"));
            var counters = SmbServerCounters.Capture(); ScanResult? scan = null;
            var backup = Measure(() => scan = new FileHistoryEngine(repo, new(WorkspaceRoot: Path.Combine(Root, "work")))
                .Scan([new(new(FoundationTests.Root, "Source"), source)]));
            var backupSmb = SmbServerCounters.Difference(counters, SmbServerCounters.Capture());
            Assert.Equal(changes, scan!.VersionsCreated); Assert.Equal(current.Length - changes, scan.Unchanged);
            Metric? compact = null, cleanup = null; Dictionary<string, long>? compactSmb = null;
            if (compactAfterChange && scan.BatchesCommitted > 0)
            {
                using var writer = repo.OpenWriter(Path.Combine(Root, "work")); counters = SmbServerCounters.Capture();
                writer.LocalCompactionCheckInterval = 4096; // Explicit Phase A operation-local computation candidate.
                compact = Measure(() => writer.CompactMetadata(Limits));
                cleanup = Measure(() => writer.CleanupRetiredMetadata());
                compactSmb = SmbServerCounters.Difference(counters, SmbServerCounters.Capture());
            }
            var after = Inventory(remote); var final = repo.ReadAuthority().Identity;
            if (changes == 0)
            { Assert.Equal(exactState, File.ReadAllBytes(Path.Combine(remote, "state.json"))); Assert.Equal(before, after); Assert.Null(compact); }
            var finalEpoch = repo.ReadAuthority().Root.Epoch;
            Assert.Equal(compactAfterChange && changes > 0, startingEpoch != finalEpoch);
            object? pipeline = null;
            string? pipelineDirectory = null;
            if (compact is not null)
            {
                // Complete format-3 client workflow: ordinary remote segments must first be read.
                // There is no production server archive service or permanent archived authority.
                var local = Path.Combine(Root, label + "-pipeline-" + Guid.NewGuid().ToString("N"));
                pipelineDirectory = local; Directory.CreateDirectory(local);
                var inputDirectory = Path.Combine(local, "input");
                var rawInput = Measure(() => CopyMetadataTree(remote, inputDirectory));
                var archive = Path.Combine(local, "segments.zip"); var remoteArchive = Path.Combine(remote, "derived-transport.zip");
                var returned = Path.Combine(local, "returned.zip"); var extracted = Path.Combine(local, "extracted");
                var pack = Measure(() =>
                {
                    using var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write);
                    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
                    foreach (var segment in Directory.GetFiles(Path.Combine(inputDirectory, "metadata"), "*.seg"))
                    { using var input = File.OpenRead(segment); using var entry = zip.CreateEntry(Path.GetFileName(segment), CompressionLevel.Fastest).Open(); input.CopyTo(entry, 1024 * 1024); }
                });
                var upload = Measure(() => File.Copy(archive, remoteArchive));
                var download = Measure(() => File.Copy(remoteArchive, returned));
                var unpack = Measure(() => ZipFile.ExtractToDirectory(returned, extracted));
                foreach (var segment in Directory.GetFiles(Path.Combine(inputDirectory, "metadata"), "*.seg"))
                {
                    using var expected = File.OpenRead(segment); using var actual = File.OpenRead(Path.Combine(extracted, Path.GetFileName(segment)));
                    Assert.Equal(System.Security.Cryptography.SHA256.HashData(expected), System.Security.Cryptography.SHA256.HashData(actual));
                }
                pipeline = new { Level = "Fastest", RawInput = rawInput, Pack = pack, Upload = upload, Download = download, Unpack = unpack,
                    ArchiveBytes = new FileInfo(archive).Length,
                    TotalIncludingBackupCompactionCleanupMs = backup.ElapsedMs + compact.ElapsedMs + cleanup!.ElapsedMs +
                        rawInput.ElapsedMs + pack.ElapsedMs + upload.ElapsedMs + download.ElapsedMs + unpack.ElapsedMs };
            }
            var result = new { Tier = tier, Changes = changes, CompactAfterChangedBackup = compactAfterChange, Preparation = preparation,
                Before = before, After = after, Starting = starting, Final = final, StartingEpoch = startingEpoch, FinalEpoch = finalEpoch, Backup = backup, Scan = scan,
                Compaction = compact, Cleanup = cleanup, BackupSmb = backupSmb, CompactionSmb = compactSmb, Pipeline = pipeline,
                LocalCompactionCheckInterval = compact is null ? (int?)null : 4096,
                TotalBackupAndMaintenanceMs = backup.ElapsedMs + (compact?.ElapsedMs ?? 0) + (cleanup?.ElapsedMs ?? 0) };
            new SmbNetworkMeasurementTests(output).Save(label, result); results.Add(result);
            DeleteOwnedChild(Remote, remote);
            if (pipelineDirectory is not null) DeleteOwnedChild(Root, pipelineDirectory);
        }
        new SmbNetworkMeasurementTests(output).Save(tier + "-backup-policy", results);
    }

    private sealed class SourceReset(string source, StoredVersion[] versions) : IDisposable
    {
        public void Dispose()
        {
            // Even a failed diagnostic must not leave the reusable source fixture
            // modified for the next independently measured case.
            foreach (var version in versions)
            {
                var path = Path.Combine(source, version.Metadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllBytes(path, new byte[4096]); File.SetLastWriteTimeUtc(path, version.Metadata.LastWriteTimeUtc.UtcDateTime);
            }
        }
    }
}
