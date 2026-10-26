using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class VersionRemovalTests
{
    internal static StoredVersion Add(Repository repo, RepositoryWriter writer, string path = "foo.txt")
    {
        FoundationTests.Add(repo, writer, path); using var reader = repo.OpenReader(); return reader.GetNewestVersion(new(FoundationTests.Root, path))!;
    }
    internal static int Tombstones(Repository repo)
    {
        using var reader = repo.OpenReader(); int count = 0;
        for (var p = reader.Captured.Root.Journal; p is not null;)
        { var record = reader.Semantic(p); if (record.Kind == SemanticKind.VersionRemoved) count++; p = record.Previous; }
        return count;
    }
    [Fact]
    public void MultipleVersionsAtOnePathDisappearWithoutChangingCurrentAndNeverResurrect()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        writer.HistoryFoldBufferEntries = 1;
        var first = Add(repo, writer); var second = Add(repo, writer); var current = Add(repo, writer);
        using var before = repo.OpenReader(); var root = before.Captured.Root;
        var result = writer.RemoveVersions([first.Identity, second.Identity], before.Authority);
        Assert.Equal(2, result.Removed); Assert.Equal(2, result.Cleanup.Deleted); Assert.Equal(2, result.RemovedLogicalBytes);
        Assert.Equal(before.Authority.Generation + 1, result.Authority.Generation); Assert.Equal(before.Authority.MutationSequence + 1, result.Authority.MutationSequence);
        using (var reader = repo.OpenReader())
        {
            Assert.Equal(root.Current, reader.Captured.Root.Current); Assert.Equal(root.Sources, reader.Captured.Root.Sources);
            Assert.Equal(current, Assert.Single(reader.EnumerateVersions(current.Identity.Path)));
            Assert.Null(reader.GetVersion(first.Identity)); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
        }
        before.Dispose();
        for (var i = 0; i < 2; i++)
        {
            writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes();
            using var reader = repo.OpenReader(); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
            Assert.Equal(current, Assert.Single(reader.EnumerateVersions(current.Identity.Path))); Assert.Equal(0, Tombstones(repo));
        }
        Assert.True(writer.LastHistoryFoldStatistics!.PeakBufferedEntries <= 1);
    }
    [Fact]
    public void RemovedDeletedPathsFinalVersionAndTimelineDisappearWhileUnpublishedOrphanRemains()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var version = Add(repo, writer, "folder/gone");
        using (var reader = repo.OpenReader()) writer.Apply(new(currentRemovals: [version.Identity.Path]), reader.Authority);
        var orphan = Guid.NewGuid(); var orphanPath = repo.GetPayloadPath(orphan); Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!); File.WriteAllText(orphanPath, "keep");
        using (var reader = repo.OpenReader()) Assert.Equal(1, writer.RemoveVersions([version.Identity], reader.Authority).Removed);
        using (var reader = repo.OpenReader())
        { Assert.Empty(reader.GetTimelinePage(new(FoundationTests.Root)).Items); Assert.Empty(reader.GetScopeHistoryPage(new(FoundationTests.Root)).Items); }
        writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); Assert.Equal("keep", File.ReadAllText(orphanPath));
        using var fresh = repo.OpenReader(); Assert.Empty(fresh.EnumerateVersions(version.Identity.Path));
    }
    [Fact]
    public void AbsentRequestIsNoOpAndCurrentOrWrongUuidAndDuplicateKeysAreRejected()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old, current;
        using (var writer = repo.OpenWriter()) { old = Add(repo, writer); current = Add(repo, writer); using var reader = repo.OpenReader(); writer.RemoveVersions([old.Identity], reader.Authority); }
        var inventory = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader())
        { var result = writer.RemoveVersions([old.Identity], reader.Authority); Assert.Equal(reader.Authority, result.Authority); Assert.Equal(0, result.Removed); Assert.Equal(1, result.AlreadyAbsent); Assert.Null(writer.LastWriteStatistics); }
        Assert.Equal(inventory, TemporaryDirectory.Snapshot(repo.DirectoryPath));
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader()) Assert.Throws<InvalidOperationException>(() => writer.RemoveVersions([current.Identity], reader.Authority));
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader()) Assert.Throws<InvalidDataException>(() => writer.RemoveVersions([current.Identity with { VersionId = Guid.NewGuid() }], reader.Authority));
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader()) Assert.Throws<ArgumentException>(() => writer.RemoveVersions([old.Identity, old.Identity], reader.Authority));
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader()) Assert.Throws<ArgumentException>(() => writer.RemoveVersions(Enumerable.Repeat(old.Identity, 4097), reader.Authority));
        Assert.Equal(inventory, TemporaryDirectory.Snapshot(repo.DirectoryPath));
    }
    [Fact]
    public void PendingAnchorSurvivesCompactionAndRestartWithoutAnyRetiredMetadata()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter())
        {
            old = Add(repo, writer); _ = Add(repo, writer);
            using (var reader = repo.OpenReader()) writer.RemoveVersions([old.Identity], reader.Authority, cleanupPayloads: false);
            writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes();
        }
        Assert.Equal(1, Tombstones(repo)); Assert.True(File.Exists(repo.GetPayloadPath(old.Identity.VersionId)));
        using (var writer = Repository.Open(repo.DirectoryPath).OpenWriter())
        {
            var result = writer.CleanupRetiredPayloads().Result; Assert.Equal(1, result.Deleted);
            Assert.Equal(1, writer.CleanupRetiredPayloads().Result.AlreadyAbsent);
            writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes();
        }
        Assert.Equal(0, Tombstones(repo));
    }
    [Fact]
    public void CleanupPagesAreBoundedAndStaleContinuationsFail()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var ids = Enumerable.Range(0, 4).Select(_ => Add(repo, writer).Identity).ToArray(); _ = Add(repo, writer);
        using (var reader = repo.OpenReader()) writer.RemoveVersions(ids, reader.Authority, cleanupPayloads: false);
        PayloadCleanupCursor? cursor = null; long deleted = 0;
        do { var page = writer.CleanupRetiredPayloads(new(2, 1), cursor); Assert.True(page.RecordsRead <= 2); Assert.True(page.Result.Inspected <= 1); deleted += page.Result.Deleted; cursor = page.Continuation; } while (cursor is not null);
        Assert.Equal(4, deleted);
        var continuation = writer.CleanupRetiredPayloads(new(1, 1)).Continuation; Assert.NotNull(continuation);
        writer.CompactMetadata(); Assert.Throws<InvalidOperationException>(() => writer.CleanupRetiredPayloads(continuation: continuation));
    }
    [Fact]
    public void OldCaptureExpiresWithinSameEpochEvenBeforeCleanupAndMissingSurvivorDoesNot()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var old = Add(repo, writer); var current = Add(repo, writer); using var captured = repo.OpenReader();
        writer.RemoveVersions([old.Identity], captured.Authority, cleanupPayloads: false);
        using (var reader = repo.OpenReader()) Assert.Equal(captured.Captured.Root.Epoch, reader.Captured.Root.Epoch);
        Assert.Throws<PayloadVersionExpiredException>(() => captured.OpenVersionPayload(old.Identity));
        writer.CleanupRetiredPayloads(); Assert.Throws<PayloadVersionExpiredException>(() => captured.OpenVersionPayload(old.Identity));
        File.Delete(repo.GetPayloadPath(current.Identity.VersionId));
        Assert.ThrowsAny<IOException>(() => captured.OpenVersionPayload(current.Identity));
        using var fresh = repo.OpenReader(); Assert.ThrowsAny<IOException>(() => fresh.OpenVersionPayload(current.Identity));
    }
    [WindowsFact]
    public void HeldPayloadAndReadOnlyAttributesDeferCleanupWithoutLosingAnchor()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var old = Add(repo, writer); _ = Add(repo, writer);
        using var reader = repo.OpenReader(); using var payload = reader.OpenVersionPayload(old.Identity)!;
        var result = writer.RemoveVersions([old.Identity], reader.Authority); Assert.Equal(1, result.Cleanup.Deferred); Assert.Equal(42, payload.Content.ReadByte());
        payload.Dispose(); reader.Dispose();
        var path = repo.GetPayloadPath(old.Identity.VersionId); File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deferred);
        writer.CompactMetadata(); writer.CleanupRetiredMetadata(); Assert.Equal(1, Tombstones(repo));
        File.SetAttributes(path, FileAttributes.Normal); Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deleted);
        writer.CompactMetadata(); Assert.Equal(0, Tombstones(repo));
    }
    [WindowsFact]
    public void ExtendedRepositoryPathsPreserveNativeReadSharingAndExactCleanup()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(@"\\?\" + temp.Child()); using var writer = repo.OpenWriter();
        var old = Add(repo, writer); _ = Add(repo, writer); using var reader = repo.OpenReader(); using var payload = reader.OpenVersionPayload(old.Identity)!;
        Assert.Equal(1, writer.RemoveVersions([old.Identity], reader.Authority).Cleanup.Deferred);
        payload.Dispose(); Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deleted);
    }
    [LinuxFact]
    public void OpenDescriptorContinuesAfterUnlink()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var old = Add(repo, writer); _ = Add(repo, writer); using var reader = repo.OpenReader(); using var payload = reader.OpenVersionPayload(old.Identity)!;
        Assert.Equal(1, writer.RemoveVersions([old.Identity], reader.Authority).Cleanup.Deleted);
        Assert.False(File.Exists(repo.GetPayloadPath(old.Identity.VersionId))); Assert.Equal(42, payload.Content.ReadByte());
    }
    [Theory]
    [InlineData("scratch")]
    [InlineData("work")]
    [InlineData("materialized")]
    [InlineData("output")]
    public void MaintenanceBudgetsLeaveAuthorityUntouched(string budget)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter(); _ = Add(repo, writer);
        var snapshot = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        var limits = budget switch { "scratch" => new MaintenanceLimits(MaximumScratchBytes: 1), "work" => new(MaximumScratchBytesWritten: 1), "materialized" => new(MaximumMaterializedBytes: 1), _ => new(MaximumOutputBytes: 1) };
        Assert.Throws<IOException>(() => writer.RebuildIndexes(limits)); Assert.Equal(snapshot, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
    }
    [Fact]
    public void MixedPublicMutationsMatchIndependentSurvivorAndCurrentOracleThroughMaintenance()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        writer.HistoryFoldBufferEntries = 3;
        var paths = Enumerable.Range(0, 8).Select(i => new HistoryPath(FoundationTests.Root, "f" + i)).ToArray();
        var versions = paths.ToDictionary(p => p, _ => new List<StoredVersion>()); var current = new Dictionary<HistoryPath, StoredVersion>(); var random = new Random(151);
        for (var step = 0; step < 60; step++)
        {
            var path = paths[random.Next(paths.Length)];
            switch (step % 5)
            {
                case 0: case 1:
                    var added = Add(repo, writer, path.CanonicalPath); versions[path].Add(added); current[path] = added; break;
                case 2:
                    using (var reader = repo.OpenReader()) writer.Apply(new(currentRemovals: [path]), reader.Authority);
                    current.Remove(path); break;
                case 3:
                    var removed = versions.Values.SelectMany(v => v).Where(v => !current.TryGetValue(v.Identity.Path, out var selected) || selected.Identity != v.Identity).Take(3).ToArray();
                    using (var reader = repo.OpenReader()) writer.RemoveVersions(removed.Select(v => v.Identity), reader.Authority, cleanupPayloads: step % 2 == 0);
                    foreach (var version in removed) versions[version.Identity.Path].Remove(version);
                    break;
                case 4:
                    if (versions[path].LastOrDefault() is { } newest)
                    { using var reader = repo.OpenReader(); writer.Apply(new(currentUpserts: [newest.Identity]), reader.Authority); current[path] = newest; }
                    break;
            }
            if (step % 7 == 0) writer.RebuildIndexes();
            if (step % 11 == 0) { writer.CompactMetadata(); writer.CleanupRetiredMetadata(); }
            using var fresh = repo.OpenReader();
            foreach (var logical in paths)
            {
                Assert.Equal(versions[logical], fresh.EnumerateVersions(logical));
                Assert.Equal(current.GetValueOrDefault(logical), fresh.GetCurrent(logical));
            }
        }
        writer.CleanupRetiredPayloads(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes();
        Assert.Equal(0, Tombstones(repo));
    }
}
