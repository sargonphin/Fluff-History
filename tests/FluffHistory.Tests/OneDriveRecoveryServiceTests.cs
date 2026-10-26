using System.Security.Cryptography;
using FluffHistory.Application;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class OneDriveRecoveryServiceTests
{
    internal sealed class Store : ICloudRecoveryStoreFactory, IDisposable
    {
        internal sealed record Entry(string Name, string ETag, byte[] Bytes);
        internal Dictionary<string, Entry> Items { get; } = [];
        internal int Uploads, Deletes, SignIns, Reads;
        internal CloudRecoveryFailure? Failure;
        internal bool RejectDelete;
        internal bool LoseUploadResponse;
        public bool IsConfigured => true;
        public Task<ICloudRecoveryStore> OpenAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); SignIns++;
            if (Failure is { } failure) throw new CloudRecoveryException(failure);
            return Task.FromResult<ICloudRecoveryStore>(new Lease(this));
        }
        private sealed class Lease(Store owner) : ICloudRecoveryStore
        {
            public Task InspectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            public Task<CloudRecoveryItem> CreateAsync(Guid repositoryId, Guid generationId, ReadOnlyMemory<byte> content, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); owner.Uploads++;
                var id = "synthetic-" + generationId.ToString("N");
                var name = $"recovery-{repositoryId:N}-{generationId:N}.json";
                owner.Items.Add(id, new(name, "\"original\"", content.ToArray()));
                if (owner.LoseUploadResponse) throw new CloudRecoveryException(CloudRecoveryFailure.NetworkUnavailable);
                return Task.FromResult(new CloudRecoveryItem(id, "\"original\"", name, []));
            }
            public Task<CloudRecoveryItem> ReadExactAsync(string itemId, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); owner.Reads++;
                if (!owner.Items.TryGetValue(itemId, out var entry)) throw new CloudRecoveryException(CloudRecoveryFailure.ObjectNotFound);
                return Task.FromResult(new CloudRecoveryItem(itemId, entry.ETag, entry.Name, entry.Bytes.ToArray()));
            }
            public Task<CloudRecoveryDeleteResult> DeleteExactAsync(string itemId, string eTag, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!owner.Items.TryGetValue(itemId, out var entry)) return Task.FromResult(CloudRecoveryDeleteResult.AlreadyAbsent);
                if (owner.RejectDelete || entry.ETag != eTag) throw new CloudRecoveryException(CloudRecoveryFailure.ObjectChanged);
                owner.Items.Remove(itemId); CryptographicOperations.ZeroMemory(entry.Bytes); owner.Deletes++;
                return Task.FromResult(CloudRecoveryDeleteResult.Deleted);
            }
            public void Dispose() { }
        }
        public void Dispose() { foreach (var entry in Items.Values) CryptographicOperations.ZeroMemory(entry.Bytes); Items.Clear(); }
    }
    private static OneDriveRecoveryService Service(Store store, Action<CloudRecoveryStage>? observed = null) => new(new(), store)
    { OpenLocal = path => Repository.OpenWithPassword(path, EncryptionTests.Password), Observed = observed };

    [Fact]
    public async Task PassiveInspectionDoesNotAuthenticateAndVerifyOrRecoveryDoesNotUpload()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var service = Service(store);
        Assert.Equal(OneDriveRecoveryState.Off, await service.InspectAsync(temp.Child())); Assert.Equal(0, store.SignIns);
        var result = await service.EnableAsync(temp.Child()); Assert.Equal(OneDriveRecoveryOutcome.Verified, result.Outcome);
        Assert.Equal(1, store.Uploads);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.EnableAsync(temp.Child())).Outcome);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.VerifyAsync(temp.Child())).Outcome);
        Assert.Equal(1, store.Uploads); Assert.Single(store.Items);
        var reference = Repository.InspectOneDriveRecovery(temp.Child())!;
        var item = store.Items[reference.ItemId]; store.Items[reference.ItemId] = item with { Name = "user-renamed.json", ETag = "\"renamed\"" };
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.VerifyAsync(temp.Child())).Outcome);
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await service.DisableAsync(temp.Child())).Outcome);
        Assert.Empty(store.Items); Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
    }

    [Theory]
    [InlineData("BeforeAuthentication", false)][InlineData("AfterAppRoot", false)]
    [InlineData("AfterUpload", false)][InlineData("AfterReadback", false)][InlineData("BeforePublication", false)]
    [InlineData("AfterPublication", true)][InlineData("BeforeFinalVerification", true)]
    public async Task EnableFailuresConvergeWithoutDanglingLocalAuthority(string name, bool published)
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        var fault = Enum.Parse<CloudRecoveryStage>(name);
        var service = Service(store, stage => { if (stage == fault) throw new IOException("Synthetic fault."); });
        var result = await service.EnableAsync(temp.Child());
        Assert.Equal(published ? OneDriveRecoveryOutcome.Uncertain : OneDriveRecoveryOutcome.Failed, result.Outcome);
        var after = TemporaryDirectory.Snapshot(temp.Child());
        if (published)
        {
            Assert.True(after.Remove(OneDriveRecoverySidecar.Name)); Assert.Single(store.Items);
            Assert.Equal(OneDriveRecoveryOutcome.Verified, (await Service(store).VerifyAsync(temp.Child())).Outcome);
        }
        else { Assert.Empty(store.Items); Assert.Null(Repository.InspectOneDriveRecovery(temp.Child())); }
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ModifiedCloudObjectIsNotDeletedAndExplicitLocalDisableRemainsPossible()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var service = Service(store); Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.EnableAsync(temp.Child())).Outcome);
        var reference = Repository.InspectOneDriveRecovery(temp.Child())!;
        var entry = store.Items[reference.ItemId];
        store.Items[reference.ItemId] = entry with { Bytes = "user-edited-non-recovery-content"u8.ToArray(), ETag = "\"edited\"" };
        Assert.Equal(OneDriveRecoveryOutcome.Failed, (await service.VerifyAsync(temp.Child())).Outcome);
        Assert.Equal(OneDriveRecoveryOutcome.Failed, (await service.DisableAsync(temp.Child())).Outcome);
        Assert.Equal(0, store.Deletes); Assert.NotNull(Repository.InspectOneDriveRecovery(temp.Child()));
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.EnableAsync(temp.Child(), repair: true)).Outcome);
        Assert.Equal(2, store.Items.Count); // Edited old content is preserved.
        var disabled = await service.DisableAsync(temp.Child(), localOnly: true);
        Assert.Equal(OneDriveRecoveryOutcome.DisabledLocally, disabled.Outcome); Assert.Equal(OneDriveRecoveryCleanup.Remote, disabled.Cleanup);
        Assert.Equal(2, store.Items.Count); Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
        CryptographicOperations.ZeroMemory(entry.Bytes);
    }

    [Fact]
    public async Task MissingWrongAccountOfflineAndCleanupConflictLeavePasswordIndependent()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var service = Service(store); await service.EnableAsync(temp.Child());
        using (var wrongAccount = new Store())
        {
            var wrong = await Service(wrongAccount).VerifyAsync(temp.Child());
            Assert.Equal(CloudRecoveryFailure.ObjectNotFound, wrong.Failure); Assert.Equal(0, wrongAccount.Uploads);
        }
        store.Failure = CloudRecoveryFailure.NetworkUnavailable;
        Assert.Equal(CloudRecoveryFailure.NetworkUnavailable, (await service.VerifyAsync(temp.Child())).Failure);
        Assert.Equal(0, store.Deletes); store.Failure = null; store.RejectDelete = true;
        Assert.Equal(OneDriveRecoveryOutcome.Failed, (await service.DisableAsync(temp.Child())).Outcome);
        Assert.Single(store.Items); Assert.NotNull(Repository.InspectOneDriveRecovery(temp.Child()));
        using var password = Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password);
    }

    [WindowsFact]
    public async Task AuthenticatedRecoveryEnrollsDPAPIThenProvesFreshLocalOpenWithoutNewUpload()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var service = Service(store); await service.EnableAsync(temp.Child());
        Assert.Throws<WindowsUserProtectorUnavailableException>(() => Repository.OpenWithWindowsUser(temp.Child()));
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.VerifyAsync(temp.Child(), enroll: true)).Outcome);
        using var windows = Repository.OpenWithWindowsUser(temp.Child());
        Assert.Equal(repository.Identity, windows.Identity); Assert.Equal(1, store.Uploads);
    }

    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task EncryptionOffAttemptsExactCloudCleanupUnderSharedGateAndToleratesOffline(bool offline)
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using (var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) EncryptionTests.Add(repository);
        var gate = new BackupOperationGate();
        var recovery = new OneDriveRecoveryService(gate, store);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await recovery.EnableAsync(temp.Child())).Outcome);
        if (offline) store.Failure = CloudRecoveryFailure.NetworkUnavailable;
        using var legacy = new LegacyRecoveryFixture.Cleanup();
        var conversion = new RepositoryEncryptionService(gate, legacy, temp.Root, recovery)
            { Options = EncryptionConversionTests.Options(temp.Root) };
        var result = await conversion.ChangeAsync(temp.Child(), false, null);
        Assert.Equal(EncryptionChangeOutcome.Completed, result.Outcome); Assert.Equal(offline, result.RemoteRecoveryCleanupRequired);
        Assert.Equal(offline ? 1 : 0, store.Items.Count); Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
        using var plain = Repository.Open(temp.Child()); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task ThreeRepositoriesHaveIndependentCloudObjectsAndExactDisable()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        var service = Service(store); var paths = Enumerable.Range(0, 3).Select(index => temp.Child(index.ToString())).ToArray();
        foreach (var path in paths)
        {
            using var repository = Repository.CreateEncrypted(path, EncryptionTests.Password);
            Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.EnableAsync(path)).Outcome);
        }
        Assert.Equal(3, store.Items.Count);
        Assert.Equal(3, paths.Select(path => Repository.InspectOneDriveRecovery(path)!.ItemId).Distinct().Count());
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await service.DisableAsync(paths[1])).Outcome);
        Assert.Equal(2, store.Items.Count);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.VerifyAsync(paths[0])).Outcome);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.VerifyAsync(paths[2])).Outcome);
    }

    [WindowsFact]
    public async Task FailedConversionDoesNotPrematurelyDeleteWorkingCloudRecovery()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store();
        using (var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) EncryptionTests.Add(repository);
        var gate = new BackupOperationGate(); var recovery = new OneDriveRecoveryService(gate, store);
        await recovery.EnableAsync(temp.Child());
        using var legacy = new LegacyRecoveryFixture.Cleanup();
        var conversion = new RepositoryEncryptionService(gate, legacy, temp.Root, recovery)
            { Options = EncryptionConversionTests.Options(temp.Root) with { CapacityProbe = _ => new(100_000_000, 0) } };
        Assert.Equal(EncryptionChangeOutcome.MoreSpaceRequired, (await conversion.ChangeAsync(temp.Child(), false, null)).Outcome);
        Assert.Equal(0, store.Deletes); Assert.Single(store.Items);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await recovery.VerifyAsync(temp.Child())).Outcome);
    }

    [Fact]
    public async Task LostUploadResponseReportsPossibleOrphanWithoutDeletingAnUnknownItem()
    {
        using var temp = new TemporaryDirectory(); using var store = new Store { LoseUploadResponse = true };
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        var result = await Service(store).EnableAsync(temp.Child());
        Assert.Equal(OneDriveRecoveryOutcome.Failed, result.Outcome); Assert.Equal(OneDriveRecoveryCleanup.Remote, result.Cleanup);
        Assert.Equal(Assert.Single(store.Items).Value.Name, result.CleanupFileName);
        Assert.Equal(0, store.Deletes); Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
        Assert.Null(Repository.InspectOneDriveRecovery(temp.Child()));
    }
}
