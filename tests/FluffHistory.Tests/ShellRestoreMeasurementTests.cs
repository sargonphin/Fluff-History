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

public sealed class ShellRestoreMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory] [Trait("Category", "MetadataDiagnostic")]
    [InlineData(6, false)] [InlineData(1000, true)]
    public async Task MeasureOneShellQueue(int smallFiles, bool largeFile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Shell diagnostic requires Windows.");
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var identities = Populate(repo, smallFiles, largeFile);
        var before = Inventory(repo); var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var destination = temp.Child("restored");
        var selections = new List<DisplayedRestoreItem> { new DisplayedRestoreFile(identities[0]), new DisplayedRestoreFile(identities[1]), new DisplayedRestoreFolder("Work") };
        if (largeFile) selections.Add(new DisplayedRestoreFile(identities[^1]));
        var request = DisplayedRestoreTests.Request(repo, destination, selections, "Documents");
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = await new InteractiveRestore(repo).RestoreAsync(request); watch.Stop();
        var allocations = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.Equal(0, result.HResult); Assert.False(result.OperationsAborted); Assert.Empty(result.Problems);
        Assert.Equal(identities.Count, result.Copied); Assert.Equal(identities.Count, result.QueuedCopies); Assert.Equal(identities.Count, result.VerifiedPayloads);
        Assert.Equal(1, result.PerformOperationsCount); Assert.Equal(0, result.MetadataWarnings);
        Assert.Equal(result.PlannedBytes, result.VerificationBytes); Assert.Equal(result.PlannedBytes, result.CopiedPayloadBytes);
        var finalFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
        Assert.Equal(identities.Count, finalFiles.Length); Assert.Equal(result.PlannedBytes, finalFiles.Sum(p => new FileInfo(p).Length));
        // Independent destination checksum validation is outside measured restore time/counters.
        var verificationBuffer = new byte[128 * 1024];
        using (var reader = repo.OpenReader())
        foreach (var identity in identities)
        {
            var version = reader.GetVersion(identity)!;
            var relative = string.Join(Path.DirectorySeparatorChar, version.Metadata.RelativePath.Split('/').Skip(1));
            var target = Path.Combine(destination, relative); using var input = File.OpenRead(target);
            var hash = new XxHash64();
            int read; while ((read = input.Read(verificationBuffer)) != 0) hash.Append(verificationBuffer.AsSpan(0, read));
            Assert.Equal(version.Checksum.Value, Convert.ToHexString(hash.GetCurrentHash()));
            Assert.Equal(version.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(target));
        }
        Assert.Equal(before, Inventory(repo)); Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        using var process = Process.GetCurrentProcess();
        output.WriteLine("SHELL_RESTORE_JSON " + JsonSerializer.Serialize(new { smallFiles, largeFile, result.SelectedVisibleItems, result.ExpandedFiles,
            result.QueuedCopies, result.PerformOperationsCount, result.PlannedBytes, result.PlanChargedBytes, result.VerifiedPayloads,
            result.VerificationBytes, result.Copied, result.CopiedPayloadBytes, result.PeakVerificationHandles,
            verificationMs = result.VerificationElapsedTicks * 1000.0 / Stopwatch.Frequency,
            shellOperationMs = result.ShellElapsedTicks * 1000.0 / Stopwatch.Frequency, totalMs = watch.ElapsedMilliseconds,
            allocations, liveBytes = GC.GetTotalMemory(false), peakWorkingSet = process.PeakWorkingSet64,
            destinationFiles = finalFiles.Length, destinationBytes = finalFiles.Sum(p => new FileInfo(p).Length),
            verificationBufferBytes = 128 * 1024, contentStagingBytes = 0, repositoryBytesWritten = 0,
            result.HResult, result.OperationsAborted, result.MetadataWarnings }));
    }

    internal static List<VersionIdentity> Populate(Repository repo, int smallFiles, bool largeFile)
    {
        using var writer = repo.OpenWriter(); using var before = repo.OpenReader();
        var drafts = new List<PayloadDraft>(); var prepared = new List<PreparedVersion>(); var identities = new List<VersionIdentity>();
        try
        {
            var buffer = new byte[128 * 1024];
            for (var i = 0; i < smallFiles + (largeFile ? 1 : 0); i++)
            {
                var big = i == smallFiles; var name = big ? "Documents/Large.dat" : i == 0 ? "Documents/Report.txt" : i == 1 ? "Documents/Notes.txt" : $"Documents/Work/Nested{i / 100:D2}/file{i:D4}.txt";
                var length = big ? 32 * 1024 * 1024 : 96; var draft = writer.CreatePayloadDraft(); drafts.Add(draft);
                Array.Fill(buffer, (byte)(i % 251)); var hash = new XxHash64();
                for (var remaining = length; remaining > 0;)
                { var count = Math.Min(remaining, buffer.Length); draft.Write(buffer.AsSpan(0, count)); hash.Append(buffer.AsSpan(0, count)); remaining -= count; }
                draft.CompleteCopy(length);
                prepared.Add(draft.PrepareVersion(new(FoundationTests.Root, name, length, FoundationTests.Time, FoundationTests.Time.AddDays(2), FileAttributes.Archive),
                    FoundationTests.Time.AddDays(3), new(Convert.ToHexString(hash.GetCurrentHash()))));
            }
            using var mutation = writer.PrepareMutation(new(prepared, sourceRoots: [new(FoundationTests.Root, "descriptive")]), before.Authority);
            foreach (var draft in drafts) draft.Install(); mutation.Commit();
            using var reader = repo.OpenReader(); foreach (var version in reader.EnumerateCurrent()) identities.Add(version.Identity);
            // Explicit order supports displayed Report/Notes selections independently of index order.
            return identities.OrderBy(v => v.Path.CanonicalPath.EndsWith("REPORT.TXT", StringComparison.Ordinal) ? 0 :
                v.Path.CanonicalPath.EndsWith("NOTES.TXT", StringComparison.Ordinal) ? 1 : v.Path.CanonicalPath.EndsWith("LARGE.DAT", StringComparison.Ordinal) ? 3 : 2).ToList();
        }
        finally { foreach (var draft in drafts) draft.Dispose(); }
    }
    private static Dictionary<string, (long, DateTime, FileAttributes)> Inventory(Repository repo) => Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p), File.GetAttributes(p)));
}

public sealed class ManualShellCollisionFactAttribute : FactAttribute
{
    public ManualShellCollisionFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FLUFF_HISTORY_SHELL_COLLISION") != "1")
            Skip = "Manual Windows collision probe: explicitly enable FLUFF_HISTORY_SHELL_COLLISION=1.";
    }
}
public sealed class ManualShellCollisionTests(ITestOutputHelper output)
{
    [ManualShellCollisionFact] [Trait("Category", "MetadataDiagnostic")]
    public async Task StandardShellCollisionUiProbe()
    {
        using var f = new HistoryFixture(); var version = f.Add("historical.txt", "historical bytes");
        var target = f.Temp.Child("out"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "historical.txt"), "existing bytes");
        var request = DisplayedRestoreTests.Request(f.Repository, target, [new DisplayedRestoreFile(version.Identity)]);
        var result = await new InteractiveRestore(f.Repository).RestoreAsync(request);
        output.WriteLine(JsonSerializer.Serialize(result)); // Human inspects Windows UI; no hardcoded dialog wording or decision.
    }
}
