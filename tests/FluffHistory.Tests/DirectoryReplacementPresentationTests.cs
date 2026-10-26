using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Fact]
    public async Task ExclusionRemovalRefreshesTimelineWithQuietAccessibleCoexistenceAndRestoreTimeFeedback()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private", "old"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); var dialogs = new Dialogs { NextFolder = f.Temp.Child("restore") };
        owner.RestoreSelection = (selection, path) => new RestoreService().RestoreDisplayedAsync(f.RepositoryPath, selection, path);
        var model = new HistoryViewModel(owner, dialogs); await model.ActivateAsync();
        await model.SelectContextItemAsync(model.Roots[0]); await model.OpenSelected.ExecuteAsync();
        Assert.Single(model.Children);
        File.Delete(Path.Combine(f.Source, "Private")); f.Write("Private/child.txt", "child");
        var settings = owner.Settings with { Sources = [SourceMapping.FromConfiguration(new(f.Root, f.Source), f.Source)],
            ExcludedFolders = [Path.Combine(f.Source, "Private")] };
        using (var repository = Repository.Open(f.RepositoryPath))
        {
            var engine = new FileHistoryEngine(repository); engine.Scan(settings.ToBackupConfiguration().Sources);
            settings = settings with { ExcludedFolders = [] }; engine.Scan(settings.ToBackupConfiguration().Sources);
        }
        owner.Settings = settings; owner.HistoryRevision++; owner.Notify(); await model.ActivateAsync();
        var pair = model.Children.Where(c => c.Name == "Private").ToArray();
        Assert.Equal(2, pair.Length); Assert.All(pair, c => { Assert.True(c.Child.HasConflict); Assert.Empty(c.TileDescription); });
        Assert.Contains(", File,", Assert.Single(pair, c => !c.Child.IsFolder).AccessibleName);
        Assert.EndsWith(", Folder", Assert.Single(pair, c => c.Child.IsFolder).AccessibleName);
        Assert.False(Assert.Single(pair, c => !c.Child.IsFolder).Child.Version!.IsCurrent);
        await model.SelectChildrenAsync(pair); Assert.DoesNotContain("conflict", model.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dialogs.Notices); await model.RestoreSelected.ExecuteAsync();
        Assert.Contains("file and folder with the same path", Assert.Single(dialogs.Notices));
        Assert.False(Directory.Exists(dialogs.NextFolder));
        await model.SelectChildrenAsync([Assert.Single(pair, c => c.Child.IsFolder)]);
        await model.RestoreSelected.ExecuteAsync(); Assert.Single(dialogs.Notices);
        Assert.Equal("child", File.ReadAllText(Path.Combine(dialogs.NextFolder!, "Private", "child.txt")));
    }
}
