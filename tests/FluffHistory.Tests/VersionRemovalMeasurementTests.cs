using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class VersionRemovalMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void MeasureTransientHistoryRemovalAndCompaction()
    {
        const int paths = 2048, rounds = 3, survivors = 16, batchSize = 128;
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using var writer = repo.OpenWriter(); var identities = new List<VersionIdentity>();
        for (var round = 0; round < rounds; round++)
        {
            foreach (var chunk in Enumerable.Range(0, paths).Chunk(256))
            {
                var versions = chunk.Select(i => FoundationTests.Prepare(writer, $"Transient/f{i:D5}.txt")).ToArray();
                using var reader = repo.OpenReader(); writer.Apply(new(versions, sourceRoots: [new(FoundationTests.Root, @"C:\Source")]), reader.Authority);
                using var confirmed = repo.OpenReader(); identities.AddRange(versions.Select(v => confirmed.GetNewestVersion(v.Path)!.Identity));
            }
        }
        using (var reader = repo.OpenReader())
        {
            var versions = Enumerable.Range(0, survivors).Select(i => FoundationTests.Prepare(writer, $"Protected/f{i:D3}.txt")).ToArray();
            writer.Apply(new(versions), reader.Authority);
        }
        foreach (var chunk in Enumerable.Range(0, paths).Chunk(batchSize))
        { using var reader = repo.OpenReader(); writer.Apply(new(currentRemovals: chunk.Select(i => new HistoryPath(FoundationTests.Root, $"Transient/f{i:D5}.txt"))), reader.Authority); }
        StoredVersion[] expectedSurvivors;
        using (var reader = repo.OpenReader()) expectedSurvivors = reader.EnumerateCurrent().ToArray();
        var held = new List<VersionPayload>();
        if (OperatingSystem.IsWindows())
        { using var reader = repo.OpenReader(); held.AddRange(identities.Take(8).Select(id => reader.OpenVersionPayload(id)!)); }
        try
        {
            var initial = MetadataFrameAccounting.Capture(repo); var clock = Stopwatch.StartNew();
            var allocation = GC.GetTotalAllocatedBytes(true); long metadata = 0, pages = 0, maxPages = 0, semanticBytes = 0, deleted = 0, deferred = 0, failed = 0;
            int batches = 0, removed = 0;
            foreach (var chunk in identities.Chunk(batchSize))
            {
                using var reader = repo.OpenReader(); var result = writer.RemoveVersions(chunk, reader.Authority);
                removed += result.Removed; deleted += result.Cleanup.Deleted; deferred += result.Cleanup.Deferred; failed += result.Cleanup.Failed;
                var stats = writer.LastWriteStatistics!; metadata += stats.BytesWritten; pages += stats.PagesWritten; maxPages = Math.Max(maxPages, stats.PagesWritten); batches++;
                semanticBytes += chunk.Sum(id => 96L + MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, reader.Captured.Root.Journal, RemovedVersion: id)).Length);
            }
            var removalMs = clock.ElapsedMilliseconds; var removalAllocated = GC.GetTotalAllocatedBytes(false) - allocation;
            var afterRemoval = MetadataFrameAccounting.Capture(repo); Assert.Equal(paths * rounds, removed); Assert.Equal(removed, VersionRemovalTests.Tombstones(repo));
            Assert.Equal(removed, afterRemoval.Physical["Semantic/VersionRemoved"].Count);
            Assert.Equal(semanticBytes, afterRemoval.Physical["Semantic/VersionRemoved"].Bytes); Assert.True(maxPages < initial.Indexes["Versions"].Pages);
            long alreadyAbsent = 0, restartedDeleted = 0, restartedDeferred = 0; PayloadCleanupCursor? cursor = null;
            do
            {
                var page = writer.CleanupRetiredPayloads(new(MaximumPayloads: batchSize), cursor);
                alreadyAbsent += page.Result.AlreadyAbsent; restartedDeleted += page.Result.Deleted; restartedDeferred += page.Result.Deferred; cursor = page.Continuation;
            } while (cursor is not null);
            clock.Restart(); writer.RebuildIndexes(); var rebuildMs = clock.ElapsedMilliseconds;
            var fold = writer.LastHistoryFoldStatistics!; var rebuildOutput = writer.LastWriteStatistics!.BytesWritten;
            Assert.True(fold.Removals == removed && fold.Merges > 0);
            var beforeCompaction = MetadataFrameAccounting.Capture(repo);
            writer.CompactMetadata(); var compactionOutput = writer.LastWriteStatistics!.BytesWritten;
            writer.CleanupRetiredMetadata(); var pending = MetadataFrameAccounting.Capture(repo);
            var anchors = VersionRemovalTests.Tombstones(repo); Assert.Equal(held.Count, anchors);
            foreach (var payload in held) payload.Dispose(); held.Clear();
            var completed = writer.CleanupRetiredPayloads().Result;
            writer.RebuildIndexes();
            writer.CompactMetadata(); writer.CleanupRetiredMetadata(); var final = MetadataFrameAccounting.Capture(repo);
            Assert.Equal(0, VersionRemovalTests.Tombstones(repo));
            using (var reader = repo.OpenReader())
            {
                Assert.Equal(expectedSurvivors, reader.EnumerateCurrent());
                Assert.Equal(survivors, reader.VersionIndex.Range(reader.Captured.Root.Versions).Count());
                foreach (var id in identities) Assert.Null(reader.GetVersion(id));
            }
            Assert.Equal(removed, deleted + completed.Deleted); Assert.Equal(0, failed);
            Assert.True(fold.PeakBufferedEntries <= 4096); Assert.True(fold.PeakBufferedKeyBytes <= 4 * 1024 * 1024); Assert.True(fold.PeakMergeReaders <= 8);
            var resultData = new
            {
                Paths = paths, VersionsPerTransientPath = rounds, Removed = removed, BatchSize = batchSize, Batches = batches,
                SemanticRecordsAppended = removed, SemanticBytesAppended = semanticBytes, VersionPagesWritten = pages, MaximumPagesPerBatch = maxPages,
                InitialVersionPages = initial.Indexes["Versions"].Pages, TotalRemovalMetadataBytesWritten = metadata,
                PayloadsDeleted = deleted, PayloadsAlreadyAbsentOnRestart = alreadyAbsent, CleanupDeferred = deferred, CleanupFailed = failed,
                RestartDeleted = restartedDeleted, RestartDeferred = restartedDeferred, FinalCleanupDeleted = completed.Deleted,
                MetadataBeforeRemoval = initial.DiskBytes, MetadataAfterRemoval = afterRemoval.DiskBytes, MetadataBeforeCompaction = beforeCompaction.DiskBytes,
                MetadataAfterCompactionWithPending = pending.DiskBytes, MetadataAfterFinalCompaction = final.DiskBytes,
                TombstonesBeforeCompaction = removed, PendingAnchorsAfterCompaction = anchors, FinalAnchors = 0, SurvivingVersions = survivors,
                RebuildRecords = fold.Records, RebuildOutputBytes = rebuildOutput, CompactionOutputBytes = compactionOutput, ScratchPeakBytes = fold.PeakScratchBytes,
                ScratchBytesWritten = fold.ScratchBytesWritten, ScratchRuns = fold.RunsCreated, ScratchMerges = fold.Merges,
                PeakBufferedEntriesPerSorter = fold.PeakBufferedEntries, PeakBufferedKeyBytesPerSorter = fold.PeakBufferedKeyBytes,
                PeakMergeReaders = fold.PeakMergeReaders, RemovalAllocatedBytes = removalAllocated,
                RemovalMilliseconds = removalMs, RebuildMilliseconds = rebuildMs, StagingContentBytes = 0
            };
            output.WriteLine("M151_JSON " + JsonSerializer.Serialize(resultData));
        }
        finally { foreach (var payload in held) payload.Dispose(); }
    }
}
