using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class DefaultSourceUxTests
{
    [Fact]
    public async Task ProfileSuggestionIsAnUnsavedDraftAndSurvivesFirstEmptyLocationChoice()
    {
        using var temp = new TemporaryDirectory(); var profile = temp.Child("Profile"); var backup = temp.Child("Backup");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(backup);
        var owner = new ProductContext { DefaultSourceFolder = profile,
            FolderPolicy = new SourceFolderPolicy(locations: [new(Path.Combine(profile, "AppData"), SourceSafetyKind.ApplicationData)]) };
        var model = new ConfigurationViewModel(owner, new ProductDialogs { SelectedFolder = backup });
        var suggestion = Assert.Single(model.Sources);
        Assert.Equal(profile, suggestion.Path); Assert.True(model.IsDirty);
        Assert.Empty(owner.Settings.Sources); Assert.Equal(0, owner.Saves);
        await model.ChangeLocation.ExecuteAsync();
        Assert.Equal(suggestion.Mapping.SourceRootId, Assert.Single(model.Sources).Mapping.SourceRootId);
        var draft = model.BuildDraft(DateTimeOffset.UtcNow); var source = Assert.Single(draft.Sources);
        Assert.False(source.ForceInclude); Assert.Contains(source.AutomaticExcludedDirectories, path => path.Equals("AppData", StringComparison.OrdinalIgnoreCase));
        await model.Apply.ExecuteAsync();
        Assert.Equal(profile, Assert.Single(owner.Settings.Sources).RuntimePath); Assert.Equal(1, owner.Saves);
    }

    [Fact]
    public async Task RemovedSuggestionStaysRemovedWhenTheFirstBackupLocationIsChosen()
    {
        using var temp = new TemporaryDirectory(); var backup = temp.Child("Backup"); Directory.CreateDirectory(backup);
        var owner = new ProductContext { DefaultSourceFolder = temp.Child("Profile") };
        var model = new ConfigurationViewModel(owner, new ProductDialogs { SelectedFolder = backup });
        model.SelectedSource = Assert.Single(model.Sources); await model.Remove.ExecuteAsync();
        await model.ChangeLocation.ExecuteAsync();
        Assert.Empty(model.Sources); Assert.All(model.BuildDraft(DateTimeOffset.UtcNow).Sources, source => Assert.False(source.IsProtected));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExistingConfigurationKeepsItsExplicitSourcesIncludingAnEmptyList(bool hasSource)
    {
        using var temp = new TemporaryDirectory(); var profile = temp.Child("Profile");
        var settings = AppSettings.Empty with { RepositoryPath = temp.Child("Backup"), Sources = hasSource ? [SourceMapping.New(temp.Child("Chosen"))] : [] };
        var model = new ConfigurationViewModel(new ProductContext { Settings = settings, DefaultSourceFolder = profile }, new ProductDialogs());
        Assert.Equal(hasSource ? 1 : 0, model.Sources.Count); Assert.DoesNotContain(model.Sources, source => source.Path == profile); Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task DefaultProfileStillRejectsABackupDestinationInsideIt()
    {
        using var temp = new TemporaryDirectory(); var profile = temp.Child("Profile"); var backup = Path.Combine(profile, "Backup"); Directory.CreateDirectory(backup);
        var owner = new ProductContext { DefaultSourceFolder = profile };
        var model = new ConfigurationViewModel(owner, new ProductDialogs { SelectedFolder = backup }); await model.ChangeLocation.ExecuteAsync();
        Assert.NotEmpty(model.Validation); Assert.False(model.Apply.CanExecute(null)); Assert.Equal(0, owner.Saves);
    }
}
