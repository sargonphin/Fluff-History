using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Windows;
using FluffHistory.Engine;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ExactVersionRemovalTests
{
    [WindowsFact]
    public async Task EnrolledEncryptedRepositoryUsesTheSameExactRemovalAndNativeShellRestore()
    {
        using var f = new LiveCapacityFixture(encrypted: true); f.Repository.EnrollCurrentWindowsUser(); var old = f.Seed(); f.Seed(content: "newer", day: 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var version = (await browser.GetPathHistoryAsync(Assert.Single((await browser.GetChildrenAsync(new(FoundationTests.Root))).Items).Version!)).Items[1];
        var output = f.Temp.Child("restored");
        var restore = await new WindowsHistoryActions().RestoreExactAsync(f.Repository.DirectoryPath, version, output, 0);
        Assert.Equal(1, restore.Copied); Assert.Equal(0, restore.Failed); Assert.Equal("hello", File.ReadAllText(Path.Combine(output, "a")));
        Assert.Equal(1, (await new ExactVersionRemovalService(new()).RemoveAsync(f.Repository.DirectoryPath, version)).Removed);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(old.Identity)); Assert.NotNull(reader.GetCurrent(old.Identity.Path));
    }
    [Fact]
    public async Task RemovesOnlyCapturedHistoricalVersionProtectsCurrentAndRejectsStaleOrForeignHandles()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); var current = f.Seed(content: "newer", day: 1);
        using var other = new LiveCapacityFixture(); other.Seed();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var file = Assert.Single((await browser.GetChildrenAsync(new(FoundationTests.Root))).Items);
        var versions = (await browser.GetPathHistoryAsync(file.Version!)).Items;
        Assert.True(versions[0].IsCurrent); Assert.False(versions[1].IsCurrent);
        var service = new ExactVersionRemovalService(new());
        Assert.True(await service.IsCurrentAsync(f.Repository.DirectoryPath, versions[0]));
        var currentFailure = await Assert.ThrowsAsync<ApplicationOperationException>(() => service.RemoveAsync(f.Repository.DirectoryPath, versions[0]));
        Assert.Equal(ApplicationFailure.InvalidSelection, currentFailure.Failure);
        var foreign = await Assert.ThrowsAsync<ApplicationOperationException>(() => service.RemoveAsync(other.Repository.DirectoryPath, versions[1]));
        Assert.Equal(ApplicationFailure.RefreshRequired, foreign.Failure);
        var result = await service.RemoveAsync(f.Repository.DirectoryPath, versions[1]);
        Assert.Equal(MaintenanceState.Completed, result.State); Assert.Equal(1, result.Removed); Assert.True(result.ChangesMayExist);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(old.Identity)); Assert.Equal(current.Identity, reader.GetCurrent(current.Identity.Path)!.Identity);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(f.Source, "a")));
        var stale = await Assert.ThrowsAsync<ApplicationOperationException>(() => service.RemoveAsync(f.Repository.DirectoryPath, versions[1]));
        Assert.Equal(ApplicationFailure.RefreshRequired, stale.Failure);
    }

    [Fact]
    public async Task RechecksAfterConfirmationAndRefusesReactivatedCurrentWithoutChangingAuthority()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(current: false);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var version = (await browser.GetPathHistoryAsync(Assert.Single((await browser.GetChildrenAsync(new(FoundationTests.Root))).Items).Version!)).Items[0];
        var service = new ExactVersionRemovalService(new()); Assert.False(await service.IsCurrentAsync(f.Repository.DirectoryPath, version));
        using (var writer = f.Repository.OpenWriter()) using (var reader = f.Repository.OpenReader()) writer.Apply(new(currentUpserts: [old.Identity]), reader.Authority);
        using var fresh = f.Repository.OpenReader(); var before = fresh.Authority;
        var failure = await Assert.ThrowsAsync<ApplicationOperationException>(() => service.RemoveAsync(f.Repository.DirectoryPath, version));
        Assert.Equal(ApplicationFailure.RefreshRequired, failure.Failure);
        using var after = f.Repository.OpenReader(); Assert.Equal(before, after.Authority); Assert.NotNull(after.GetVersion(old.Identity));
    }

    [Theory]
    [InlineData("cancel-before")]
    [InlineData("cancel-after")]
    [InlineData("lost-after")]
    public async Task CancellationOrUncertainPublicationNeverClaimsSuccessfulDeletion(string scenario)
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(); f.Seed(content: "newer", day: 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var version = (await browser.GetPathHistoryAsync(Assert.Single((await browser.GetChildrenAsync(new(FoundationTests.Root))).Items).Version!)).Items[1];
        using var cancel = new CancellationTokenSource();
        var service = new ExactVersionRemovalService(new()) { ConfigureWriter = writer => writer.Observe = stage =>
        {
            if (scenario == "cancel-before" && stage == PublicationStage.BeforeReplace) cancel.Cancel();
            if (scenario == "cancel-after" && stage == PublicationStage.AfterReplace) cancel.Cancel();
            if (scenario == "lost-after" && stage == PublicationStage.AfterReplace) writer.LoseHandleForTest();
        } };
        var result = await service.RemoveAsync(f.Repository.DirectoryPath, version, cancel.Token);
        Assert.Equal(scenario == "lost-after" ? MaintenanceState.NeedsAttention : MaintenanceState.Cancelled, result.State);
        Assert.Equal(scenario != "cancel-before", result.ChangesMayExist); Assert.True(File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId)));
    }

    [Fact]
    public async Task BusyAdmissionDoesNotQueueADeletion()
    {
        using var f = new LiveCapacityFixture(); f.Seed(); f.Seed(content: "newer", day: 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var version = (await browser.GetPathHistoryAsync(Assert.Single((await browser.GetChildrenAsync(new(FoundationTests.Root))).Items).Version!)).Items[1];
        var gate = new BackupOperationGate(); Assert.True(gate.TryEnter());
        try { var failure = await Assert.ThrowsAsync<ApplicationOperationException>(() => new ExactVersionRemovalService(gate).RemoveAsync(f.Repository.DirectoryPath, version)); Assert.Equal(ApplicationFailure.Busy, failure.Failure); }
        finally { gate.Exit(); }
    }
}
