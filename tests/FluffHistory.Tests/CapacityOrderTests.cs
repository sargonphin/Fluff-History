using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class CapacityOrderTests
{
    internal static StoredVersion Version(string name, long sequence, int ordinal, int day, Guid? root = null, long length = 1) =>
        new(new(new(root ?? FoundationTests.Root, name), sequence, ordinal, Guid.NewGuid()),
            new(root ?? FoundationTests.Root, name, length, FoundationTests.Time, FoundationTests.Time.AddDays(-day), FileAttributes.Archive),
            FoundationTests.Time.AddDays(day), new("D24EC4F1A98C6E5B"));

    internal static void Populate(Repository repo, IReadOnlyList<(StoredVersion Version, bool Current)> input)
    {
        // Give the synthetic compacted base enough real generations for its original
        // publication identities; keep the normal root sequence/generation validator active.
        var maximumSequence = input.Max(p => p.Version.Identity.Sequence);
        using (var writer = repo.OpenWriter())
        {
            while (true)
            {
                using var reader = repo.OpenReader(); if (reader.Authority.Generation + 1 >= maximumSequence) break;
                writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "fixture-" + reader.Authority.Generation)]), reader.Authority);
            }
        }
        RetentionProjectionTests.Corrupt(repo, (reader, append) =>
        {
            FrameAddress? journal = null;
            FrameAddress Semantic(SemanticRecord record) { journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(record with { Previous = journal })); return journal; }
            var sources = input.Select(p => p.Version.Identity.Path.SourceRootId).Distinct().Select(id =>
                (PathKeys.Source(id), (FrameAddress?)Semantic(new(SemanticKind.SourceRoot, null, Source: new(id, "missing-source"))))).ToArray();
            var versions = new List<(byte[], FrameAddress?)>(); var currents = new List<(byte[], FrameAddress?)>();
            foreach (var (version, current) in input)
            {
                var address = Semantic(new(SemanticKind.VersionAdded, null, Version: version)); versions.Add((PathKeys.Version(version.Identity), address));
                if (current) { Semantic(new(SemanticKind.CurrentUpsert, null, Path: version.Identity.Path, VersionAddress: address)); currents.Add((PathKeys.Current(version.Identity.Path), address)); }
            }
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
            return reader.Captured.Root with { Sequence = input.Max(p => p.Version.Identity.Sequence), Journal = journal,
                Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(null, versions),
                Current = new OrderedIndex(IndexKind.Current, Read, append).Update(null, currents), Sources = new OrderedIndex(IndexKind.Sources, Read, append).Update(null, sources) };
        });
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(4096)]
    public void DerivedOrderMatchesIndependentSortAcrossRootsTiesRollbackAndFrozenPaths(int chunk)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var roots = new[] { FoundationTests.Root, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Guid.Parse("00000001-0000-0000-0000-000000000001") };
        var input = Enumerable.Range(0, 129).Select(i => (Version($"é/\U00010428/p{i:D4}", i / 4 + 1, i % 4, i % 3 == 0 ? -2 : i % 5, roots[i % 3]), Current: i % 7 == 0)).ToArray();
        Populate(repo, input); using var reader = repo.OpenReader(); var before = RetentionTests.Inventory(repo); var result = new CapacityRetentionResult(reader.Authority, 0);
        var options = new CapacityRetentionOptions(SortChunkEntries: chunk, WorkspaceRoot: temp.Child("scratch"));
        string directory;
        using (var order = CapacityOrder.Derive(repo, reader, options, result, default))
        {
            directory = order.DirectoryPath;
            var expected = input.Where(p => !p.Current).Select(p => p.Item1).OrderBy(v => v.StoredUtc).ThenBy(v => v.Identity.Sequence).ThenBy(v => v.Identity.Ordinal).ToArray();
            var actual = order.Candidates().ToArray(); Assert.Equal(expected.Select(v => v.Identity), actual.Select(v => v.Identity));
            Assert.Equal(expected.Select(v => v.StoredUtc), actual.Select(v => v.StoredUtc)); Assert.Equal(expected.Length, result.Eligible);
            Assert.Equal(129, result.Sort.InputVersions); Assert.Equal(129, result.Sort.PublicationRecordsValidated); Assert.Equal(1, result.HistoryScans);
            Assert.InRange(result.Sort.PeakChunkEntries, 1, chunk); Assert.InRange(result.Sort.PeakChunkBytes, 1, options.SortChunkBytes);
            Assert.InRange(result.Sort.PeakOpenFiles, 1, 10); Assert.InRange(result.Sort.PeakReadFiles, 1, 9); Assert.InRange(result.Sort.PeakMergeFanIn, 0, 8);
        }
        Assert.False(Directory.Exists(directory)); Assert.Equal(before, RetentionTests.Inventory(repo));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AmbiguousPublicationIdentityIsRejectedEvenWithDifferentTimesOrProtectedRow(bool protectedRow)
    {
        using var f = new HistoryFixture(); Populate(f.Repository, [(Version("a", 1, 0, 1), protectedRow), (Version("b", 1, 0, -2), false)]);
        var before = RetentionTests.Inventory(f.Repository); var error = Assert.Throws<CapacityRetentionException>(() => CapacityRetentionTests.Engine(f.Repository).EnsureCapacity(0));
        Assert.IsType<InvalidDataException>(error.InnerException); Assert.Equal(0, error.Result.Removed); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    [Theory]
    [InlineData("live")] [InlineData("written")] [InlineData("versions")] [InlineData("cancel")]
    public void WorkLimitsAndCancellationDiscardOnlyOwnedScratchBeforeAnyRemoval(string stop)
    {
        using var f = new HistoryFixture(); for (int i = 0; i < 10; i++) f.Add(day: i);
        var scratchRoot = f.Temp.Child("scratch"); Directory.CreateDirectory(scratchRoot); var other = Path.Combine(scratchRoot, "unrelated"); File.WriteAllText(other, "keep");
        var options = new CapacityRetentionOptions(WorkspaceRoot: scratchRoot, SortChunkEntries: 1,
            MaximumScratchBytes: stop == "live" ? 1 : 1_073_741_824, MaximumScratchBytesWritten: stop == "written" ? 1 : 8_589_934_592,
            MaximumVersions: stop == "versions" ? 1 : 2_000_000);
        using var cts = new CancellationTokenSource(); var engine = new CapacityRetentionEngine(f.Repository, options, CapacityRetentionTests.Pressure());
        engine.SortObserve = (stage, _) => { if (stop == "cancel" && stage == CapacitySortStage.RecordWritten) cts.Cancel(); };
        var before = RetentionTests.Inventory(f.Repository); var result = engine.EnsureCapacity(0, cts.Token);
        Assert.Equal(stop == "cancel" ? CapacityRetentionOutcome.Cancelled : CapacityRetentionOutcome.WorkLimitExceeded, result.Outcome);
        Assert.Equal(0, result.Removed); Assert.Equal(before, RetentionTests.Inventory(f.Repository)); Assert.Empty(Directory.GetDirectories(scratchRoot)); Assert.Equal("keep", File.ReadAllText(other));
    }

    [Theory]
    [InlineData("hash")] [InlineData("length")] [InlineData("truncated")] [InlineData("extra")]
    public void CorruptScratchStopsBeforePublicationAndLeavesRepositoryUnchanged(string defect)
    {
        using var f = new HistoryFixture(); for (int i = 0; i < 4; i++) f.Add(day: i);
        var engine = new CapacityRetentionEngine(f.Repository, new(SortChunkEntries: 1, WorkspaceRoot: f.Temp.Child("scratch")), CapacityRetentionTests.Pressure()); bool injected = false;
        engine.SortObserve = (stage, path) =>
        {
            if (stage != CapacitySortStage.RunWritten || injected) return; injected = true;
            var bytes = File.ReadAllBytes(path);
            if (defect == "hash") bytes[^1] ^= 1;
            if (defect == "length") System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, int.MaxValue);
            if (defect == "truncated") bytes = bytes[..^1]; if (defect == "extra") bytes = [.. bytes, 1];
            File.WriteAllBytes(path, bytes);
        };
        var before = RetentionTests.Inventory(f.Repository); var error = Assert.Throws<CapacityRetentionException>(() => engine.EnsureCapacity(0));
        Assert.Equal(0, error.Result.Removed); Assert.True(injected); Assert.Equal(before, RetentionTests.Inventory(f.Repository)); Assert.Empty(Directory.GetDirectories(f.Temp.Child("scratch")));
    }

    [Fact]
    public void ByteBoundsForceOneCandidateChunksAndBatchesForLongPaths()
    {
        using var f = new HistoryFixture(); var prefix = string.Join('/', Enumerable.Repeat(new string('a', 200), 9));
        for (int i = 0; i < 3; i++) { var path = prefix + "/" + i; f.Add(path); f.Remove(path); }
        var result = new CapacityRetentionEngine(f.Repository, new(SortChunkBytes: 8512, MaximumCandidateBytes: 8512), CapacityRetentionTests.Pressure()).EnsureCapacity(0);
        Assert.Equal(3, result.Removed); Assert.Equal(3, result.BatchesCommitted); Assert.Equal(1, result.MaximumBatchCount); Assert.Equal(1, result.Sort.PeakChunkEntries);
    }

    [Fact]
    public void LogicalAccountingOverflowStopsBeforeTheNextBatchAndPreservesConfirmedCounters()
    {
        using var f = new HistoryFixture(); var first = Version("a", 1, 0, 0, length: long.MaxValue); var next = Version("b", 1, 1, 1);
        Populate(f.Repository, [(first, false), (next, false)]);
        var error = Assert.Throws<CapacityRetentionException>(() => CapacityRetentionTests.Engine(f.Repository).EnsureCapacity(0));
        Assert.IsType<OverflowException>(error.InnerException); Assert.Equal(1, error.Result.Removed); Assert.Equal(1, error.Result.BatchesCommitted); Assert.Equal(long.MaxValue, error.Result.RemovedLogicalBytes);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(first.Identity)); Assert.NotNull(reader.GetVersion(next.Identity));
    }
}
