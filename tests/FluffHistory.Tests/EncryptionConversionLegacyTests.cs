using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class EncryptionConversionLegacyTests
{
    [LegacyStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task CommittedTargetAdvanceByReleasedWriterIsPreservedOnMarkerRecovery(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) EncryptionTests.Add(source);
        // The real child dies after exposing the fully verified target header but before retiring Ready.
        using var legacy = new LegacyStorage();
        await RunKilledCommit();
        using (var released = legacy.Open(temp.Child(), encrypted))
        { legacy.Add(released, "after-commit.txt"); legacy.CompactAndClean(released); }
        var result = Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.True(result.PostCommitAuthorityAdvanced); Assert.Equal(1, result.Versions);
        using var reopened = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
        using var reader = reopened.OpenReader(); Assert.Equal(2, reader.EnumerateCurrent().Count());
        Assert.NotNull(reader.GetCurrent(new(FoundationTests.Root, "after-commit.txt")));

        async Task RunKilledCommit()
        {
            await using var child = WriterProcess.Start($"m252-convert-{(encrypted ? "encrypt" : "decrypt")}-AfterBootstrapPivot", temp.Child());
            await child.WaitUntilReadyAsync(); await child.KillAsync();
        }
    }

    [LegacyStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public void SourceAdvanceBeforeFenceRefreshesStartingCaptureWithoutDiscardingHistory(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) EncryptionTests.Add(source);
        var options = EncryptionConversionTests.Options(temp.Root) with
        { Observed = point => { if (point == EncryptionConversionStage.MarkerInstalled) throw new IOException("Before fence."); } };
        Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, options));
        using var legacy = new LegacyStorage();
        using (var released = legacy.Open(temp.Child(), !encrypted)) legacy.Add(released, "before-fence.txt");
        var result = Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.Equal(2, result.Versions);
        using var reopened = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
        using var reader = reopened.OpenReader(); Assert.Equal(2, reader.EnumerateCurrent().Count());
    }
    [LegacyStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public void ReleasedFreshAndRetainedClientsCannotWriteMixedPausedRepository(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true))
            EncryptionTests.Add(source);
        using var legacy = new LegacyStorage(); using var retained = legacy.Open(temp.Child(), !encrypted);
        var options = EncryptionConversionTests.Options(temp.Root) with
        { Observed = point => { if (point == EncryptionConversionStage.BeforeMetadata) throw new IOException("Controlled maintenance pause."); } };
        Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, options));
        var failure = Record.Exception(() => { using var fresh = legacy.Open(temp.Child(), !encrypted); using var writer = legacy.Writer(fresh); });
        Assert.NotNull(failure);
        Assert.NotNull(Record.Exception(() => { using var writer = legacy.Writer(retained); }));
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        using var complete = legacy.Open(temp.Child(), encrypted); using var usable = legacy.Writer(complete);
    }
}
