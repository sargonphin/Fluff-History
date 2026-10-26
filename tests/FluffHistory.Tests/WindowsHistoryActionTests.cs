using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using FluffHistory.Windows;
using Xunit;

namespace FluffHistory.Tests;

public sealed class WindowsHistoryActionTests
{
    [WindowsFact]
    public async Task PreviewWrapperKeepsRepositoryAliveThroughStaAndReturnsIndependentExactLease()
    {
        using var fixture = new ApplicationAdapterTests.Fixture(); fixture.Write("a.txt", "synthetic preview"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var version = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single().Version!;
        var launcher = new Launcher(); var actions = new WindowsHistoryActions { PreviewLauncher = launcher };
        var lease = await actions.PreviewAsync(fixture.RepositoryPath, version, (nint)12345);
        Assert.Equal((nint)12345, launcher.Owner); Assert.Equal(ApartmentState.STA, launcher.Apartment);
        Assert.Equal("synthetic preview", File.ReadAllText(lease.FilePath));
        var path = lease.FilePath; lease.Dispose(); Assert.False(File.Exists(path));
    }

    [WindowsFact]
    public async Task ShellWrapperForwardsOwnerAndMappingAndRetainsRepositoryAcrossAwait()
    {
        using var fixture = new ApplicationAdapterTests.Fixture(); fixture.Write("a.txt", "content"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var selected = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single();
        var leafCompleted = false;
        var actions = new WindowsHistoryActions { ShellLeaf = async (repository, request, token) =>
        {
            Assert.Equal((nint)6789, request.OwnerWindow); Assert.Equal(fixture.Temp.Child("destination"), request.DestinationDirectory);
            await Task.Yield(); using var reader = repository.OpenReader(); Assert.NotNull(reader.GetVersion(selected.Version!.Identity));
            leafCompleted = true;
            return new InteractiveRestoreResult { Copied = 1, MetadataWarnings = 1, CancellationRequested = token.IsCancellationRequested };
        } };
        var result = await actions.RestoreAsync(fixture.RepositoryPath, selected.Selection, fixture.Temp.Child("destination"), (nint)6789);
        Assert.True(leafCompleted); Assert.Equal(1, result.Copied); Assert.Equal(1, result.MetadataWarnings);
    }
    private sealed class Launcher : IHistoricalFileLauncher
    {
        internal nint Owner; internal ApartmentState Apartment;
        public bool Open(string path, nint owner) { Owner = owner; Apartment = Thread.CurrentThread.GetApartmentState(); Assert.True(File.Exists(path)); return true; }
        public void OpenWith(string path, nint owner) => throw new InvalidOperationException();
    }
}
