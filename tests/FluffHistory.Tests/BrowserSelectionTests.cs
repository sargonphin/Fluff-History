using FluffHistory.Application;
using Xunit;

namespace FluffHistory.Tests;

public sealed class BrowserSelectionTests
{
    [Fact]
    public async Task CombinedFilesAndFolderAreFrozenAndRestoreExactlyTheSelectedChildren()
    {
        using var f = new ApplicationAdapterTests.Fixture();
        f.Write("Folder/nested.txt", "nested"); f.Write("one.txt", "one"); f.Write("unselected.txt", "leave"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var rows = (await browser.GetChildrenAsync(new(f.Root))).Items;
        var input = rows.Where(c => c.Name != "unselected.txt").ToList();
        var combined = browser.CreateSelection(input); input.Clear();
        Assert.Equal(2, combined.Request.Items.Count);
        var result = await new RestoreService().RestoreDisplayedAsync(f.RepositoryPath, combined, f.Temp.Child("out"));
        Assert.Equal(2, result.Restored); Assert.Equal(2, result.ChecksumsVerified);
        Assert.Equal("nested", File.ReadAllText(f.Temp.Child("out/Folder/nested.txt")));
        Assert.Equal("one", File.ReadAllText(f.Temp.Child("out/one.txt")));
        Assert.False(File.Exists(f.Temp.Child("out/unselected.txt")));
    }

    [Fact]
    public async Task EmptyNullDuplicateAndOverBoundInputsAreRejectedWithoutQueryIO()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var child = (await browser.GetChildrenAsync(new(f.Root))).Items.Single();
        browser.QueryEntered = () => throw new InvalidOperationException("Combining must not query the reader");
        Assert.Throws<ArgumentNullException>(() => browser.CreateSelection(null!));
        AssertInvalid(() => browser.CreateSelection([]));
        AssertInvalid(() => browser.CreateSelection([null!]));
        AssertInvalid(() => browser.CreateSelection([child, child]));
        AssertInvalid(() => browser.CreateSelection(Enumerable.Repeat(child, HistoryBrowserSession.MaximumSelectionItems + 1)));
        Assert.Single(browser.CreateSelection([child]).Request.Items);
    }

    [Fact]
    public async Task ForeignCaptureAndStaleRefreshAreRejectedEvenAtTheSameRepositoryAuthority()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        await using var first = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        await using var second = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var a = (await first.GetChildrenAsync(new(f.Root))).Items.Single();
        var b = (await second.GetChildrenAsync(new(f.Root))).Items.Single();
        AssertRefresh(() => first.CreateSelection([a, b]));
        await first.RefreshAsync(); AssertRefresh(() => first.CreateSelection([a]));
    }

    [Fact]
    public async Task DifferentAuthorityScopeAndTimeAreRejected()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Folder/a", "a"); f.Write("root", "r"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var root = (await browser.GetChildrenAsync(new(f.Root))).Items;
        var nested = (await browser.GetChildrenAsync(new(f.Root, "Folder"))).Items.Single();
        AssertInvalid(() => browser.CreateSelection([root[0], nested]));
        var time = (await browser.GetTimelineAsync(new(f.Root))).Items[0];
        var historical = (await browser.GetChildrenAsync(new(f.Root, AtUtc: time))).Items;
        AssertInvalid(() => browser.CreateSelection([root[0], historical[0]]));
        f.Write("new", "new"); f.Scan();
        await using var newer = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var fresh = (await newer.GetChildrenAsync(new(f.Root))).Items[0];
        AssertRefresh(() => newer.CreateSelection([fresh, root[0]]));
    }

    [Fact]
    public async Task HistoricalFileFolderConflictIdentityIsPreservedForPlannerRejection()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Alpha/child", "child"); f.Scan();
        Directory.Delete(Path.Combine(f.Source, "Alpha"), true); f.Write("Alpha", "file"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var rows = (await browser.GetChildrenAsync(new(f.Root))).Items;
        Assert.Equal(2, rows.Length); Assert.All(rows, c => Assert.True(c.HasConflict));
        var selection = browser.CreateSelection(rows); Assert.Equal(2, selection.Request.Items.Count);
        await Assert.ThrowsAsync<ApplicationOperationException>(() => new RestoreService().RestoreDisplayedAsync(f.RepositoryPath, selection, f.Temp.Child("out")));
        Assert.False(Directory.Exists(f.Temp.Child("out")));
    }

    [Fact]
    public async Task DisposalRejectsNewSelections()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var child = (await browser.GetChildrenAsync(new(f.Root))).Items.Single(); await browser.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => browser.CreateSelection([child]));
    }
    private static void AssertInvalid(Action action) => Assert.Equal(ApplicationFailure.InvalidSelection, Assert.Throws<ApplicationOperationException>(action).Failure);
    private static void AssertRefresh(Action action) => Assert.Equal(ApplicationFailure.RefreshRequired, Assert.Throws<ApplicationOperationException>(action).Failure);
}
