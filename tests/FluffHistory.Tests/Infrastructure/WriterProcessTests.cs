using Xunit;

namespace FluffHistory.Tests.Infrastructure;

public sealed class WriterProcessTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public async Task AccessBarrierRetriesOnlyUntilTheRequiredOpenActuallySucceeds(int error)
    {
        var attempts = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await WriterProcess.WaitForFileAccessAsync(() =>
        {
            if (++attempts < 3) throw new IOException("Simulated delayed sharing teardown.", unchecked((int)(0x80070000u | (uint)error)));
        }, isWindows: true, timeout.Token);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessBarrierDoesNotRetryOtherErrorsOrHideMissingFiles(bool missing)
    {
        var attempts = 0;
        var error = missing ? new FileNotFoundException("Source missing.") : new IOException("Not a sharing error.");
        var actual = await Record.ExceptionAsync(() => WriterProcess.WaitForFileAccessAsync(() =>
        {
            attempts++;
            throw error;
        }, isWindows: true, CancellationToken.None));
        Assert.Same(error, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AccessBarrierHonorsItsDeadlineWhileSharingIsStillDenied()
    {
        using var deadline = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriterProcess.WaitForFileAccessAsync(() =>
        {
            attempts++;
            deadline.Cancel(); // Deterministic expiration while access remains denied; no fixed sleep.
            throw new IOException("Still held.", unchecked((int)0x80070020));
        }, isWindows: true, deadline.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AccessBarrierDoesNotApplyWindowsSharingRetriesOnUnix()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => WriterProcess.WaitForFileAccessAsync(() =>
        {
            attempts++;
            throw new IOException("Synthetic Windows error on a non-Windows path.", unchecked((int)0x80070020));
        }, isWindows: false, CancellationToken.None));
        Assert.Equal(1, attempts);
    }
}
