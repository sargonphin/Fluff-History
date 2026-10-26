using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class PreparedPublicationTests
{
    [Fact]
    public void PreparationUsesLocalMetadataAndStagesOnlyNonAuthoritativeDestinationData()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var local = temp.Child("local");
        var before = MetadataBytes(repo); var state = File.ReadAllBytes(Path.Combine(temp.Child(), "state.json"));
        using var old = repo.OpenReader(); using var writer = repo.OpenWriter(local);
        var version = Draft(writer);
        using var prepared = writer.PrepareMutation(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), old.Authority);
        Assert.True(prepared.Changed); Assert.Equal(old.Authority, prepared.PredecessorAuthority);
        var workspace = Assert.Single(Directory.GetDirectories(local));
        Assert.Equal(workspace, prepared.Workspace.CreatedDirectory);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(workspace, "metadata"), "*.seg"));
        Assert.False(File.Exists(Path.Combine(workspace, "state.json")));
        Assert.False(File.Exists(Path.Combine(workspace, "repository.json")));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(temp.Child(), "state.json")));
        Assert.Equal(before, MetadataBytes(repo));
        Assert.Single(Directory.GetFiles(temp.Child(), "state.*.tmp"));
        Assert.Equal(prepared.Intended, AuthorityFiles.ReadPrepared(temp.Child(), repo.Identity, prepared.CandidateName, prepared.ControlName));
        Assert.Throws<InvalidDataException>(() => AuthorityFiles.Read(temp.Child(), repo.Identity, prepared.CandidateName));
        using (var fresh = repo.OpenReader()) Assert.Empty(fresh.EnumerateCurrent());
        Install(writer, version); var result = prepared.Commit();
        Assert.Equal(prepared.IntendedAuthority, result.Authority); Assert.True(result.Changed);
        Assert.Empty(old.EnumerateCurrent());
        using (var fresh = repo.OpenReader()) Assert.Equal(version.VersionId, fresh.GetCurrent(version.Path)!.Identity.VersionId);
        Assert.False(Directory.Exists(workspace)); Assert.Empty(Directory.GetFiles(temp.Child(), "state.*.tmp"));
        Assert.Empty(Directory.GetFiles(temp.Child(), "prepare.*.root"));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Child(), "metadata"), "*.tmp"));
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Child(), "metadata"), "*.seg"));
    }

    [Fact]
    public void LocalWorkingCopyCanBeDeletedBeforeCommitWithoutChangingHistory()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        using var writer = repo.OpenWriter(temp.Child("working"));
        using var prepared = writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
        Directory.Delete(prepared.Workspace.CreatedDirectory!, true);
        using (var fresh = repo.OpenReader()) Assert.Equal(reader.Authority, fresh.Authority);
        Assert.True(prepared.Commit().Changed); // Destination staging is self-contained; local bytes are derived.
        using var confirmed = repo.OpenReader(); Assert.Equal(prepared.IntendedAuthority, confirmed.Authority);
    }

    [Fact]
    public void WorkspacesAreUniqueAndDefaultToThePlatformUserTemporaryArea()
    {
        using var temp = new TemporaryDirectory(); var a = Repository.Create(temp.Child("a")); var b = Repository.Create(temp.Child("b"));
        using var ar = a.OpenReader(); using var br = b.OpenReader(); var local = temp.Child("local");
        using var aw = a.OpenWriter(local); using var bw = b.OpenWriter(local);
        using var ap = aw.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), ar.Authority);
        using var bp = bw.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), br.Authority);
        Assert.NotEqual(ap.Workspace.CreatedDirectory, bp.Workspace.CreatedDirectory);
        Assert.Equal(2, Directory.GetDirectories(local).Length);
        using var comparison = a.OpenComparisonReader(); _ = comparison.Read(comparison.Captured.Address);
        var working = comparison.Workspace!.CreatedDirectory!;
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(working));
        File.WriteAllText(Path.Combine(working, "write-probe"), "user writable");
        Assert.Throws<ArgumentException>(() => a.OpenComparisonReader(a.DirectoryPath));
    }

    [Fact]
    public void EmptyMutationDoesNotCreateAWorkspaceOrPublicationArtifacts()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var before = TemporaryDirectory.Snapshot(temp.Child());
        var local = temp.Child("unused-local"); using var reader = repo.OpenReader(); using var writer = repo.OpenWriter(local);
        using var prepared = writer.PrepareMutation(new(), reader.Authority);
        Assert.False(prepared.Changed); Assert.Null(prepared.Workspace.CreatedDirectory);
        Assert.False(prepared.Commit().Changed); Assert.Null(writer.LastWriteStatistics);
        Assert.False(Directory.Exists(local)); writer.Dispose();
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingOrWrongLengthPayloadPreventsMetadataInstallationAndCanReconcileWithoutFinalRoot(int length)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); AuthorityIdentity before; Guid transaction;
        using (var reader = repo.OpenReader()) before = reader.Authority;
        var bytes = MetadataBytes(repo);
        using (var writer = repo.OpenWriter(temp.Child("local")))
        {
            var version = Draft(writer);
            using var prepared = writer.PrepareMutation(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), before);
            transaction = prepared.IntendedAuthority.TransactionId;
            if (length >= 0) Install(writer, version, length);
            if (length < 0) Assert.ThrowsAny<IOException>(() => prepared.Commit());
            else Assert.Throws<InvalidDataException>(() => prepared.Commit());
            Assert.Equal(bytes, MetadataBytes(repo));
            Assert.Throws<InvalidOperationException>(() => writer.Apply(new(), before));
        }
        using (var writer = repo.OpenWriter()) Assert.Equal(PublicationOutcome.Unpublished, writer.Reconcile(transaction));
        using var fresh = repo.OpenReader(); Assert.Equal(before, fresh.Authority); Assert.Empty(fresh.EnumerateCurrent());
    }

    [Fact]
    public void PayloadIsCheckedAgainImmediatelyBeforeAuthorityReplacement()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        using var writer = repo.OpenWriter(temp.Child("local")); var version = Draft(writer); Install(writer, version);
        using var prepared = writer.PrepareMutation(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
        writer.Observe = stage => { if (stage == PublicationStage.BeforeReplace) File.Delete(writer.GetPreparedPayloadPath(version)); };
        Assert.Throws<FileNotFoundException>(() => prepared.Commit());
        using var fresh = repo.OpenReader(); Assert.Equal(reader.Authority, fresh.Authority); Assert.Empty(fresh.EnumerateCurrent());
        Assert.True(new FileInfo(SegmentReader.SegmentPath(temp.Child(), reader.Captured.Address.SegmentId)).Length > reader.Captured.Address.End);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(4, true)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public void FailureAndCancellationAtEveryPrePublicationPhasePreserveTheOldAuthority(int boundary, bool cancel)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        Authority before; using (var reader = repo.OpenReader()) before = reader.Captured;
        var beforePhysicalLength = new FileInfo(SegmentReader.SegmentPath(temp.Child(), before.Address.SegmentId)).Length;
        var originalBytes = MetadataBytes(repo); var local = temp.Child("working"); using var cts = new CancellationTokenSource();
        using (var writer = repo.OpenWriter(local))
        {
            writer.Observe = stage =>
            {
                if ((int)stage != boundary) return;
                if (cancel) cts.Cancel(); else throw new IOException("Injected phase failure");
            };
            var version = FoundationTests.Prepare(writer);
            if (cancel) Assert.Throws<OperationCanceledException>(() => writer.Apply(new([version]), before.Identity, cts.Token));
            else Assert.Throws<IOException>(() => writer.Apply(new([version]), before.Identity));
            Assert.Throws<InvalidOperationException>(() => writer.Apply(new(), before.Identity));
        }
        using (var reader = repo.OpenReader())
        {
            Assert.Equal(before.Identity, reader.Authority); Assert.Single(reader.EnumerateCurrent());
            Assert.Single(reader.EnumerateVersions(new(FoundationTests.Root, "foo.txt")));
        }
        if (boundary is 0 or 2)
            Assert.True(new FileInfo(SegmentReader.SegmentPath(temp.Child(), before.Address.SegmentId)).Length > beforePhysicalLength);
        else Assert.Equal(originalBytes, MetadataBytes(repo));
        if (Directory.Exists(local)) Assert.Empty(Directory.GetDirectories(local));
        using var recovery = repo.OpenWriter();
        foreach (var transaction in recovery.EnumeratePendingPublications()) Assert.Equal(PublicationOutcome.Unpublished, recovery.Reconcile(transaction));
    }

    [Fact]
    public void ChangedDestinationAppendExtentCannotInvalidatePreparedAddressesSilently()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        Guid transaction;
        using (var writer = repo.OpenWriter(temp.Child("local")))
        {
            using var prepared = writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
            transaction = prepared.IntendedAuthority.TransactionId;
            using (var stream = new FileStream(SegmentReader.SegmentPath(temp.Child(), reader.Captured.Address.SegmentId), FileMode.Append)) stream.Write([42]);
            Assert.Throws<InvalidDataException>(() => prepared.Commit());
            using var fresh = repo.OpenReader(); Assert.Equal(reader.Authority, fresh.Authority);
        }
        using (var recovery = repo.OpenWriter())
        {
            Assert.Equal(PublicationOutcome.Unpublished, recovery.Reconcile(transaction));
            Assert.True(FoundationTests.Add(repo, recovery).Changed); // Rotate past the abandoned tail, never overwrite it.
        }
        using var confirmed = repo.OpenReader(); Assert.Single(confirmed.EnumerateCurrent());
    }

    [Fact]
    public void DiscardingAnUncommittedPreparationCleansOwnedTemporaryArtifacts()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        using var writer = repo.OpenWriter(temp.Child("local"));
        var prepared = writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
        var workspace = prepared.Workspace.CreatedDirectory!; prepared.Dispose();
        using var fresh = repo.OpenReader(); Assert.Equal(reader.Authority, fresh.Authority);
        Assert.Empty(Directory.GetFiles(temp.Child(), "state.*.tmp")); Assert.Empty(Directory.GetFiles(temp.Child(), "prepare.*.root"));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Child(), "metadata"), "*.tmp")); Assert.False(Directory.Exists(workspace));
        Assert.Throws<InvalidOperationException>(() => writer.Apply(new(), reader.Authority));
    }

    [Fact]
    public void TemporaryNameCollisionDoesNotDeleteAnUnownedDestinationFile()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        var local = temp.Child("local"); using var writer = repo.OpenWriter(local); string? collision = null;
        writer.Observe = stage =>
        {
            if (stage != PublicationStage.LocalPrepared) return;
            var image = Assert.Single(Directory.GetFiles(Path.Combine(Assert.Single(Directory.GetDirectories(local)), "metadata"), "*.seg"));
            using var stream = File.OpenRead(image); var bytes = new byte[Format.SegmentHeaderBytes]; stream.ReadExactly(bytes);
            var header = MetadataCodec.DecodeSegment(bytes); var root = writer.PendingPublication!.Intended.Root;
            collision = Path.Combine(temp.Child(), "metadata", $"prepare.{root.TransactionId:N}.{header.SegmentId:N}.tmp");
            File.WriteAllText(collision, "unowned file");
        };
        Assert.Throws<IOException>(() => writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority));
        Assert.Equal("unowned file", File.ReadAllText(collision!));
        using var fresh = repo.OpenReader(); Assert.Equal(reader.Authority, fresh.Authority);
    }

    [Fact]
    public void TamperedStagingCannotBeInstalled()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        using var writer = repo.OpenWriter(); using var prepared = writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
        var transfer = Assert.Single(prepared.Transfers); File.AppendAllText(transfer.TemporaryPath, "tampered");
        Assert.Throws<InvalidDataException>(() => prepared.Commit());
        using var fresh = repo.OpenReader(); Assert.Equal(reader.Authority, fresh.Authority);
    }

    [Fact]
    public void CorruptPreparedControlIsPreservedForReviewRatherThanAssumedUnpublished()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); Guid transaction;
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader())
        {
            using var prepared = writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
            transaction = prepared.IntendedAuthority.TransactionId;
            writer.Observe = stage => { if (stage == PublicationStage.BeforeMetadataInstall) throw new IOException("stop"); };
            Assert.Throws<IOException>(() => prepared.Commit());
        }
        var control = Path.Combine(temp.Child(), $"prepare.{transaction:N}.root"); File.WriteAllBytes(control, [0]);
        using var recovery = repo.OpenWriter(); Assert.Throws<InvalidDataException>(() => recovery.Reconcile(transaction));
        Assert.Single(recovery.EnumeratePendingPublications()); Assert.True(File.Exists(control));
    }

    [Fact]
    public void ComparisonMaterializesEachDestinationSegmentOnlyOnceAcrossLocalHandleEvictions()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter())
        {
            writer.RotationBytes = 32768;
            for (var i = 0; i < 16; i++) FoundationTests.Add(repo, writer, $"f{i:D3}");
        }
        PageEntry[] versions;
        using (var reader = repo.OpenReader()) versions = reader.VersionIndex.Range(reader.Captured.Root.Versions).ToArray();
        var uniqueSegments = versions.Select(e => e.Value.SegmentId).Distinct().Count(); Assert.True(uniqueSegments > 8);
        var before = MetadataBytes(repo); var local = temp.Child("comparison"); string path;
        using (var reader = repo.OpenComparisonReader(local))
        {
            for (var pass = 0; pass < 3; pass++) foreach (var entry in versions) _ = reader.Version(entry.Value, versionKey: entry.Key);
            Assert.Equal(uniqueSegments, reader.Workspace!.DestinationSegmentFetches);
            Assert.True(reader.Segments.Statistics.SegmentOpens > reader.Workspace.DestinationSegmentFetches);
            path = reader.Workspace.CreatedDirectory!; Assert.False(File.Exists(Path.Combine(path, "state.json")));
        }
        Assert.False(Directory.Exists(path)); Assert.Equal(before, MetadataBytes(repo));
    }

    [Fact]
    public async Task ProcessDeathAfterCandidateStagingIsUnpublishedAndNeedsNoLocalWorkingCopyForReconciliation()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        await using (var child = WriterProcess.Start("prepared", temp.Child())) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        TemporaryDirectory.DeleteWithSharingViolationRetry(() => Directory.Delete(temp.Child("process-workspace"), true), OperatingSystem.IsWindows());
        using (var reader = Repository.Open(temp.Child()).OpenReader()) Assert.Equal(0, reader.Authority.Generation);
        using var writer = repo.OpenWriter(); var transaction = Assert.Single(writer.EnumeratePendingPublications());
        Assert.Equal(PublicationOutcome.Unpublished, writer.Reconcile(transaction));
    }

    private static PreparedVersion Draft(RepositoryWriter writer) => writer.PrepareVersion(
        new(FoundationTests.Root, "foo.txt", 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
        FoundationTests.Time, new("D24EC4F1A98C6E5B"));
    private static void Install(RepositoryWriter writer, PreparedVersion version, int length = 1)
    {
        var path = writer.GetPreparedPayloadPath(version); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[length]);
    }
    private static Dictionary<string, string> MetadataBytes(Repository repository) => Directory.GetFiles(Path.Combine(repository.DirectoryPath, "metadata"), "*.seg")
        .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToBase64String(File.ReadAllBytes(path)));
}
