using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class VersionRemovalRecoveryTests
{
    [Theory]
    [InlineData("remove-prepared", false)]
    [InlineData("remove-before", false)]
    [InlineData("remove-after", true)]
    [InlineData("remove-cleanup", true)]
    public async Task ProcessDeathPreservesExactVisibilityAndRestartCleanup(string mode, bool published)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old, second, current;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); second = VersionRemovalTests.Add(repo, writer); current = VersionRemovalTests.Add(repo, writer); }
        await using (var child = WriterProcess.Start(mode, repo.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        using (var reader = Repository.Open(repo.DirectoryPath).OpenReader())
        {
            Assert.Equal(published ? 1 : 3, reader.EnumerateVersions(current.Identity.Path).Count()); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
            Assert.Equal(published ? 2 : 0, VersionRemovalTests.Tombstones(repo));
        }
        if (mode != "remove-cleanup") Assert.True(File.Exists(repo.GetPayloadPath(old.Identity.VersionId)));
        if (!published) Assert.True(File.Exists(repo.GetPayloadPath(second.Identity.VersionId)));
        using var recovery = repo.OpenWriter();
        foreach (var tx in recovery.EnumeratePendingPublications().ToArray()) Assert.Equal(published ? PublicationOutcome.Published : PublicationOutcome.Unpublished, recovery.Reconcile(tx));
        var cleanup = recovery.CleanupRetiredPayloads().Result;
        if (published)
        {
            Assert.Equal(mode == "remove-cleanup" ? 1 : 2, cleanup.Deleted); Assert.Equal(mode == "remove-cleanup" ? 1 : 0, cleanup.AlreadyAbsent);
            recovery.CompactMetadata(); recovery.CleanupRetiredMetadata(); recovery.RebuildIndexes(); Assert.Equal(0, VersionRemovalTests.Tombstones(repo));
        }
        else { Assert.Equal(0, cleanup.Inspected); Assert.True(File.Exists(repo.GetPayloadPath(old.Identity.VersionId))); }
    }
    [Theory]
    [InlineData("compact-removal-prepared", false)]
    [InlineData("compact-removal-after", true)]
    public async Task CompactionInterruptionKeepsPayloadObligationInEitherCommittedBase(string mode, bool published)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter())
        {
            old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer);
            using var reader = repo.OpenReader(); writer.RemoveVersions([old.Identity], reader.Authority, cleanupPayloads: false);
        }
        await using (var child = WriterProcess.Start(mode, repo.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        using var recovery = repo.OpenWriter();
        foreach (var tx in recovery.EnumeratePendingPublications().ToArray()) Assert.Equal(published ? PublicationOutcome.Published : PublicationOutcome.Unpublished, recovery.Reconcile(tx));
        recovery.CleanupRetiredMetadata(); recovery.RebuildIndexes(); Assert.Equal(1, VersionRemovalTests.Tombstones(repo));
        Assert.Equal(1, recovery.CleanupRetiredPayloads().Result.Deleted); recovery.CompactMetadata(); Assert.Equal(0, VersionRemovalTests.Tombstones(repo));
    }
    [WindowsFact]
    public async Task CrossProcessPayloadHandleDefersCleanupUntilOwnerDies()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer); }
        await using var child = WriterProcess.Start("hold-payload", repo.DirectoryPath); await child.WaitUntilReadyAsync();
        using (var owner = repo.OpenWriter()) using (var reader = repo.OpenReader())
            Assert.Equal(1, owner.RemoveVersions([old.Identity], reader.Authority).Cleanup.Deferred);
        await child.KillAsync();
        // The generic helper observes writer.lock; this child holds a payload instead.
        // Process exit alone need not mean Windows sharing teardown has completed.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await WriterProcess.WaitForFileAccessAsync(() =>
        {
            using var released = new FileStream(repo.GetPayloadPath(old.Identity.VersionId), FileMode.Open, FileAccess.Read, FileShare.None);
        }, OperatingSystem.IsWindows(), timeout.Token);
        using var cleanup = repo.OpenWriter(); Assert.Equal(1, cleanup.CleanupRetiredPayloads().Result.Deleted);
    }
    [Theory]
    [InlineData("cancel-before", false)]
    [InlineData("cancel-after", true)]
    [InlineData("throw-before", false)]
    [InlineData("throw-after", true)]
    [InlineData("lost-after", true)]
    public void CancellationAndAmbiguousReplacementNeverAuthorizeUnconfirmedCleanup(string fault, bool published)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old; AuthorityIdentity prior;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer); using var reader = repo.OpenReader(); prior = reader.Authority; }
        using var cancellation = new CancellationTokenSource();
        using (var writer = repo.OpenWriter())
        {
            writer.Observe = stage =>
            {
                if (stage != (fault.EndsWith("before", StringComparison.Ordinal) ? PublicationStage.BeforeReplace : PublicationStage.AfterReplace)) return;
                if (fault.StartsWith("cancel", StringComparison.Ordinal)) cancellation.Cancel();
                else if (fault == "lost-after") writer.LoseHandleForTest();
                else throw new IOException("Injected authority acknowledgement error.");
            };
            if (fault == "cancel-before") Assert.ThrowsAny<OperationCanceledException>(() => writer.RemoveVersions([old.Identity], prior, cancellationToken: cancellation.Token));
            else if (fault == "throw-before") Assert.Throws<IOException>(() => writer.RemoveVersions([old.Identity], prior));
            else if (fault == "lost-after") Assert.Throws<PublicationUncertainException>(() => writer.RemoveVersions([old.Identity], prior));
            else
            {
                var result = writer.RemoveVersions([old.Identity], prior, cancellationToken: cancellation.Token); Assert.Equal(1, result.Removed);
                Assert.Equal(fault == "cancel-after", result.Cleanup.Cancelled);
            }
        }
        using (var reader = repo.OpenReader()) Assert.Equal(!published, reader.GetVersion(old.Identity) is not null);
        if (fault != "throw-after") Assert.True(File.Exists(repo.GetPayloadPath(old.Identity.VersionId)));
        Guid[] candidates;
        using (var recovery = repo.OpenWriter())
        {
            candidates = recovery.EnumeratePendingPublications().ToArray();
            if (candidates.Length != 0) Assert.Throws<IOException>(() => recovery.CleanupRetiredPayloads());
        }
        using var reconciled = repo.OpenWriter();
        foreach (var transaction in candidates) Assert.Equal(published ? PublicationOutcome.Published : PublicationOutcome.Unpublished, reconciled.Reconcile(transaction));
        if (published)
        {
            using var reader = repo.OpenReader(); Assert.Equal(PublicationOutcome.Published, reconciled.Reconcile(reader.Authority.TransactionId));
            Assert.Equal(fault == "throw-after" ? 0 : 1, reconciled.CleanupRetiredPayloads().Result.Deleted);
        }
        else Assert.Equal(0, reconciled.CleanupRetiredPayloads().Result.Inspected);
    }
    [Fact]
    public void CancellationDuringCleanupReportsCommittedProgressAndRemainingObligations()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var writer = repo.OpenWriter();
        var first = VersionRemovalTests.Add(repo, writer); var second = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer);
        using var cancellation = new CancellationTokenSource(); writer.PayloadCleanupObserve = (stage, _) => { if (stage == PayloadCleanupStage.Deleted) cancellation.Cancel(); };
        using var reader = repo.OpenReader(); var result = writer.RemoveVersions([first.Identity, second.Identity], reader.Authority, cancellationToken: cancellation.Token);
        Assert.Equal(2, result.Removed); Assert.Equal(1, result.Cleanup.Deleted); Assert.True(result.Cleanup.Cancelled);
        writer.PayloadCleanupObserve = null; var later = writer.CleanupRetiredPayloads().Result; Assert.Equal(1, later.Deleted); Assert.Equal(1, later.AlreadyAbsent);
    }
}
