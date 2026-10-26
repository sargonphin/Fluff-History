using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class VersionRemovalCorruptionTests
{
    private static void Append(Repository repo, params SemanticRecord[] records)
    {
        using var reader = repo.OpenReader(); var captured = reader.Captured;
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, captured.Root.Epoch, captured);
        var journal = captured.Root.Journal;
        foreach (var record in records) journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(record with { Previous = journal }));
        Publish(repo, captured, append, captured.Root with { Journal = journal });
    }
    private static void Publish(Repository repo, Authority prior, SegmentAppender append, MetadataRoot root)
    {
        root = root with { Generation = prior.Root.Generation + 1, Sequence = prior.Root.Sequence + 1, TransactionId = Guid.NewGuid(),
            PreviousGeneration = prior.Root.Generation, PreviousTransactionId = prior.Root.TransactionId, PreviousRootHash = prior.Address.Sha256 };
        var pointer = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
        AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, pointer)));
        AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
    }
    [Theory]
    [InlineData("duplicate-add")][InlineData("duplicate-remove")][InlineData("uuid-conflict")]
    [InlineData("remove-current")][InlineData("remove-before-add")][InlineData("uuid-alias")][InlineData("future-remove")]
    public void SemanticFoldRejectsInvalidHistoryIndependentOfIndexes(string corruption)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old, current;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); current = VersionRemovalTests.Add(repo, writer); }
        switch (corruption)
        {
            case "duplicate-add": Append(repo, new SemanticRecord(SemanticKind.VersionAdded, null, Version: old)); break;
            case "duplicate-remove": Append(repo, new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: old.Identity), new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: old.Identity)); break;
            case "uuid-conflict": Append(repo, new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: old.Identity with { VersionId = Guid.NewGuid() })); break;
            case "remove-current": Append(repo, new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: current.Identity)); break;
            case "future-remove": Append(repo, new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: old.Identity with { Sequence = long.MaxValue })); break;
            default:
                using (var reader = repo.OpenReader())
                {
                    var id = new VersionIdentity(new(FoundationTests.Root, "other"), reader.Authority.MutationSequence + 1, 0, corruption == "uuid-alias" ? old.Identity.VersionId : Guid.NewGuid());
                    var metadata = new LogicalFileMetadata(FoundationTests.Root, "other", 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive);
                    var version = new StoredVersion(id, metadata, FoundationTests.Time, old.Checksum);
                    if (corruption == "remove-before-add") Append(repo, new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: id), new SemanticRecord(SemanticKind.VersionAdded, null, Version: version));
                    else Append(repo, new SemanticRecord(SemanticKind.VersionAdded, null, Version: version));
                }
                break;
        }
        var state = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        using (var writer = repo.OpenWriter()) Assert.Throws<InvalidDataException>(() => writer.RebuildIndexes());
        using (var writer = repo.OpenWriter()) Assert.Throws<InvalidDataException>(() => writer.CompactMetadata());
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"))); Assert.True(File.Exists(repo.GetPayloadPath(current.Identity.VersionId)));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompactionRejectsMissingSurvivorOrResurrectedIndexEntryAndRebuildRepairs(bool reintroduceRemoved)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer); }
        FrameAddress addition;
        using (var reader = repo.OpenReader()) addition = reader.VersionIndex.Find(reader.Captured.Root.Versions, PathKeys.Version(old.Identity))!;
        if (reintroduceRemoved) using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader()) writer.RemoveVersions([old.Identity], reader.Authority, false);
        using (var reader = repo.OpenReader())
        using (var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, reader.Captured))
        {
            byte[] Read(FrameAddress address) => append.ReadNew(address) ?? reader.Read(address);
            var index = new OrderedIndex(IndexKind.Versions, Read, append);
            var versions = index.Update(reader.Captured.Root.Versions, [(PathKeys.Version(old.Identity), reintroduceRemoved ? addition : null)]);
            Publish(repo, reader.Captured, append, reader.Captured.Root with { Versions = versions });
        }
        using (var writer = repo.OpenWriter()) Assert.Throws<InvalidDataException>(() => writer.CompactMetadata());
        using (var writer = repo.OpenWriter()) { writer.RebuildIndexes(); writer.CompactMetadata(); }
        using var fresh = repo.OpenReader(); Assert.Equal(!reintroduceRemoved, fresh.GetVersion(old.Identity) is not null);
    }
    [Fact]
    public void CommittedRemovalWithoutMatchingIndexDeletionCannotAuthorizeCleanup()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer); }
        Append(repo, new SemanticRecord(SemanticKind.VersionRemoved, null, RemovedVersion: old.Identity));
        using var owner = repo.OpenWriter(); Assert.Throws<InvalidDataException>(() => owner.CleanupRetiredPayloads()); Assert.True(File.Exists(repo.GetPayloadPath(old.Identity.VersionId)));
    }
    [Fact]
    public void CleanupRejectsReparseParentSubstitutionAndCompactionKeepsUnknownAbsence()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter())
        {
            old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer);
            using var reader = repo.OpenReader(); writer.RemoveVersions([old.Identity], reader.Authority, false);
        }
        var path = repo.GetPayloadPath(old.Identity.VersionId); var shard = Path.GetDirectoryName(path)!;
        var held = shard + ".held"; Directory.Move(shard, held);
        try
        {
            Directory.CreateSymbolicLink(shard, held);
            using (var writer = repo.OpenWriter()) { writer.CompactMetadata(); Assert.Equal(1, VersionRemovalTests.Tombstones(repo)); }
            using (var writer = repo.OpenWriter()) Assert.Throws<IOException>(() => writer.CleanupRetiredPayloads());
            Assert.True(File.Exists(Path.Combine(held, Path.GetFileName(path))));
        }
        finally { if (Directory.Exists(shard)) Directory.Delete(shard); Directory.Move(held, shard); }
    }
    [Fact]
    public void FinalFileSubstitutionAfterValidationNeverDeletesSymlinkTarget()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer);
        using (var reader = repo.OpenReader()) writer.RemoveVersions([old.Identity], reader.Authority, false);
        var path = repo.GetPayloadPath(old.Identity.VersionId); var held = path + ".held"; var unrelated = temp.Child("unrelated"); File.WriteAllText(unrelated, "keep");
        writer.PayloadCleanupObserve = (stage, _) =>
        {
            if (stage != PayloadCleanupStage.Validated) return;
            File.Move(path, held); File.CreateSymbolicLink(path, unrelated);
        };
        Assert.Throws<InvalidDataException>(() => writer.CleanupRetiredPayloads());
        Assert.Equal("keep", File.ReadAllText(unrelated)); Assert.True(File.Exists(held)); Assert.Equal(1, VersionRemovalTests.Tombstones(repo));
    }
    [Fact]
    public void UnreadableFreshAuthorityDoesNotBecomePayloadExpiry()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer); using var captured = repo.OpenReader();
        writer.RemoveVersions([old.Identity], captured.Authority, false); File.WriteAllText(Path.Combine(repo.DirectoryPath, "state.json"), "{}");
        Assert.Throws<InvalidDataException>(() => captured.OpenVersionPayload(old.Identity));
    }
    [Theory]
    [InlineData("hash")][InlineData("length")][InlineData("truncate")][InlineData("extra")]
    public void ExternalScratchRejectsCorruptionAndBoundsMergeMemory(string damage)
    {
        using var temp = new TemporaryDirectory(); using var scratch = new HistoryScratch(temp.Child(), new(), default);
        var history = new HistoryRuns(scratch, SemanticHistoryFold.KeyOrder, 1);
        var address = new FrameAddress(Guid.NewGuid(), 1, 160, 0, FrameKind.Semantic, 1, new string('A', 64));
        for (var i = 0; i < 80; i++) history.Add(new(PathKeys.Version(new(new(FoundationTests.Root, "f" + i.ToString("D4")), 1, 0, Guid.NewGuid())), Guid.NewGuid(), SemanticKind.VersionRemoved, address));
        var run = history.Finish(); Assert.True(scratch.Statistics.PeakBufferedEntries <= 1); Assert.True(scratch.Statistics.PeakMergeReaders <= 8); Assert.True(scratch.Statistics.Merges > 1);
        var bytes = File.ReadAllBytes(run.Path);
        if (damage == "hash") bytes[40] ^= 1;
        if (damage == "length") bytes.AsSpan(0, 4).Fill(255);
        if (damage == "truncate") bytes = bytes[..^1];
        if (damage == "extra") bytes = [..bytes, 0];
        File.WriteAllBytes(run.Path, bytes);
        using var reader = new HistoryScratch.Reader(run, SemanticHistoryFold.KeyOrder, default);
        if (damage == "truncate") Assert.Throws<EndOfStreamException>(() => { while (reader.MoveNext()) { } });
        else Assert.Throws<InvalidDataException>(() => { while (reader.MoveNext()) { } });
    }
}
