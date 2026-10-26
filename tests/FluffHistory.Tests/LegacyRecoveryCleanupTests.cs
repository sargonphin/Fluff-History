using FluffHistory.Application;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

internal static class LegacyRecoveryFixture
{
    internal sealed class Cleanup : ILegacyRecoveryCredentialCleanup, IDisposable
    {
        internal HashSet<RecoveryCredentialCoordinate> Keys { get; } = [];
        internal bool Fail;
        public bool Exists(RecoveryCredentialCoordinate coordinate) => Keys.Contains(coordinate);
        public void Remove(RecoveryCredentialCoordinate coordinate)
        { if (Fail) throw new IOException("Synthetic exact legacy cleanup failure."); Keys.Remove(coordinate); }
        public void Dispose() => Keys.Clear();
    }
    internal static void Seed(Repository repository, Cleanup cleanup)
    {
        var coordinate = RecoveryCredentialCoordinate.ForRepository(repository.Identity.RepositoryId);
        cleanup.Keys.Add(coordinate);
        // An opaque bounded legacy blob is enough: migration must never decrypt it.
        File.WriteAllBytes(Path.Combine(repository.DirectoryPath, ExperimentalRecoverySidecar.FileName),
            new ExperimentalRecoverySidecar("Fluff-History", 1, repository.Identity.RepositoryId,
                coordinate.Resource, coordinate.Name, Convert.ToBase64String(new byte[128])).Bytes());
    }
}

public sealed class LegacyRecoveryCleanupTests
{
    private sealed class Adapter(OneDriveRecoveryServiceTests.Store store, LegacyRecoveryFixture.Cleanup cleanup) : ILegacyRecoveryCleanup
    {
        internal bool CalledAfterVerified;
        public Task RemoveAsync(string path, Guid repositoryId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var reference = Repository.InspectOneDriveRecovery(path)!;
            using var secret = OneDriveRecoverySecret.ParseCloudObject(store.Items[reference.ItemId].Bytes, reference);
            using (var verified = Repository.OpenWithOneDriveRecovery(path, secret))
            { using var reader = verified.OpenReader(); _ = reader.Authority; }
            CalledAfterVerified = true;
            using var local = Repository.OpenWithPassword(path, EncryptionTests.Password);
            Assert.Equal(repositoryId, local.Identity.RepositoryId); local.RemoveLegacyMicrosoftRecovery(cleanup, token);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NewCloudRecoveryIsVerifiedBeforeExactLegacyRetirementAndFailureDoesNotRollItBack(bool failure)
    {
        using var temp = new TemporaryDirectory(); using var cleanup = new LegacyRecoveryFixture.Cleanup { Fail = failure };
        using var store = new OneDriveRecoveryServiceTests.Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        LegacyRecoveryFixture.Seed(repository, cleanup);
        var unrelated = RecoveryCredentialCoordinate.ForRepository(Guid.NewGuid()); cleanup.Keys.Add(unrelated);
        var adapter = new Adapter(store, cleanup);
        var service = new OneDriveRecoveryService(new(), store, adapter)
            { OpenLocal = path => Repository.OpenWithPassword(path, EncryptionTests.Password) };
        Assert.Equal(OneDriveRecoveryState.LegacyUpgradeRequired, await service.InspectAsync(temp.Child()));
        Assert.Equal(0, store.SignIns);
        var result = await service.EnableAsync(temp.Child());
        Assert.Equal(OneDriveRecoveryOutcome.Verified, result.Outcome); Assert.True(adapter.CalledAfterVerified);
        Assert.Equal(failure ? OneDriveRecoveryCleanup.Legacy : OneDriveRecoveryCleanup.None, result.Cleanup);
        Assert.False(File.Exists(Path.Combine(temp.Child(), ExperimentalRecoverySidecar.FileName)));
        Assert.Contains(unrelated, cleanup.Keys);
        Assert.Equal(failure ? 2 : 1, cleanup.Keys.Count);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.VerifyAsync(temp.Child())).Outcome);
        if (failure)
        {
            cleanup.Fail = false;
            Assert.Equal(OneDriveRecoveryCleanup.None, (await service.EnableAsync(temp.Child())).Cleanup);
            Assert.Single(cleanup.Keys); Assert.Equal(1, store.Uploads);
        }
    }

    [Fact]
    public async Task LegacyStateIsNeverUsedAsARecoveryFallback()
    {
        using var temp = new TemporaryDirectory(); using var cleanup = new LegacyRecoveryFixture.Cleanup();
        using var store = new OneDriveRecoveryServiceTests.Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        LegacyRecoveryFixture.Seed(repository, cleanup);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        var service = new OneDriveRecoveryService(new(), store);
        var result = await service.VerifyAsync(temp.Child());
        Assert.Equal(OneDriveRecoveryOutcome.Failed, result.Outcome); Assert.Equal(CloudRecoveryFailure.ObjectNotFound, result.Failure);
        Assert.Equal(0, store.SignIns); Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child())); Assert.Single(cleanup.Keys);
    }
}
