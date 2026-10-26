using System.Security.Cryptography;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static FluffHistory.Tests.SmbNetworkMeasurementTests;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbMetadataAccessMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline", 250_000)] [InlineData("heavy", 800_000)]
    public void CompareCoalescedTimelinePrototype(string tier, int survivors)
    {
        var results = new List<object>(); string? expected = null;
        foreach (var mode in new[] { "exact-captured-length", "coalesced-64KiB", "coalesced-256KiB", "coalesced-1MiB" })
        {
            var clone = SmbDiagnosticCopies.CreateServerClone(tier + "-dense", "timeline-cold-" + Guid.NewGuid().ToString("N"));
            var repo = Repository.Open(clone ?? Path.Combine(Remote, tier + "-dense"));
            var local = Path.Combine(Root, "timeline-candidate-" + Guid.NewGuid().ToString("N"));
            using var candidate = new MetadataReadCandidate(repo, repo.ReadAuthority(), mode, local);
            var counters = SmbServerCounters.Capture(); (long Count, string Digest) actual = default;
            var metric = Measure(() => actual = candidate.Timeline());
            Assert.Equal(survivors, actual.Count); expected ??= actual.Digest; Assert.Equal(expected, actual.Digest);
            results.Add(new { Mode = mode, Metric = metric, actual.Count, actual.Digest, candidate.ReadRequests, candidate.RequestedBytes,
                candidate.SegmentOpens, candidate.LocalBytes, Smb = SmbServerCounters.Difference(counters, SmbServerCounters.Capture()) });
            candidate.Dispose(); DeleteOwnedChild(Root, local); if (clone is not null) DeleteOwnedChild(Remote, clone);
        }
        new SmbNetworkMeasurementTests(output).Save(tier + "-timeline-coalesced", results);
    }

    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline", "dense")] [InlineData("heavy", "dense")]
    [InlineData("baseline", "dirty")] [InlineData("heavy", "dirty")]
    public void CompareCurrentMetadataAccess(string tier, string shape)
    {
        var remote = Path.Combine(Remote, tier + "-" + shape);
        if (!Directory.Exists(remote)) CopyTree(Path.Combine(Root, tier + "-" + shape), remote);
        var repository = Repository.Open(remote); var authority = repository.ReadAuthority();
        var measurements = new List<object>(); string? expected = null;
        var clones = new List<string>();
        var localDirectories = new List<string>();
        Repository CandidateRepository()
        {
            if (shape != "dense") return repository;
            var clone = SmbDiagnosticCopies.CreateServerClone(tier + "-dense", "access-cold-" + Guid.NewGuid().ToString("N"));
            if (clone is null) return repository;
            clones.Add(clone); return Repository.Open(clone);
        }
        using (var reader = CandidateRepository().OpenReader())
        {
            string? digest = null; long count = 0;
            var before = SmbServerCounters.Capture();
            var metric = Measure(() =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var value in reader.EnumerateCurrentComparison()) { hash.AppendData(MetadataReadCandidate.Encode(value)); count++; }
                digest = Convert.ToHexString(hash.GetHashAndReset());
            });
            expected = digest;
            var stats = reader.LastComparisonStatistics!;
            measurements.Add(new { Mode = After ? "production-selected" : "production-grouped", Metric = metric, Count = count, Digest = digest,
                stats.DestinationReadRequests, stats.DestinationBytesRequested, stats.Io.SegmentOpens,
                LocalBytes = 0, Smb = SmbServerCounters.Difference(before, SmbServerCounters.Capture()) });
        }
        foreach (var mode in new[] { "exact-captured-length", "coalesced-64KiB", "coalesced-256KiB", "coalesced-1MiB", "touched-segment" })
        {
            var localDirectory = Path.Combine(Root, "access-" + Guid.NewGuid().ToString("N")); localDirectories.Add(localDirectory);
            using var candidate = new MetadataReadCandidate(CandidateRepository(), authority, mode, localDirectory);
            (long Count, string Digest) result = default;
            var before = SmbServerCounters.Capture(); var metric = Measure(() => result = candidate.Current());
            Assert.Equal(expected, result.Digest);
            measurements.Add(new { Mode = mode, Metric = metric, result.Count, result.Digest, candidate.ReadRequests, candidate.RequestedBytes,
                candidate.SegmentOpens, candidate.LocalBytes, Smb = SmbServerCounters.Difference(before, SmbServerCounters.Capture()) });
        }
        var localized = Path.Combine(Root, "localized-" + Guid.NewGuid().ToString("N"));
        localDirectories.Add(localized);
        var copySource = CandidateRepository().DirectoryPath;
        var captureBefore = SmbServerCounters.Capture(); var copy = Measure(() => CopyMetadataTree(copySource, localized));
        var copySmb = SmbServerCounters.Difference(captureBefore, SmbServerCounters.Capture());
        var localRepo = Repository.Open(localized); string? localDigest = null; long localCount = 0;
        var compute = Measure(() =>
        {
            using var reader = localRepo.OpenReader(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var value in reader.EnumerateCurrentComparison()) { hash.AppendData(MetadataReadCandidate.Encode(value)); localCount++; }
            localDigest = Convert.ToHexString(hash.GetHashAndReset());
        });
        Assert.Equal(expected, localDigest);
        measurements.Add(new { Mode = "full-local-diagnostic-clone", Copy = copy, Compute = compute, TotalMs = copy.ElapsedMs + compute.ElapsedMs,
            Count = localCount, Digest = localDigest, LocalBytes = Inventory(localized).Bytes, Smb = copySmb });
        new SmbNetworkMeasurementTests(output).Save(tier + "-" + shape + "-access", new { Measurements = measurements,
            CacheCondition = clones.Count > 0 ? "Independent server-side clone per candidate, not previously accessed by this client; control reads precede timing. Server/OS caches uncontrolled." : "Shared fixture, uncontrolled client/server/OS caches." });
        foreach (var clone in clones) DeleteOwnedChild(Remote, clone);
        foreach (var localDirectory in localDirectories) DeleteOwnedChild(Root, localDirectory);
    }

    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline")] [InlineData("heavy")]
    public void CompareHistoryLocalComputation(string tier)
    {
        var remote = Repository.Open(Path.Combine(Remote, tier + "-dense"));
        var localPath = Path.Combine(Root, "history-localized-" + Guid.NewGuid().ToString("N"));
        var transfer = Measure(() => CopyMetadataTree(remote.DirectoryPath, localPath));
        var local = Repository.Open(localPath); Assert.Equal(remote.ReadAuthority(), local.ReadAuthority());
        var measurements = new List<object>();
        foreach (var (location, repo) in new[] { ("remote", remote), ("local", local) })
        {
            foreach (var folder in new[] { "", "Documents/area00" })
            {
                using var reader = repo.OpenReader(); var scope = new HistoryScope(FoundationTests.Root, folder);
                HistoryPage<DateTimeOffset>? first = null;
                var counters = SmbServerCounters.Capture();
                var cold = Measure(() => first = reader.GetTimelinePage(scope));
                var coldStats = reader.HistoryStatistics;
                Assert.Equal(128, first!.Items.Count);
                var warm = Measure(() => reader.GetTimelinePage(scope, cursor: first!.Continuation));
                var warmStats = reader.HistoryStatistics;
                Assert.Equal(coldStats, warmStats);
                StoredVersion? newestVersion = null;
                var newest = Measure(() => newestVersion = reader.GetNewestVersion(new(FoundationTests.Root, SmbDiagnosticFixture.Name(0))));
                Assert.NotNull(newestVersion); var newestStats = reader.HistoryStatistics;
                HistoryPage<ScopeHistoryItem>? sparsePage = null;
                var sparse = Measure(() => sparsePage = reader.GetScopeHistoryPage(scope, new(FoundationTests.Time), 128));
                Assert.Single(sparsePage!.Items); Assert.Equal(SmbDiagnosticFixture.Name(0), sparsePage.Items[0].Version.Metadata.RelativePath);
                measurements.Add(new { Location = location, Folder = folder, Cold = cold, ColdStatistics = coldStats, Warm = warm,
                    WarmHistorical = Difference(coldStats, warmStats), RetainedSnapshotBytes = reader.TimelineCacheStatistics.RetainedTimestampBytes,
                    Newest = newest, NewestHistorical = Difference(warmStats, newestStats), SparseAsOf = sparse,
                    SparseHistorical = Difference(newestStats, reader.HistoryStatistics), SparseReturned = sparsePage.Items.Count,
                    Smb = SmbServerCounters.Difference(counters, SmbServerCounters.Capture()) });
            }
            using (var reader = repo.OpenReader())
            {
                long versions = 0, current = 0;
                var counters = SmbServerCounters.Capture();
                var retention = Measure(() => { foreach (var value in reader.EnumerateRetentionVersions()) { versions++; if (value.IsCurrent) current++; } });
                measurements.Add(new { Location = location, Retention = retention, Versions = versions, Current = current,
                    Statistics = reader.LastRetentionStatistics, Smb = SmbServerCounters.Difference(counters, SmbServerCounters.Capture()) });
            }
        }
        new SmbNetworkMeasurementTests(output).Save(tier + "-history-compute", new { FullLocalization = transfer, MetadataBytes = Inventory(localPath).Bytes, Measurements = measurements });
        DeleteOwnedChild(Root, localPath);
    }
    private static HistoryReadStatistics Difference(HistoryReadStatistics before, HistoryReadStatistics after) =>
        new(after.IndexPagesRead - before.IndexPagesRead, after.SemanticFramesResolved - before.SemanticFramesResolved,
            after.SegmentOpens - before.SegmentOpens, after.BytesRequested - before.BytesRequested);
}
