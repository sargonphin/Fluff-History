using FluffHistory.Tests.Infrastructure;
using FluffHistory.Windows;
using Xunit;

namespace FluffHistory.Tests;

public sealed class WindowsStorageUsageTests
{
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task StoredSizeIncludesHiddenLockedFilesAndSkipsExternalLinksWithoutReadingPayloads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var root = temp.Child(); Directory.CreateDirectory(Path.Combine(root, "nested"));
        var file = Path.Combine(root, "nested", "stored.bin"); File.WriteAllBytes(file, new byte[1234]);
        File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.System);
        File.WriteAllBytes(Path.Combine(root, "metadata"), new byte[17]);
        var outside = temp.Child("outside"); Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "unrelated"), new byte[9999]);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        var service = new WindowsStorageUsage();
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(1251, await service.ObserveBackupAsync(root, CancellationToken.None));
        var disk = await service.ObserveDriveAsync(root, CancellationToken.None);
        Assert.NotNull(disk); Assert.True(disk.Capacity > 0); Assert.InRange(disk.Free, 0, disk.Capacity);
        Assert.Null(await service.ObserveBackupAsync(Path.Combine(root, "link"), CancellationToken.None));
        File.SetAttributes(file, FileAttributes.Normal);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task MissingLocationsAreUnknownAndCancellationDoesNotStartAnotherObservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var service = new WindowsStorageUsage();
        Assert.Null(await service.ObserveDriveAsync(temp.Child("absent"), CancellationToken.None));
        Assert.Null(await service.ObserveBackupAsync(temp.Child("absent"), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ObserveDriveAsync(temp.Root, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ObserveBackupAsync(temp.Root, cancelled.Token));
    }
}
