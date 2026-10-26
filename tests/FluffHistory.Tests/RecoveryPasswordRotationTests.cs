using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class RecoveryPasswordRotationTests
{
    private const string NewPassword = "Synthetic replacement password \u00e9 with spaces ";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SameMasterOnlyBootstrapChangesAndLiveSessionCanContinue(bool withWindows)
    {
        if (withWindows && !OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, withWindows);
        EncryptionTests.Add(repository);
        var before = TemporaryDirectory.Snapshot(repository.DirectoryPath);
        var bootstrap = RepositoryBootstrap.Read(repository.DirectoryPath);
        repository.ChangeRecoveryPassword(NewPassword);
        var after = TemporaryDirectory.Snapshot(repository.DirectoryPath);
        Assert.NotEqual(before["repository.json"], after["repository.json"]);
        after["repository.json"] = before["repository.json"]; Assert.Equal(before, after);
        var changed = RepositoryBootstrap.Read(repository.DirectoryPath);
        Assert.Equal(bootstrap.Binding(), changed.Binding()); Assert.Equal(bootstrap.WindowsUserKey, changed.WindowsUserKey);
        Assert.NotEqual(bootstrap.Recovery!.Salt, changed.Recovery!.Salt);
        Assert.Equal(bootstrap, changed with { Recovery = bootstrap.Recovery });
        Assert.Throws<RecoveryPasswordAuthenticationException>(() => Repository.OpenWithPassword(repository.DirectoryPath, EncryptionTests.Password));
        using var recovered = Repository.OpenWithPassword(repository.DirectoryPath, NewPassword);
        using var reader = recovered.OpenReader(); Assert.Single(reader.EnumerateCurrent());
        using var liveReader = repository.OpenReader(); Assert.Equal(reader.Authority, liveReader.Authority);
        if (withWindows) { using var windows = Repository.OpenWithWindowsUser(repository.DirectoryPath); Assert.Equal(repository.Identity, windows.Identity); }
        repository.ChangeRecoveryPassword(NewPassword + "next"); // No stale stored password wrapper in the live owner.
    }

    [Theory]
    [InlineData("cancel-before")] [InlineData("create")] [InlineData("flush")] [InlineData("cancel" )]
    [InlineData("before")] [InlineData("after")] [InlineData("readback")] [InlineData("restore-prior")]
    [InlineData("unknown")]
    public void FailuresReconcileExactInstalledBootstrap(string fault)
    {
        using var temp = new TemporaryDirectory();
        using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        EncryptionTests.Add(repository);
        var before = TemporaryDirectory.Snapshot(repository.DirectoryPath);
        var prior = File.ReadAllBytes(Path.Combine(repository.DirectoryPath, "repository.json"));
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancel-before") cancellation.Cancel();
        repository.PasswordChangeObserved = stage =>
        {
            if (fault == "create" && stage == PasswordChangeStage.CandidateCreated ||
                fault == "flush" && stage == PasswordChangeStage.CandidateFlushed ||
                fault == "before" && stage == PasswordChangeStage.BeforeReplace ||
                fault == "after" && stage == PasswordChangeStage.AfterReplace ||
                fault == "readback" && stage == PasswordChangeStage.BeforeConfirmation) throw new IOException("Injected security operation fault.");
            if (fault == "cancel" && stage == PasswordChangeStage.BeforeReplace) cancellation.Cancel();
            if (fault == "restore-prior" && stage == PasswordChangeStage.AfterReplace) File.WriteAllBytes(Path.Combine(repository.DirectoryPath, "repository.json"), prior);
            if (fault == "unknown" && stage == PasswordChangeStage.AfterReplace) File.WriteAllText(Path.Combine(repository.DirectoryPath, "repository.json"), "corrupt synthetic data");
        };
        if (fault == "after") repository.ChangeRecoveryPassword(NewPassword, cancellation.Token); // Exception is not proof of non-installation.
        else if (fault is "readback" or "unknown") Assert.Throws<RecoveryPasswordChangeUncertainException>(() => repository.ChangeRecoveryPassword(NewPassword, cancellation.Token));
        else if (fault is "cancel-before" or "cancel") Assert.Throws<OperationCanceledException>(() => repository.ChangeRecoveryPassword(NewPassword, cancellation.Token));
        else Assert.Throws<IOException>(() => repository.ChangeRecoveryPassword(NewPassword, cancellation.Token));
        Assert.Empty(Directory.GetFiles(repository.DirectoryPath, "bootstrap-*.tmp"));
        if (fault is "after" or "readback")
        {
            using var fresh = Repository.OpenWithPassword(repository.DirectoryPath, NewPassword);
            using var reader = fresh.OpenReader(); Assert.Single(reader.EnumerateCurrent());
        }
        else if (fault != "unknown") Assert.Equal(before, TemporaryDirectory.Snapshot(repository.DirectoryPath));
    }

    [WindowsFact]
    public async Task RotationThroughWindowsAccessSurvivesFreshProcessAndDeniedReplacement()
    {
        using var temp = new TemporaryDirectory();
        using (var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true)) EncryptionTests.Add(repository);
        using (var repository = Repository.OpenWithWindowsUser(temp.Child()))
        {
            var before = TemporaryDirectory.Snapshot(temp.Child());
            using (var deny = new FileStream(Path.Combine(temp.Child(), "repository.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(() => repository.ChangeRecoveryPassword(NewPassword));
            Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
            repository.ChangeRecoveryPassword(NewPassword);
        }
        await using var child = WriterProcess.Start("m201-silent-open", temp.Child());
        await child.WaitUntilReadyAsync(); Assert.Equal(0, await child.ExitCodeAsync());
    }

    [Fact]
    public void InvalidPasswordsAndOwnershipRejectBeforeMutation()
    {
        using var temp = new TemporaryDirectory(); using var repository = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        Assert.Throws<ArgumentException>(() => repository.ChangeRecoveryPassword(""));
        Assert.Throws<ArgumentException>(() => repository.ChangeRecoveryPassword(new string('a', 1025)));
        using (var writer = repository.OpenWriter()) Assert.Throws<IOException>(() => repository.ChangeRecoveryPassword(NewPassword));
        using var locked = Repository.Discover(temp.Child())!;
        Assert.Throws<RepositoryLockedException>(() => locked.ChangeRecoveryPassword(NewPassword));
        using var plain = Repository.Create(temp.Child("plain"));
        Assert.Throws<InvalidOperationException>(() => plain.ChangeRecoveryPassword(NewPassword));
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
    }
}
