using System.Collections.Immutable;
using System.Text.Json.Nodes;
using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class IndependentFolderExclusionTests
{
    [Fact]
    public void OutsideChoiceIsInertThenProjectsWhenABroaderSourceIsAdded()
    {
        var settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [SourceMapping.New(@"C:\Other")], ExcludedFolders = [@"C:\Users\ExampleUser\Downloads"] };
        settings.Validate();
        Assert.Empty(settings.ToBackupConfiguration().Sources.Single().Configuration.ExcludedDirectories);
        var root = SourceMapping.New(@"C:\Users\ExampleUser") with { ForceInclude = true, MaximumFileSize = 1000, AutomaticExcludedDirectories = ["CACHE"] };
        settings = settings with { Sources = [root] };
        var projected = Assert.Single(FolderExclusions.Apply(settings.Sources, settings.ExcludedFolders));
        Assert.Equal(root, projected with { ExcludedDirectories = [] });
        Assert.Equal(new[] { "DOWNLOADS" }, projected.ExcludedDirectories);
        Assert.Empty(root.ExcludedDirectories); // Application choices do not rewrite configured source identity.
    }

    [Theory]
    [InlineData(@"C:\Users\ExampleUser\Downloads")]
    [InlineData(@"c:\users\EXAMPLEUSER\downloads\")]
    [InlineData(@"C:\Users\ExampleUser")]
    public void AnExclusionCannotCoverAnEntireExplicitlySelectedSource(string excluded)
    {
        var settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [SourceMapping.New(@"C:\Users\ExampleUser\Downloads")], ExcludedFolders = [excluded] };
        Assert.Equal(FolderExclusions.ConflictMessage, Assert.Throws<InvalidDataException>(settings.Validate).Message);
    }

    [Fact]
    public async Task IndependentListHasNoSourceSelectionDependencyAndRejectsConflictsInBothDirections()
    {
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup" } };
        var dialogs = new ProductDialogs { SelectedFolder = @"C:\Data\Downloads", SelectedFolders = [@"C:\Data\Downloads"] };
        var model = new ConfigurationViewModel(owner, dialogs);
        await model.ExcludeFolder.ExecuteAsync(); Assert.Single(model.ExcludedFolders); Assert.Empty(model.Sources);
        await model.Add.ExecuteAsync(); Assert.Empty(model.Sources); Assert.Single(model.ExcludedFolders);
        Assert.Equal(1, dialogs.Notices); Assert.Equal(FolderExclusions.ConflictMessage, dialogs.LastWarning);
        Assert.DoesNotContain(FolderExclusions.ConflictMessage, model.Message);
        dialogs.SelectedFolders = [@"C:\Data"]; await model.Add.ExecuteAsync(); Assert.Single(model.Sources);
        await model.Apply.ExecuteAsync(); Assert.Equal(1, owner.Saves);
        Assert.Equal(new[] { "DOWNLOADS" }, owner.Settings.ToBackupConfiguration().Sources.Single().Configuration.ExcludedDirectories);
        model.SelectedSource = Assert.Single(model.Sources); await model.Remove.ExecuteAsync();
        Assert.Single(model.ExcludedFolders); // Removing a source never erases independent choices.
        model.SelectedExclusion = Assert.Single(model.ExcludedFolders); await model.RemoveExclusion.ExecuteAsync();
        dialogs.SelectedFolders = [@"C:\Data\Downloads"]; await model.Add.ExecuteAsync();
        await model.ExcludeFolder.ExecuteAsync(); Assert.Empty(model.ExcludedFolders); Assert.Single(model.Sources);
        Assert.Equal(2, dialogs.Notices); Assert.Equal(FolderExclusions.ConflictMessage, dialogs.LastWarning);
        Assert.DoesNotContain(FolderExclusions.ConflictMessage, model.Message);
    }

    [Fact]
    public void ExplicitNestedSourceCannotBeSilentlyDisabledByAnIndependentExclusion()
    {
        var sources = SourceFolderPolicy.Empty.Prepare(@"D:\Backup", [SourceMapping.New(@"C:\Data"), SourceMapping.New(@"C:\Data\Private")]);
        var settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = sources, ExcludedFolders = [@"C:\Data\Private"] };
        Assert.Throws<InvalidDataException>(settings.Validate);
        // Automatic nested-root barriers stay a distinct policy, not user list entries.
        Assert.Equal(new[] { "PRIVATE" }, sources[0].AutomaticExcludedDirectories);
    }

    [Fact]
    public void NormalizationAndGlobalAndEffectiveBoundsNeverTruncate()
    {
        var folders = FolderExclusions.Add([], @"C:\Data\Private\Nested");
        folders = FolderExclusions.Add(folders, @"c:\data\Private");
        Assert.Single(folders); Assert.Single(FolderExclusions.Add(folders, @"C:\Data\Private\Deeper"));
        Assert.Throws<InvalidDataException>(() => FolderExclusions.Normalize(Enumerable.Range(0, 257).Select(i => @"C:\Unused\" + i)));
        Assert.Throws<InvalidDataException>(() => FolderExclusions.Normalize(Enumerable.Range(0, 256).Select(i => @"C:\Unused\" + i + new string('x', 1100))));
        var full = Enumerable.Range(0, 256).Select(i => @"C:\Data\" + i).ToImmutableArray();
        Assert.Equal(256, FolderExclusions.Add(full, @"C:\Data\0").Length);
        Assert.Single(FolderExclusions.Add(full, @"C:\Data"));
        var source = SourceMapping.New(@"C:\Data") with { AutomaticExcludedDirectories = Enumerable.Range(0, 256).Select(i => "AUTO" + i).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => FolderExclusions.Apply([source], [@"C:\Data\Private"]));
        Assert.Empty(source.ExcludedDirectories);
    }

    [Fact]
    public async Task OldSettingsWithoutTheOptionalListStillLoadAndIndependentChoicesRoundTrip()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("settings.json"); var store = new AppSettingsStore(path);
        var settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [SourceMapping.New(@"C:\Data")], ExcludedFolders = [@"C:\Data\Private", @"C:\Unused"] };
        await store.SaveAsync(settings);
        Assert.Equal(settings.ExcludedFolders.ToArray(), (await store.LoadAsync()).Settings!.ExcludedFolders);
        var old = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); old.Remove("excludedFolders");
        await File.WriteAllTextAsync(path, old.ToJsonString()); Assert.Empty((await store.LoadAsync()).Settings!.ExcludedFolders);
    }

    [Fact]
    public async Task LegacyMappedRelativeChoicesBecomeEditableWithoutAnAutomaticSaveOrDirtyDraft()
    {
        var source = SourceMapping.New(@"C:\Data") with { ExcludedDirectories = ["PRIVATE"], AutomaticExcludedDirectories = ["CACHE"] };
        var owner = new ProductContext { FolderPolicy = new SourceFolderPolicy(locations: [new(@"C:\Data\Cache", SourceSafetyKind.HardBlocked)]),
            Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [source] } };
        var model = new ConfigurationViewModel(owner, new ProductDialogs());
        Assert.False(model.IsDirty); Assert.Equal(0, owner.Saves); Assert.Equal(@"C:\Data\PRIVATE", Assert.Single(model.ExcludedFolders).Path);
        Assert.Equal(new[] { "PRIVATE" }, owner.Settings.Sources[0].ExcludedDirectories);
        model.SelectedExclusion = Assert.Single(model.ExcludedFolders); await model.RemoveExclusion.ExecuteAsync(); await model.Apply.ExecuteAsync();
        Assert.Empty(owner.Settings.ExcludedFolders); Assert.Empty(owner.Settings.Sources[0].ExcludedDirectories);
        Assert.Equal(new[] { "CACHE" }, owner.Settings.Sources[0].AutomaticExcludedDirectories);
    }

    [Fact]
    public void OversizedLegacyChoicesRemainVisibleForCorrectionRatherThanCrashingTheEditor()
    {
        var sources = new[] { @"C:\First", @"C:\Second" }.Select(path => SourceMapping.New(path) with
            { ExcludedDirectories = Enumerable.Range(0, 129).Select(i => "DIR" + i).ToImmutableArray() }).ToImmutableArray();
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = sources } };
        owner.Settings.Validate(); var model = new ConfigurationViewModel(owner, new ProductDialogs());
        Assert.False(model.IsDirty); Assert.Equal(258, model.ExcludedFolders.Count); Assert.Contains("256", model.Validation); Assert.False(model.Apply.CanExecute(null));
    }

    [Fact]
    public void IndependentChoicesReuseCurrentRemovalAndExactReactivationWithoutNewVersions()
    {
        using var fixture = new ApplicationAdapterTests.Fixture(); fixture.Write("Private/a.txt", "private"); fixture.Write("Other/b.txt", "keep");
        var settings = AppSettings.Empty with { RepositoryPath = fixture.RepositoryPath, Sources = [SourceMapping.FromConfiguration(new(fixture.Root, fixture.Source), fixture.Source)] };
        using var repository = Repository.Open(fixture.RepositoryPath); var engine = new FileHistoryEngine(repository);
        Assert.Equal(2, engine.Scan(settings.ToBackupConfiguration().Sources).VersionsCreated);
        using var before = repository.OpenReader(); var identity = before.GetCurrent(new(fixture.Root, "Private/a.txt"))!.Identity;
        settings = settings with { ExcludedFolders = [Path.Combine(fixture.Source, "Private")] };
        var excluded = engine.Scan(settings.ToBackupConfiguration().Sources); Assert.Equal(0, excluded.VersionsCreated); Assert.Equal(0, excluded.PayloadBytesWritten);
        using (var reader = repository.OpenReader())
        {
            Assert.Null(reader.GetCurrent(identity.Path)); Assert.Single(reader.EnumerateVersions(identity.Path)); Assert.NotNull(reader.GetCurrent(new(fixture.Root, "Other/b.txt")));
        }
        var resumed = engine.Scan((settings with { ExcludedFolders = [] }).ToBackupConfiguration().Sources);
        Assert.Equal(1, resumed.Reactivations); Assert.Equal(0, resumed.VersionsCreated); Assert.Equal(0, resumed.PayloadBytesWritten);
        using var after = repository.OpenReader(); Assert.Equal(identity, after.GetCurrent(identity.Path)!.Identity);
    }

    [Fact]
    public void ShellResolutionUsesTheSameEffectiveExclusions()
    {
        var settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [SourceMapping.New(@"C:\Data")], ExcludedFolders = [@"C:\Data\Private"] };
        Assert.Null(ShellPathResolver.Resolve(settings, @"C:\Data\Private\a.txt", ShellItemKind.File));
        Assert.NotNull(ShellPathResolver.Resolve(settings, @"C:\Data\Other\a.txt", ShellItemKind.File));
    }
}
