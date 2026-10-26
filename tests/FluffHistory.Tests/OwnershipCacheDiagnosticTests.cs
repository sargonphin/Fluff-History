using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class OwnershipCacheDiagnosticTests(ITestOutputHelper output)
{
    [ExternalM201RecoveryFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void PasswordRecoveryOfWindowsEnrolledRepositoryOnIndependentPlatform()
    {
        var path = Environment.GetEnvironmentVariable("FLUFF_HISTORY_M201_EXTERNAL_REPOSITORY")!;
        if (OperatingSystem.IsWindows() && !Directory.Exists(path))
        {
            using var created = Repository.CreateEncrypted(path, EncryptionTests.Password);
            EncryptionTests.Add(created); created.EnrollCurrentWindowsUser();
        }
        Assert.NotNull(RepositoryBootstrap.Read(path).WindowsUserKey);
        var before = TemporaryDirectory.Snapshot(path);
        using (var recovered = Repository.OpenWithPassword(path, EncryptionTests.Password))
        using (var reader = recovered.OpenReader())
        {
            var version = reader.EnumerateCurrent().Single();
            using var payload = reader.OpenVersionPayload(version.Identity)!;
            using var bytes = new MemoryStream(); payload.Content.CopyTo(bytes);
            Assert.Equal("canary-private-file-content"u8.ToArray(), bytes.ToArray());
        }
        Assert.Equal(before, TemporaryDirectory.Snapshot(path));
        output.WriteLine("Password-only recovery of a Windows-enrolled Format-4 repository passed on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }

    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void CompareWorkspaceDecodedCache()
    {
        var root = Environment.GetEnvironmentVariable("FLUFF_HISTORY_M201_MEASUREMENT_ROOT")!;
        Assert.False(string.IsNullOrEmpty(root), "Set a disposable M20.1 measurement root.");
        Directory.CreateDirectory(root); var path = Path.Combine(root, "repository");
        if (!Directory.Exists(path))
        {
            using var created = Repository.CreateEncrypted(path, EncryptionTests.Password);
            SmbDiagnosticFixture.Populate(created, 6000, 18000);
        }
        using var repo = Repository.OpenWithPassword(path, EncryptionTests.Password);
        var observations = new List<object>();
        var authority = repo.ReadAuthority();
        using var workspace = new MetadataWorkspace(repo, Path.Combine(root, "workspace"));
        var budget = Environment.GetEnvironmentVariable("FLUFF_HISTORY_M201_CACHE_MIB");
        if (budget is not null)
        {
            // Reflection lets the same diagnostic run against an instrumented M20 disk baseline.
            var property = typeof(MetadataWorkspace).GetProperty("MaximumDecodedBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            property.SetValue(workspace, long.Parse(budget, System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024);
        }
        var stats = new IoStatistics();
        var seen = new HashSet<(SegmentHeader, long)>(); long redecoded = 0, decodedBytes = 0;
        workspace.DecodedObserved = (segment, header) => { if (!seen.Add((segment, header.Physical))) redecoded++; decodedBytes += header.DecodedLength; };
        using (var segments = new SegmentReader(path, repo.Identity, authority.Root.Epoch, authority.Address, workspace, stats, crypto: repo.Crypto))
        using (var candidate = new MetadataReadCandidate(repo, authority, "m201", Path.Combine(root, "candidate"), segments.Read))
        {
            Measure("workspace-grouped-current-cold", () => Assert.Equal(6000, candidate.Current().Count));
            Measure("workspace-timeline", () => Assert.Equal(18000, candidate.Timeline().Count));
            Measure("workspace-grouped-current-revisit", () => Assert.Equal(6000, candidate.Current().Count));
            using var reader = repo.OpenWorkingReader(workspace);
            Measure("maintenance-history-fold-and-index-validation", () =>
            {
                using var fold = new SemanticHistoryFold(reader, workspace, new(), default);
                fold.ValidateIndexes(); Assert.Equal(18000, fold.Groups().Count());
            });
            void Measure(string operation, Action action)
            {
                var watch = Stopwatch.StartNew(); action(); watch.Stop();
                var counters = typeof(MetadataWorkspace).GetProperties(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(long) && p.Name.StartsWith("Decoded", StringComparison.Ordinal))
                    .ToDictionary(p => p.Name, p => p.GetValue(workspace));
                observations.Add(new { operation, elapsedMs = watch.Elapsed.TotalMilliseconds, cumulativeSegmentIo = Format4ProductionMeasurementTests.Snapshot(stats),
                    destinationSegmentFetches = workspace.DestinationSegmentFetches, destinationPhysicalRequests = workspace.DestinationRangeFetches,
                    destinationPhysicalBytes = workspace.DestinationBytesFetched,
                    decoded = counters, decodedBytesAllWorkspaceReaders = decodedBytes, redecodedAfterEviction = redecoded,
                    workspaceDiskBytes = Directory.GetFiles(workspace.DirectoryPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length),
                    decodedDiskFiles = Directory.GetFiles(workspace.DirectoryPath, "*.decoded", SearchOption.AllDirectories).Length });
            }
        }
        // These production grouped operations intentionally bypass the workspace and are controls.
        using (var reader = repo.OpenReader())
        {
            var watch = Stopwatch.StartNew(); Assert.Equal(6000, reader.EnumerateCurrentComparison().Count());
            observations.Add(new { operation = "production-grouped-current-control", elapsedMs = watch.Elapsed.TotalMilliseconds, io = Format4ProductionMeasurementTests.Snapshot(reader.LastComparisonStatistics!.Io) });
            watch.Restart(); Assert.NotEmpty(reader.GetTimelinePage(new(FoundationTests.Root)).Items);
            observations.Add(new { operation = "production-timeline-control", elapsedMs = watch.Elapsed.TotalMilliseconds, io = Format4ProductionMeasurementTests.Snapshot(reader.LastTimelineStatistics!.Reads.Io) });
        }
        var json = JsonSerializer.Serialize(new { currentPaths = 6000, versions = 18000, encrypted = true, budgetMiB = budget, observations,
            note = "Same persisted fixture, deterministic path/pass ordering; generated UUID/key entropy; VM uncontrolled OS caches. Workspace counters cumulative. Segment IO excludes fold's separate reader; workspace counters include it. M20 baseline requires counter-only instrumentation; its decoded bytes are disk bytes and reader RAM accounting is separate." }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json); File.WriteAllText(Environment.GetEnvironmentVariable("FLUFF_HISTORY_M201_EVIDENCE")!, json);
    }
}

public sealed class ExternalM201RecoveryFactAttribute : FactAttribute
{
    public ExternalM201RecoveryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLUFF_HISTORY_M201_EXTERNAL_REPOSITORY") is null)
            Skip = "Requires a synthetic Windows-enrolled M20.1 repository copy; set FLUFF_HISTORY_M201_EXTERNAL_REPOSITORY. No real account credentials.";
    }
}
