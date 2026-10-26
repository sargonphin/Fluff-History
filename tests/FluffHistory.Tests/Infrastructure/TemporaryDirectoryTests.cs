using Xunit;

namespace FluffHistory.Tests.Infrastructure;

public sealed class TemporaryDirectoryTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void WindowsCleanupRetriesTransientSharingErrors(int error)
    {
        var attempts = 0;

        TemporaryDirectory.DeleteWithSharingViolationRetry(() =>
        {
            if (++attempts == 1)
            {
                throw new IOException("Transient cleanup sharing violation.", error);
            }
        }, isWindows: true);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void PersistentSharingViolationStillFailsAfterBoundedCleanupRetries()
    {
        var attempts = 0;
        var error = new IOException("Still owned.", unchecked((int)0x80070020));

        var failure = Assert.Throws<IOException>(() => TemporaryDirectory.DeleteWithSharingViolationRetry(() =>
        {
            attempts++;
            throw error;
        }, isWindows: true));

        Assert.Equal(6, attempts);
        Assert.Same(error, failure);
    }

    [Theory]
    [InlineData(false, unchecked((int)0x80070020))]
    [InlineData(true, unchecked((int)0x80070005))]
    [InlineData(true, unchecked((int)0x80070091))]
    public void NonWindowsOrUnrelatedIoErrorsAreNotRetried(bool isWindows, int errorCode)
    {
        var attempts = 0;
        var error = new IOException("Not a transient Windows sharing violation.", errorCode);

        var failure = Assert.Throws<IOException>(() => TemporaryDirectory.DeleteWithSharingViolationRetry(() =>
        {
            attempts++;
            throw error;
        }, isWindows));

        Assert.Equal(1, attempts);
        Assert.Same(error, failure);
    }

    [Fact]
    public void CleanupDeletesOnlyItsOwnTemporaryRoot()
    {
        using var unrelated = new TemporaryDirectory();
        File.WriteAllText(unrelated.Child("keep.txt"), "keep");
        string root;
        using (var temporary = new TemporaryDirectory())
        {
            root = temporary.Root;
            File.WriteAllText(temporary.Child("owned.txt"), "owned");
        }

        Assert.False(Directory.Exists(root));
        Assert.Equal("keep", File.ReadAllText(unrelated.Child("keep.txt")));
    }
}
