using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class OwnershipEnrollmentTests
{
    [WindowsTheory]
    [InlineData(null)] [InlineData("not-a-dpapi-blob")]
    public async Task PortableRecoveryReplacesUnavailableProtectorAndChildSilentlyReopens(string? oldSlot)
    {
        using var temp = new TemporaryDirectory(); var directory = temp.Child();
        using (var created = Repository.CreateEncrypted(directory, EncryptionTests.Password)) EncryptionTests.Add(created);
        var original = RepositoryBootstrap.Read(directory) with { WindowsUserKey = oldSlot };
        var header = Path.Combine(directory, "repository.json"); File.WriteAllBytes(header, original.Bytes());
        var before = TemporaryDirectory.Snapshot(directory);
        Assert.ThrowsAny<Exception>(() => Repository.OpenWithWindowsUser(directory));
        Assert.Throws<RecoveryPasswordAuthenticationException>(() => Repository.OpenWithPassword(directory, "wrong synthetic password"));
        Assert.Equal(before, TemporaryDirectory.Snapshot(directory));
        using (var unlocked = Repository.OpenWithPassword(directory, EncryptionTests.Password))
        {
            Assert.Equal(before, TemporaryDirectory.Snapshot(directory)); // Password open is read-only.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                unlocked.EnrollCurrentWindowsUser();
                var updated = RepositoryBootstrap.Read(directory);
                Assert.NotNull(updated.WindowsUserKey); Assert.Equal(original, updated with { WindowsUserKey = oldSlot });
                Assert.Equal(original.Binding(), updated.Binding());
                var after = TemporaryDirectory.Snapshot(directory);
                after["repository.json"] = before["repository.json"]; Assert.Equal(before, after);
            }
        }
        await using (var child = WriterProcess.Start("m201-silent-open", directory))
        { await child.WaitUntilReadyAsync(); Assert.Equal(0, await child.ExitCodeAsync()); }
        using var recovered = Repository.OpenWithPassword(directory, EncryptionTests.Password);
        using var reader = recovered.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [Fact]
    public void LockedPlainDisposedAndPortableOpenSemantics()
    {
        using var temp = new TemporaryDirectory(); using var plain = Repository.Create(temp.Child("plain"));
        Assert.Throws<InvalidOperationException>(() => plain.EnrollCurrentWindowsUser());
        using var encrypted = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        using var locked = Repository.Discover(encrypted.DirectoryPath)!;
        Assert.Throws<RepositoryLockedException>(() => locked.EnrollCurrentWindowsUser());
        var before = TemporaryDirectory.Snapshot(encrypted.DirectoryPath);
        using (var password = Repository.OpenWithPassword(encrypted.DirectoryPath, EncryptionTests.Password))
        {
            using var reader = password.OpenReader(); Assert.Empty(reader.EnumerateCurrent());
            if (!OperatingSystem.IsWindows()) Assert.Throws<PlatformNotSupportedException>(() => password.EnrollCurrentWindowsUser());
        }
        Assert.Equal(before, TemporaryDirectory.Snapshot(encrypted.DirectoryPath));
        encrypted.Dispose(); Assert.Throws<ObjectDisposedException>(() => encrypted.EnrollCurrentWindowsUser());
    }

    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public void DamagedAuthorityOrRootCannotEnrollAnAlreadyUnwrappedKey(bool root)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var authority = repo.ReadAuthority();
        var header = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "repository.json"));
        var path = root ? SegmentReader.SegmentPath(repo.DirectoryPath, authority.Address.SegmentId) : Path.Combine(repo.DirectoryPath, "state.json");
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => repo.EnrollCurrentWindowsUser());
        Assert.Equal(header, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "repository.json")));
    }

    [WindowsTheory]
    [InlineData("before")] [InlineData("cancel")] [InlineData("replace-denied")] [InlineData("after")]
    [InlineData("readback")]
    public void EnrollmentBoundaryFailuresAreExplicitAndLeavePasswordRecovery(string fault)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true);
        var before = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "repository.json"));
        using var cancel = new CancellationTokenSource(); FileStream? denyReplace = null;
        repo.EnrollmentObserved = stage =>
        {
            if (stage == EnrollmentStage.BeforeReplace)
            {
                if (fault == "before") throw new IOException("Synthetic pre-replacement failure.");
                if (fault == "cancel") cancel.Cancel();
                if (fault == "replace-denied") denyReplace = new(Path.Combine(repo.DirectoryPath, "repository.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            if (stage == EnrollmentStage.AfterReplace)
            {
                if (fault == "after") throw new IOException("Synthetic confirmation failure.");
                if (fault == "readback") File.WriteAllBytes(Path.Combine(repo.DirectoryPath, "repository.json"), before);
            }
        };
        try
        {
            if (fault is "before") Assert.Throws<IOException>(() => repo.EnrollCurrentWindowsUser(cancel.Token));
            else if (fault is "cancel") Assert.Throws<OperationCanceledException>(() => repo.EnrollCurrentWindowsUser(cancel.Token));
            else Assert.Throws<OwnershipEnrollmentUncertainException>(() => repo.EnrollCurrentWindowsUser(cancel.Token));
        }
        finally { denyReplace?.Dispose(); }
        if (fault is "before" or "cancel" or "replace-denied" or "readback")
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "repository.json")));
        Assert.Empty(Directory.GetFiles(repo.DirectoryPath, "bootstrap-*.tmp"));
        using var password = Repository.OpenWithPassword(repo.DirectoryPath, EncryptionTests.Password);
        using var windows = Repository.OpenWithWindowsUser(repo.DirectoryPath);
        Assert.Equal(password.Identity, windows.Identity);
    }

    [WindowsFact]
    public void ConcurrentEnrollmentAndHistoryWriterUseSameOwnership()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        using var other = Repository.OpenWithPassword(repo.DirectoryPath, EncryptionTests.Password);
        repo.EnrollmentObserved = stage =>
        {
            if (stage != EnrollmentStage.CandidateFlushed) return;
            // Independent unlocked session on a second thread cannot mutate the bootstrap or history.
            Task.Run(() => Assert.Throws<IOException>(() => other.EnrollCurrentWindowsUser())).GetAwaiter().GetResult();
            Assert.Throws<IOException>(() => other.OpenWriter());
        };
        repo.EnrollCurrentWindowsUser(); other.EnrollCurrentWindowsUser();
        using var silent = Repository.OpenWithWindowsUser(repo.DirectoryPath); Assert.Equal(repo.Identity, silent.Identity);
    }

    [SmbFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void NativeUncEnrollmentUsesExistingNetworkRenameAndPasswordRecovery()
    {
        using var smb = new SmbTestEnvironment(); var path = Path.Combine(smb.Remote, "repository");
        using var repo = Repository.CreateEncrypted(path, EncryptionTests.Password);
        var before = TemporaryDirectory.Snapshot(path); repo.EnrollCurrentWindowsUser();
        using var windows = Repository.OpenWithWindowsUser(path); using var password = Repository.OpenWithPassword(path, EncryptionTests.Password);
        var after = TemporaryDirectory.Snapshot(path); after["repository.json"] = before["repository.json"]; Assert.Equal(before, after);
        var header = File.ReadAllBytes(Path.Combine(path, "repository.json"));
        using (var denied = new FileStream(Path.Combine(path, "repository.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<OwnershipEnrollmentUncertainException>(() => repo.EnrollCurrentWindowsUser());
        Assert.Equal(header, File.ReadAllBytes(Path.Combine(path, "repository.json")));
        using var fresh = Repository.OpenWithWindowsUser(path); Assert.Equal(repo.Identity, fresh.Identity);
    }
}
