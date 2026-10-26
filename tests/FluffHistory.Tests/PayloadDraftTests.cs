using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class PayloadDraftTests
{
    [Fact]
    public void DraftIsWriteOnlyFlushedAndInstalledAfterPreparationWithoutOverwrite()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter())
        {
            using var old = repo.OpenReader(); using var draft = writer.CreatePayloadDraft();
            Assert.False(draft.Content.CanRead); Assert.True(draft.Content.CanWrite);
            Assert.Throws<NotSupportedException>(() => draft.Content.ReadByte());
            draft.Content.Write([42]); draft.CompleteCopy(1);
            Assert.Throws<InvalidOperationException>(() => draft.Content);
            var version = draft.PrepareVersion(new(FoundationTests.Root, "a", 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive), FoundationTests.Time, new("D24EC4F1A98C6E5B"));
            Assert.Equal(draft.VersionId, version.VersionId);
            using var prepared = writer.PrepareMutation(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), old.Authority);
            Assert.False(File.Exists(repo.GetPayloadPath(version.VersionId)));
            draft.Install(); Assert.True(prepared.Commit().Changed);
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(repo.GetPayloadPath(version.VersionId)));
        }
        Assert.False(Directory.Exists(Path.Combine(repo.DirectoryPath, "staging")));
    }

    [Fact]
    public void StagingCollisionNeverDeletesForeignFileAndAbortsOwner()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var id = Guid.NewGuid(); string foreign;
        using (var writer = repo.OpenWriter())
        {
            using var first = writer.CreatePayloadDraft();
            foreign = Path.Combine(Path.GetDirectoryName(first.StagingPath)!, $"{id:N}.part"); File.WriteAllText(foreign, "foreign");
            writer.PayloadIdentity = () => id;
            Assert.Throws<IOException>(() => writer.CreatePayloadDraft());
            Assert.Equal("foreign", File.ReadAllText(foreign));
            Assert.Throws<InvalidOperationException>(() => writer.PrepareVersion(new(FoundationTests.Root, "x", 1, FoundationTests.Time, FoundationTests.Time, 0), FoundationTests.Time, new("D24EC4F1A98C6E5B")));
        }
        Assert.Equal("foreign", File.ReadAllText(foreign));
    }

    [Fact]
    public void FinalCollisionIsNotOverwrittenAndCandidateCanReconcile()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); Guid transaction; string final;
        using (var writer = repo.OpenWriter())
        using (var old = repo.OpenReader())
        {
            using var draft = writer.CreatePayloadDraft(); draft.Content.Write([42]); draft.CompleteCopy(1);
            var version = draft.PrepareVersion(new(FoundationTests.Root, "a", 1, FoundationTests.Time, FoundationTests.Time, 0), FoundationTests.Time, new("D24EC4F1A98C6E5B"));
            using var prepared = writer.PrepareMutation(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), old.Authority);
            transaction = prepared.IntendedAuthority.TransactionId; final = repo.GetPayloadPath(version.VersionId);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllBytes(final, [99]);
            Assert.Throws<IOException>(() => draft.Install()); Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(final));
        }
        using (var fresh = repo.OpenReader()) Assert.Empty(fresh.EnumerateCurrent());
        using (var writer = repo.OpenWriter()) Assert.Equal(PublicationOutcome.Unpublished, writer.Reconcile(transaction));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(final));
    }

    [Fact]
    public void InstalledOrphanIsNotHistoryAndIsNeverRemovedByDraftDisposal()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); string final;
        using (var writer = repo.OpenWriter())
        using (var old = repo.OpenReader())
        {
            using var draft = writer.CreatePayloadDraft(); draft.Content.Write([42]); draft.CompleteCopy(1);
            var version = draft.PrepareVersion(new(FoundationTests.Root, "a", 1, FoundationTests.Time, FoundationTests.Time, 0), FoundationTests.Time, new("D24EC4F1A98C6E5B"));
            using var publication = writer.PrepareMutation(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), old.Authority);
            draft.Install(); final = repo.GetPayloadPath(version.VersionId);
            // Intentionally abandon before sole authority replacement.
        }
        Assert.True(File.Exists(final)); using var reader = repo.OpenReader(); Assert.Empty(reader.EnumerateCurrent());
    }

    [WindowsFact]
    public void NativePayloadInstallNeverFallsBackToCrossVolumeContentCopy()
    {
        using var temp = new TemporaryDirectory();
        if (Path.GetPathRoot(temp.Root)!.Equals(Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "payload-cross-volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = temp.Child("copy.part"); var destination = Path.Combine(directory, "final.bin"); File.WriteAllBytes(source, [42]);
        try
        {
            Assert.Throws<IOException>(() => PayloadFiles.MoveNew(source, destination));
            Assert.False(File.Exists(destination)); Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(directory, recursive: false); }
    }
}
