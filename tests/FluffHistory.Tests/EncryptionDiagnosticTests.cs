using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Windows;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class EncryptionDiagnosticTests(ITestOutputHelper output)
{
    [ExternalM20RecoveryFact] [Trait("Category", "MetadataDiagnostic")]
    public void RecoverWindowsWrittenRepositoryOnCleanEnvironment()
    {
        using var repo = Repository.OpenWithPassword(Environment.GetEnvironmentVariable("FLUFF_HISTORY_M20_EXTERNAL_REPOSITORY")!, EncryptionTests.Password);
        using var reader = repo.OpenReader(); Assert.Equal(1001, reader.EnumerateCurrentComparison().Count());
        var version = reader.GetNewestVersion(new(FoundationTests.Root, "sparse-extra-file"))!;
        using var payload = reader.OpenVersionPayload(version.Identity)!; using var copy = new MemoryStream(); payload.Content.CopyTo(copy);
        Assert.Equal("canary-private-file-content"u8.ToArray(), copy.ToArray());
        output.WriteLine("Windows-written completed Format-4 repository: password-only metadata and payload recovery PASS on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }
    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public void CompactPerformanceAndIndependentCopiedRepositoryRecovery()
    {
        using var temp = new TemporaryDirectory(); var queries = new List<object>(); var payloads = new List<object>();
        var watch = Stopwatch.StartNew(); using (var kdf = Repository.CreateEncrypted(temp.Child("unlock-cost"), EncryptionTests.Password)) { }
        watch.Restart(); using (var unlocked = Repository.OpenWithPassword(temp.Child("unlock-cost"), EncryptionTests.Password)) { }
        var unlockMs = watch.Elapsed.TotalMilliseconds;
        foreach (var encrypted in new[] { false, true })
        {
            using var repo = encrypted ? Repository.CreateEncrypted(temp.Child("encrypted"), EncryptionTests.Password, OperatingSystem.IsWindows()) : Repository.Create(temp.Child("plain"));
            // Small common corpus: 1,000 paths x 3 successful versions. No giant Baseline population.
            for (var i = 0; i < 3; i++) ShellRestoreMeasurementTests.Populate(repo, 1000, false);
            using var reference = repo.OpenReader(); var point = reference.EnumerateCurrent().Last();
            watch.Restart(); using (var reader = repo.OpenReader()) Assert.NotNull(reader.GetVersion(point.Identity)); var pointMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart(); using (var reader = repo.OpenReader()) Assert.Equal(1000, reader.EnumerateCurrentComparison().Count()); var currentMs = watch.Elapsed.TotalMilliseconds;
            long physical, decoded, ranges;
            watch.Restart(); using (var reader = repo.OpenReader())
            { Assert.NotEmpty(reader.GetTimelinePage(new(FoundationTests.Root)).Items); physical = reader.LastTimelineStatistics!.Reads.Io.PhysicalBytesRead; decoded = reader.LastTimelineStatistics.Reads.Io.DecodedBytes; ranges = reader.LastTimelineStatistics.Reads.Io.PhysicalReads; }
            var timelineMs = watch.Elapsed.TotalMilliseconds;
            var files = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg"); var before = files.ToDictionary(p => p, File.ReadAllBytes);
            EncryptionTests.Add(repo, "sparse-extra-file"); var totalBefore = before.Values.Sum(b => (long)b.Length);
            var totalAfter = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg").Sum(p => new FileInfo(p).Length);
            foreach (var p in before) Assert.Equal(p.Value, File.ReadAllBytes(p.Key).AsSpan(0, p.Value.Length).ToArray());
            queries.Add(new { encrypted, currentPaths = 1000, versions = 3000, pointMs, currentMs, timelineMs, physical, decoded, ranges, metadataBytes = totalBefore, sparseMetadataBytes = totalAfter - totalBefore, committedPrefixRewritten = 0 });
            if (!encrypted) continue;
            foreach (var length in new[] { 64 * 1024, 1024 * 1024, 16 * 1024 * 1024 })
            {
                using var writer = repo.OpenWriter(); using var draft = writer.CreatePayloadDraft(); var buffer = new byte[128 * 1024]; new Random(711).NextBytes(buffer);
                watch.Restart(); var hash = new XxHash64();
                for (var left = length; left > 0;) { var count = Math.Min(left, buffer.Length); hash.Append(buffer.AsSpan(0, count)); draft.Write(buffer.AsSpan(0, count)); left -= count; }
                draft.CompleteCopy(length); var writeMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); using var input = File.OpenRead(draft.StagingPath); using var decodedInput = new EncryptedPayload.Reader(repo, draft.VersionId, input, length);
                var restored = new XxHash64(); int read; long countRead = 0;
                while ((read = decodedInput.Read(buffer)) != 0) { restored.Append(buffer.AsSpan(0, read)); countRead += read; }
                Assert.Equal(hash.GetCurrentHash(), restored.GetCurrentHash()); Assert.Equal(length, countRead);
                payloads.Add(new { logicalBytes = length, physicalBytes = input.Length, writeMs, readMs = watch.Elapsed.TotalMilliseconds });
            }
            var copy = temp.Child("copied-recovery");
            foreach (var path in Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories))
            { var target = Path.Combine(copy, Path.GetRelativePath(repo.DirectoryPath, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target); }
            var bootstrapPath = Path.Combine(copy, "repository.json"); var bootstrap = RepositoryBootstrap.Read(copy) with { WindowsUserKey = null }; File.WriteAllBytes(bootstrapPath, bootstrap.Bytes());
            using var recovered = Repository.OpenWithPassword(copy, EncryptionTests.Password); using var copied = recovered.OpenReader(); Assert.Equal(1001, copied.EnumerateCurrentComparison().Count());
            // Optional export is a synthetic repository only, for independent Linux production recovery.
            var export = Environment.GetEnvironmentVariable("FLUFF_HISTORY_M20_RECOVERY_COPY");
            if (export is not null)
            {
                Directory.CreateDirectory(export);
                foreach (var path in Directory.GetFiles(copy, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(export, Path.GetRelativePath(copy, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target, false); }
            }
        }
        var evidence = new { passwordKdfIterations = 600000, unlockMs, metadataEncryptedOverheadPerBlock = 96, queries, payloads, copiedRecoveryWithoutWindowsSlot = "passed", timingContext = "native Windows VM; uncontrolled OS caches; not CI thresholds or SMB wire counters" };
        var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }); output.WriteLine(json);
        var pathOut = Environment.GetEnvironmentVariable("FLUFF_HISTORY_M20_EVIDENCE"); if (pathOut is not null) File.WriteAllText(pathOut, json);
    }

    [MetadataDiagnosticFact] [Trait("Category", "MetadataDiagnostic")]
    public async Task NativeEncryptedShellUsesVerifiedRestrictedSelectedStaging()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var identities = ShellRestoreMeasurementTests.Populate(repo, 6, true);
        var request = DisplayedRestoreTests.Request(repo, temp.Child("restored"), identities.Select(v => (DisplayedRestoreItem)new DisplayedRestoreFile(v)).ToArray(), "Documents");
        // Select immediate files and a folder, as the real displayed planner requires.
        request = request with { Items = [new DisplayedRestoreFile(identities[0]), new DisplayedRestoreFile(identities[1]), new DisplayedRestoreFolder("Work"), new DisplayedRestoreFile(identities[^1])] };
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = await new InteractiveRestore(repo).RestoreAsync(request); watch.Stop();
        Assert.Equal(7, result.Copied); Assert.Equal(0, result.HResult); Assert.Empty(result.Problems); Assert.Equal(1, result.PerformOperationsCount);
        Assert.Equal(7, result.PlaintextStagingFiles); Assert.Equal(result.PlannedBytes, result.PlaintextStagingBytes);
        var evidence = JsonSerializer.Serialize(new { kind = "M20-native-Shell", result.Copied, result.PlannedBytes, result.VerificationBytes, result.PeakVerificationHandles,
            result.PlaintextStagingBytes, result.PlaintextStagingFiles, stagingMs = result.PlaintextStagingElapsedTicks * 1000.0 / Stopwatch.Frequency,
            elapsedMs = watch.Elapsed.TotalMilliseconds, allocations = GC.GetTotalAllocatedBytes(true) - allocated, liveBytes = GC.GetTotalMemory(false) });
        output.WriteLine(evidence);
        var evidencePath = Environment.GetEnvironmentVariable("FLUFF_HISTORY_M20_SHELL_EVIDENCE"); if (evidencePath is not null) File.WriteAllText(evidencePath, evidence);
        using var reader = repo.OpenReader(); var buffer = new byte[128 * 1024];
        foreach (var id in identities)
        {
            var v = reader.GetVersion(id)!; var relative = string.Join(Path.DirectorySeparatorChar, v.Metadata.RelativePath.Split('/').Skip(1));
            using var input = File.OpenRead(Path.Combine(request.DestinationDirectory!, relative)); var hash = new XxHash64(); int count;
            while ((count = input.Read(buffer)) != 0) hash.Append(buffer.AsSpan(0, count));
            Assert.Equal(v.Checksum.Value, Convert.ToHexString(hash.GetCurrentHash()));
        }
    }
}

public sealed class ExternalM20RecoveryFactAttribute : FactAttribute
{
    public ExternalM20RecoveryFactAttribute()
    { if (Environment.GetEnvironmentVariable("FLUFF_HISTORY_M20_EXTERNAL_REPOSITORY") is null) Skip = "Requires a copied synthetic Windows-written M20 repository."; }
}
