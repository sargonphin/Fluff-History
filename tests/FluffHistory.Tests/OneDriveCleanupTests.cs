using System.Security.Cryptography;
using FluffHistory.Application;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class OneDriveCleanupTests
{
    private static async Task<OneDriveRecoveryService> Prepare(string path, OneDriveRecoveryServiceTests.Store store)
    {
        using (var repository = Repository.CreateEncrypted(path, EncryptionTests.Password, true)) EncryptionTests.Add(repository);
        var service = new OneDriveRecoveryService(new(), store);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await service.EnableAsync(path)).Outcome);
        return service;
    }
    private static void Decrypt(string path, string workspace, Action<EncryptionConversionStage>? fault = null)
        => Repository.ConvertEncryption(path, false, EncryptionTests.Password,
            EncryptionConversionTests.Options(workspace) with { Observed = fault });

    [WindowsTheory]
    [InlineData("BeforeOneDriveCleanupRecord")]
    [InlineData("OneDriveCleanupRecorded")]
    [InlineData("BeforeOneDriveRetirement")]
    [InlineData("AfterOneDriveRetirement")]
    [InlineData("BeforeStatePivot")]
    [InlineData("AfterStatePivot")]
    [InlineData("ReadyRecorded")]
    [InlineData("BeforeBootstrapPivot")]
    [InlineData("AfterBootstrapPivot")]
    [InlineData("BeforeMarkerRemoval")]
    [InlineData("AfterMarkerRemoval")]
    public async Task FaultStagesRetainExactCoordinatesAndRestartCleanup(string faultName)
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store);
        var original = Repository.InspectOneDriveRecovery(path)!;
        var stage = Enum.Parse<EncryptionConversionStage>(faultName);
        var error = Record.Exception(() => Decrypt(path, temp.Root, reached =>
        { if (reached == stage) throw new IOException("Synthetic crash."); }));
        // An interrupted final-header replacement is reconciled by exact installed bytes.
        if (stage is EncryptionConversionStage.AfterBootstrapPivot or EncryptionConversionStage.AfterStatePivot) Assert.Null(error);
        else Assert.IsType<IOException>(error);
        Assert.Single(store.Items); Assert.Equal(0, store.Deletes);
        if (stage != EncryptionConversionStage.BeforeOneDriveCleanupRecord)
            Assert.True(Repository.HasPendingOneDriveCleanup(path));
        var signIns = store.SignIns;
        if (Repository.InspectEncryptionMigration(path) is not null)
        {
            Assert.Equal(OneDriveRecoveryOutcome.Failed, (await recovery.RetryCleanupAsync(path)).Outcome);
            Assert.Equal(signIns, store.SignIns); // No sign-in while local commit is unresolved.
            Decrypt(path, temp.Root);
        }
        using (var plain = Repository.Open(path))
        using (var reader = plain.OpenReader()) Assert.Single(reader.EnumerateCurrent());
        Assert.Null(Repository.InspectOneDriveRecovery(path));
        // Recreate the service: no in-memory reference survives the simulated process restart.
        var restarted = new OneDriveRecoveryService(new(), store);
        Assert.Equal(OneDriveRecoveryState.CleanupRequired, await restarted.InspectAsync(path));
        Assert.Equal(signIns, store.SignIns); // Passive status never authenticates.
        Assert.Equal(original, Assert.Single(ReadTickets(path)).Reference);
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await restarted.RetryCleanupAsync(path)).Outcome);
        Assert.Empty(store.Items); Assert.False(Repository.HasPendingOneDriveCleanup(path));
        Assert.Equal(1, store.Deletes);
        var reads = store.Reads; signIns = store.SignIns;
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await restarted.RetryCleanupAsync(path)).Outcome);
        Assert.Equal(reads, store.Reads); Assert.Equal(signIns, store.SignIns);
    }

    [WindowsTheory]
    [InlineData("BeforeOneDriveCleanupRecord")][InlineData("OneDriveCleanupRecorded")]
    [InlineData("BeforeOneDriveRetirement")][InlineData("AfterOneDriveRetirement")]
    [InlineData("BeforeStatePivot")][InlineData("BeforeBootstrapPivot")][InlineData("BeforeMarkerRemoval")]
    public async Task CancellationPausesWithoutLosingCleanup(string name)
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); await Prepare(path, store); using var cancelled = new CancellationTokenSource();
        var stage = Enum.Parse<EncryptionConversionStage>(name);
        Assert.Throws<OperationCanceledException>(() => Repository.ConvertEncryption(path, false, EncryptionTests.Password,
            EncryptionConversionTests.Options(temp.Root) with { Observed = reached =>
                { if (reached == stage) { cancelled.Cancel(); cancelled.Token.ThrowIfCancellationRequested(); } } }, token: cancelled.Token));
        Assert.Single(store.Items); Assert.Equal(0, store.Deletes);
        Decrypt(path, temp.Root);
        Assert.Single(ReadTickets(path));
        using var plain = Repository.Open(path); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [WindowsTheory]
    [InlineData("absent")][InlineData("changed-etag")][InlineData("precondition")]
    [InlineData("network")][InlineData("authentication")][InlineData("cancelled")]
    public async Task OnlyExactTerminalOutcomesAcknowledgeRecords(string condition)
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store); Decrypt(path, temp.Root);
        var item = Assert.Single(store.Items);
        switch (condition)
        {
            case "absent": store.Items.Remove(item.Key); CryptographicOperations.ZeroMemory(item.Value.Bytes); break;
            case "changed-etag": store.Items[item.Key] = item.Value with { ETag = "\"changed\"" }; break;
            case "precondition": store.RejectDelete = true; break;
            case "network": store.Failure = CloudRecoveryFailure.NetworkUnavailable; break;
            case "authentication": store.Failure = CloudRecoveryFailure.AuthenticationRequired; break;
            case "cancelled": store.Failure = CloudRecoveryFailure.SignInCancelled; break;
        }
        var before = File.ReadAllBytes(Path.Combine(path, "state.json"));
        var result = await recovery.RetryCleanupAsync(path);
        Assert.Equal(OneDriveRecoveryOutcome.Failed, result.Outcome);
        Assert.True(Repository.HasPendingOneDriveCleanup(path));
        Assert.Equal(0, store.Deletes); Assert.Equal(before, File.ReadAllBytes(Path.Combine(path, "state.json")));
        if (condition != "absent")
        {
            Assert.Single(store.Items); store.Failure = null; store.RejectDelete = false;
            store.Items[item.Key] = item.Value;
            Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(path)).Outcome);
        }
    }

    [WindowsFact]
    public async Task WrongAccountNotFoundRetainsCoordinatesForRetryWithOriginalAccount()
    {
        using var temp = new TemporaryDirectory(); using var original = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); await Prepare(path, original); Decrypt(path, temp.Root);
        var record = Assert.Single(OneDriveCleanupRecord.Paths(path)); var bytes = File.ReadAllBytes(record);
        var state = File.ReadAllBytes(Path.Combine(path, "state.json"));
        using var wrong = new OneDriveRecoveryServiceTests.Store();
        var result = await new OneDriveRecoveryService(new(), wrong).RetryCleanupAsync(path);
        Assert.Equal(OneDriveRecoveryOutcome.Failed, result.Outcome);
        Assert.Equal(CloudRecoveryFailure.ObjectNotFound, result.Failure);
        Assert.Equal(OneDriveRecoveryCleanup.Remote, result.Cleanup);
        Assert.Equal(1, wrong.Reads); Assert.Equal(0, wrong.DeleteRequests);
        Assert.Single(original.Items); Assert.Equal(bytes, File.ReadAllBytes(record));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(path, "state.json")));
        Assert.Equal(OneDriveRecoveryOutcome.Disabled,
            (await new OneDriveRecoveryService(new(), original).RetryCleanupAsync(path)).Outcome);
        Assert.False(Repository.HasPendingOneDriveCleanup(path)); Assert.Empty(original.Items);
    }

    [WindowsFact]
    public async Task VerifiedReadThenAlreadyAbsentConditionalDeleteAcknowledgesExactRecord()
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store); Decrypt(path, temp.Root);
        var item = Assert.Single(store.Items);
        store.BeforeDelete = () => { store.Items.Remove(item.Key); CryptographicOperations.ZeroMemory(item.Value.Bytes); };
        var state = File.ReadAllBytes(Path.Combine(path, "state.json"));
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(path)).Outcome);
        Assert.Equal(1, store.DeleteRequests); Assert.Equal(0, store.Deletes);
        Assert.False(Repository.HasPendingOneDriveCleanup(path));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(path, "state.json")));
    }

    [WindowsTheory]
    [InlineData(false)][InlineData(true)]
    public async Task InterruptionAfterDeletionRetainsRecordWhenLaterAbsenceCannotBeProven(bool cancelled)
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); await Prepare(path, store); Decrypt(path, temp.Root);
        var record = Assert.Single(OneDriveCleanupRecord.Paths(path)); var bytes = File.ReadAllBytes(record);
        var state = File.ReadAllBytes(Path.Combine(path, "state.json"));
        var interrupted = new OneDriveRecoveryService(new(), store) { Observed = stage =>
        {
            if (stage != CloudRecoveryStage.CleanupDeletedBeforeAcknowledgement) return;
            if (cancelled) throw new OperationCanceledException();
            throw new IOException("Synthetic interruption after remote deletion.");
        } };
        Assert.Equal(cancelled ? OneDriveRecoveryOutcome.Cancelled : OneDriveRecoveryOutcome.Failed,
            (await interrupted.RetryCleanupAsync(path)).Outcome);
        Assert.Empty(store.Items); Assert.Equal(1, store.Deletes);
        var restarted = new OneDriveRecoveryService(new(), store);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await restarted.RetryCleanupAsync(path);
            Assert.Equal(OneDriveRecoveryOutcome.Failed, result.Outcome);
            Assert.Equal(CloudRecoveryFailure.ObjectNotFound, result.Failure);
            Assert.Equal(bytes, File.ReadAllBytes(record));
        }
        Assert.Equal(1, store.DeleteRequests);
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(path, "state.json")));
        using var plain = Repository.Open(path); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [WindowsTheory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrCorruptRecordNeverGuessesOrBlocksHistory(bool corrupt)
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store); Decrypt(path, temp.Root);
        var record = Assert.Single(OneDriveCleanupRecord.Paths(path)); var bytes = File.ReadAllBytes(record);
        Assert.DoesNotContain(Assert.Single(store.Items).Key, System.Text.Encoding.UTF8.GetString(bytes));
        if (corrupt) { bytes[^1] ^= 1; File.WriteAllBytes(record, bytes); } else File.Delete(record);
        var signIns = store.SignIns;
        var result = await recovery.RetryCleanupAsync(path);
        Assert.Equal(corrupt ? OneDriveRecoveryOutcome.Failed : OneDriveRecoveryOutcome.Disabled, result.Outcome);
        Assert.Equal(signIns, store.SignIns); Assert.Single(store.Items); Assert.Equal(0, store.Deletes);
        using var plain = Repository.Open(path); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [WindowsFact]
    public async Task ReencryptionDoesNotAutomaticallyConsumeCleanupAndExplicitRetryPreservesNewRecovery()
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store); Decrypt(path, temp.Root);
        var original = Assert.Single(ReadTickets(path)); var signIns = store.SignIns;
        using var legacy = new LegacyRecoveryFixture.Cleanup();
        var service = new RepositoryEncryptionService(new(), legacy, temp.Root, recovery)
            { Options = EncryptionConversionTests.Options(temp.Root) };
        Assert.Equal(EncryptionChangeOutcome.Completed,
            (await service.ChangeAsync(path, true, EncryptionTests.Password.ToCharArray())).Outcome);
        Assert.Equal(signIns, store.SignIns); Assert.True(Repository.HasPendingOneDriveCleanup(path));
        Assert.Single(store.Items);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await recovery.EnableAsync(path)).Outcome);
        var active = Repository.InspectOneDriveRecovery(path)!;
        Assert.NotEqual(original.Reference.GenerationId, active.GenerationId);
        Assert.Equal(OneDriveRecoveryState.EnabledWithCleanup, await recovery.InspectAsync(path));
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(path)).Outcome);
        Assert.Equal(active.ItemId, Assert.Single(store.Items).Key);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await recovery.VerifyAsync(path)).Outcome);
        Assert.False(Repository.HasPendingOneDriveCleanup(path));
        using (var password = Repository.OpenWithPassword(path, EncryptionTests.Password))
        using (var reader = password.OpenReader()) Assert.Single(reader.EnumerateCurrent());
        using (var local = Repository.OpenWithWindowsUser(path))
        using (var reader = local.OpenReader()) Assert.Single(reader.EnumerateCurrent());
    }

    private static IReadOnlyList<OneDriveCleanupTicket> ReadTickets(string path)
    { using var repository = ApplicationRepository.Open(path); using var writer = repository.OpenWriter(); return writer.ReadPendingOneDriveCleanup(); }

    [WindowsFact]
    public async Task FullLedgerRejectsBeforeFencingAndExplicitCleanupPreservesActiveRecovery()
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store); Decrypt(path, temp.Root);
        var retired = Assert.Single(ReadTickets(path)).Record;
        for (var index = 1; index < OneDriveCleanupRecord.MaximumRecords; index++)
            (retired with { OperationId = Guid.NewGuid() }).Install(path);
        Repository.ConvertEncryption(path, true, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        await recovery.EnableAsync(path); var active = Repository.InspectOneDriveRecovery(path)!;
        Assert.Throws<InvalidDataException>(() => Decrypt(path, temp.Root));
        Assert.Null(Repository.InspectEncryptionMigration(path));
        using (var ordinary = Repository.OpenWithWindowsUser(path))
        using (var reader = ordinary.OpenReader()) Assert.Single(reader.EnumerateCurrent());
        var reads = store.Reads; var deletions = store.DeleteRequests;
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(path)).Outcome);
        Assert.Equal(reads + 1, store.Reads); Assert.Equal(deletions + 1, store.DeleteRequests);
        Assert.Equal(active.ItemId, Assert.Single(store.Items).Key);
        Assert.Equal(OneDriveRecoveryOutcome.Verified, (await recovery.VerifyAsync(path)).Outcome);
        Decrypt(path, temp.Root);
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(path)).Outcome);
        Assert.Empty(store.Items);
    }

    [WindowsFact]
    public async Task ARecordForActiveRecoveryIsRejectedBeforeAuthentication()
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store);
        var active = Repository.InspectOneDriveRecovery(path)!;
        using (var local = Repository.OpenWithWindowsUser(path))
            new OneDriveCleanupRecord("Fluff-History", 1, Guid.NewGuid(), active,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(path, "repository.json")))),
                Convert.ToHexString(SHA256.HashData(RepositoryBootstrap.Plain(local.Identity).Bytes()))).Install(path);
        var signIns = store.SignIns;
        Assert.Equal(OneDriveRecoveryOutcome.Failed, (await recovery.RetryCleanupAsync(path)).Outcome);
        Assert.Equal(signIns, store.SignIns); Assert.Single(store.Items); Assert.Equal(0, store.Deletes);
        Assert.True(Repository.HasPendingOneDriveCleanup(path));
    }

    [WindowsFact]
    public async Task RelocationWorksButAnotherRepositoryCannotAdoptCoordinates()
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store); Decrypt(path, temp.Root);
        var record = Assert.Single(OneDriveCleanupRecord.Paths(path));
        var other = temp.Child("other"); using (var unrelated = Repository.Create(other)) { }
        File.Copy(record, Path.Combine(other, Path.GetFileName(record)));
        var signIns = store.SignIns;
        Assert.Equal(OneDriveRecoveryOutcome.Failed, (await recovery.RetryCleanupAsync(other)).Outcome);
        Assert.Equal(signIns, store.SignIns); Assert.Single(store.Items);
        var moved = temp.Child("moved"); Directory.Move(path, moved);
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(moved)).Outcome);
        Assert.Empty(store.Items);
    }

    [WindowsFact]
    public async Task RecordLimitIsBoundedWithoutOverwritingOrDeletingOlderObligations()
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); await Prepare(path, store); Decrypt(path, temp.Root);
        var original = Assert.Single(ReadTickets(path)).Record;
        for (var index = 1; index < OneDriveCleanupRecord.MaximumRecords; index++)
            (original with { OperationId = Guid.NewGuid() }).Install(path);
        var before = OneDriveCleanupRecord.Paths(path).ToDictionary(value => Path.GetFileName(value)!, File.ReadAllBytes);
        Assert.Throws<InvalidDataException>(() => (original with { OperationId = Guid.NewGuid() }).Install(path));
        Assert.Equal(OneDriveCleanupRecord.MaximumRecords, OneDriveCleanupRecord.Paths(path).Count());
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(Path.Combine(path, entry.Key!)));
        Assert.Equal(0, store.Deletes);
        using var plain = Repository.Open(path); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [WindowsTheory]
    [InlineData("BeforeOneDriveRetirement")][InlineData("AfterOneDriveRetirement")]
    [InlineData("BeforeBootstrapPivot")][InlineData("AfterBootstrapPivot")][InlineData("AfterMarkerRemoval")]
    public async Task ProcessDeathRetainsTheDurableObligation(string point)
    {
        using var temp = new TemporaryDirectory(); using var store = new OneDriveRecoveryServiceTests.Store();
        var path = temp.Child(); var recovery = await Prepare(path, store);
        await using (var child = WriterProcess.Start($"convert-decrypt-{point}", path))
        { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        Assert.Single(store.Items); Assert.Equal(0, store.Deletes);
        Assert.True(Repository.HasPendingOneDriveCleanup(path));
        if (Repository.InspectEncryptionMigration(path) is not null) Decrypt(path, temp.Root);
        Assert.Equal(OneDriveRecoveryOutcome.Disabled, (await recovery.RetryCleanupAsync(path)).Outcome);
        Assert.Empty(store.Items); Assert.False(Repository.HasPendingOneDriveCleanup(path));
        using var plain = Repository.Open(path); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }
}
