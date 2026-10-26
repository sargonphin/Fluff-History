using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class UserFolderExclusionTests
{
    [Fact]
    public void IndependentProjectionPreservesUnrelatedFieldsAndSeparateAutomaticChoices()
    {
        var source = SourceMapping.New(@"C:\Data") with { ForceInclude = true, MaximumFileSize = 1000, AutomaticExcludedDirectories = ["CACHE"] };
        var choices = FolderExclusions.Add([], @"C:\Data\Private\Nested");
        choices = FolderExclusions.Add(choices, @"C:\Data\Private");
        var projected = Assert.Single(FolderExclusions.Apply([source], choices));
        Assert.Equal(new[] { "PRIVATE" }, projected.ExcludedDirectories);
        Assert.Equal(source, projected with { ExcludedDirectories = [] });
        choices = FolderExclusions.Add(choices, @"C:\Data\Cache");
        choices = FolderExclusions.Remove(choices, @"C:\Data\Cache");
        Assert.Contains("CACHE", Assert.Single(FolderExclusions.Apply([source], choices)).ToConfiguration().ExcludedDirectories);
    }

    [Fact]
    public void EffectiveCountAndPathSizeBoundsAreNotTruncated()
    {
        var source = SourceMapping.New(@"C:\Data") with { AutomaticExcludedDirectories = Enumerable.Range(0, 256).Select(i => $"AUTO{i}").ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => FolderExclusions.Apply([source], [@"C:\Data\Private"]));
        Assert.Throws<ArgumentException>(() => FolderExclusions.Apply([source], [@"C:\Data\" + new string('a', 2049)]));
        Assert.Empty(source.ExcludedDirectories); Assert.Equal(256, source.AutomaticExcludedDirectories.Length);
        var large = SourceMapping.New(@"C:\Data") with { ExcludedDirectories = Enumerable.Range(0, 255)
            .Select(i => $"D{i:D3}/" + new string('a', 1015)).ToImmutableArray() };
        _ = large.ToConfiguration();
        Assert.Throws<InvalidDataException>(() => FolderExclusions.Apply([large], [@"C:\Data\" + new string('b', 2048)]));
    }
    [Fact]
    public async Task DraftCommandsSaveDiscardSelectionAndRoundTripUserAndAutomaticChoicesSeparately()
    {
        using var temp = new TemporaryDirectory();
        var source = SourceMapping.New(@"C:\Data") with { ForceInclude = true, AutomaticExcludedDirectories = ["CACHE"] };
        var owner = new ProductContext { FolderPolicy = new SourceFolderPolicy(locations: [new(@"C:\Data\Cache", SourceSafetyKind.HardBlocked)]),
            Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [source] } };
        var dialogs = new ProductDialogs { SelectedFolder = @"C:\Data\Private" };
        var model = new ConfigurationViewModel(owner, dialogs);
        Assert.True(model.ExcludeFolder.CanExecute(null)); Assert.False(model.RemoveExclusion.CanExecute(null));
        await model.ExcludeFolder.ExecuteAsync();
        Assert.True(model.IsDirty); Assert.Equal(0, owner.Saves); Assert.Equal(@"C:\Data\Private", Assert.Single(model.ExcludedFolders).Path);
        owner.Notify(); Assert.Single(model.ExcludedFolders); // Navigation/status does not discard a draft.
        await model.Discard.ExecuteAsync(); Assert.False(model.IsDirty); Assert.Empty(model.ExcludedFolders);
        model.SelectedSource = Assert.Single(model.Sources); await model.ExcludeFolder.ExecuteAsync(); await model.Apply.ExecuteAsync();
        Assert.Equal(1, owner.Saves); Assert.False(model.IsDirty);
        var store = new AppSettingsStore(temp.Child("settings.json")); await store.SaveAsync(owner.Settings);
        var loaded = (await store.LoadAsync()).Settings!; var saved = Assert.Single(loaded.Sources);
        Assert.Empty(saved.ExcludedDirectories); Assert.Equal(new[] { @"C:\Data\Private" }, loaded.ExcludedFolders);
        Assert.Contains("PRIVATE", loaded.ToBackupConfiguration(owner.FolderPolicy).Sources.Single().Configuration.ExcludedDirectories);
        Assert.Equal(source.SourceRootId, saved.SourceRootId); Assert.True(saved.ForceInclude);
        Assert.Equal(new[] { "CACHE" }, saved.AutomaticExcludedDirectories);
        model.SelectedSource = Assert.Single(model.Sources); model.SelectedExclusion = Assert.Single(model.ExcludedFolders);
        Assert.True(model.RemoveExclusion.CanExecute(null)); await model.RemoveExclusion.ExecuteAsync(); Assert.True(model.IsDirty);
        Assert.Empty(model.ExcludedFolders); Assert.Null(model.SelectedExclusion);
        await model.Remove.ExecuteAsync(); Assert.Null(model.SelectedSource); Assert.True(model.ExcludeFolder.CanExecute(null));
    }

    [Fact]
    public async Task RejectedPickerSelectionPreservesDraftAndSelection()
    {
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [SourceMapping.New(@"C:\Data")] } };
        var dialogs = new ProductDialogs { SelectedFolder = @"C:\Data" };
        var model = new ConfigurationViewModel(owner, dialogs);
        model.SelectedSource = Assert.Single(model.Sources); await model.ExcludeFolder.ExecuteAsync();
        Assert.False(model.IsDirty); Assert.Empty(model.ExcludedFolders); Assert.NotNull(model.SelectedSource);
        Assert.Equal(1, dialogs.Notices); Assert.Equal(FolderExclusions.ConflictMessage, dialogs.LastWarning);
        Assert.DoesNotContain(FolderExclusions.ConflictMessage, model.Message);
    }

    [Fact]
    public async Task BusyPickerCannotEditAReplacedSourceOrConfiguration()
    {
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [SourceMapping.New(@"C:\Data")] } };
        var dialogs = new PendingFolderDialogs(); var model = new ConfigurationViewModel(owner, dialogs);
        model.SelectedSource = Assert.Single(model.Sources); var pending = model.ExcludeFolder.ExecuteAsync();
        Assert.False(model.CanEdit); Assert.False(model.Remove.CanExecute(null)); Assert.False(model.Apply.CanExecute(null));
        owner.Settings = AppSettings.Empty with { RepositoryPath = @"D:\NewBackup" }; owner.Notify();
        dialogs.Folder.SetResult(@"C:\Data\Private"); await pending;
        Assert.Empty(model.Sources); Assert.Empty(model.ExcludedFolders); Assert.False(model.IsDirty); Assert.Equal(0, owner.Saves);
    }

    [Fact]
    public async Task ExclusionEndsOnlyCoveredCurrentAndUnchangedReenableReactivatesExactHistory()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private/a.txt", "private"); f.Write("Other/b.txt", "keep");
        var parent = SourceMapping.FromConfiguration(new(f.Root, f.Source), f.Source);
        var policy = SourceFolderPolicy.Empty;
        SourceMapping[] mappings = [parent];
        using var repository = Repository.Open(f.RepositoryPath);
        var engine = new FileHistoryEngine(repository);
        Assert.Equal(2, engine.Scan(policy.Select(f.RepositoryPath, mappings)).VersionsCreated);
        var actualRoot = parent.SourceRootId;
        var relative = "Private/a.txt";
        using var before = repository.OpenReader(); var original = before.GetCurrent(new(actualRoot, relative))!;
        var payloads = Directory.GetFiles(Path.Combine(f.RepositoryPath, "versions"), "*", SearchOption.AllDirectories).Order().ToArray();
        var choices = FolderExclusions.Add([], Path.Combine(f.Source, "Private")); mappings = FolderExclusions.Apply([parent], choices).ToArray();
        var excluded = engine.Scan(policy.Select(f.RepositoryPath, mappings)); Assert.Equal(0, excluded.VersionsCreated); Assert.Equal(0, excluded.SourceBytesRead);
        using (var reader = repository.OpenReader())
        {
            Assert.Single(reader.EnumerateCurrent());
            Assert.NotNull(reader.GetCurrent(new(parent.SourceRootId, "Other/b.txt")));
            Assert.Null(reader.GetCurrent(new(actualRoot, relative)));
            Assert.Equal(original.Identity, Assert.Single(reader.EnumerateVersions(new(actualRoot, relative))).Identity);
        }
        await using (var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath))
        {
            Assert.False(Assert.Single((await browser.GetPathHistoryAsync(actualRoot, relative)).Items).IsCurrent);
            var children = (await browser.GetChildrenAsync(new(parent.SourceRootId))).Items;
            Assert.Contains(children, item => item.Name == "Other");
            Assert.Contains(children, item => item.Name == "Private" && item.Version is null); // Inferred only.
        }
        Assert.Equal(payloads, Directory.GetFiles(Path.Combine(f.RepositoryPath, "versions"), "*", SearchOption.AllDirectories).Order());
        choices = FolderExclusions.Remove(choices, Path.Combine(f.Source, "Private")); mappings = FolderExclusions.Apply([parent], choices).ToArray();
        var resumed = engine.Scan(policy.Select(f.RepositoryPath, mappings));
        Assert.Equal(0, resumed.VersionsCreated); Assert.Equal(1, resumed.Reactivations); Assert.Equal(0, resumed.SourceBytesRead);
        using var after = repository.OpenReader(); Assert.Equal(original.Identity, after.GetCurrent(new(actualRoot, relative))!.Identity);
        Assert.Single(after.EnumerateVersions(new(actualRoot, relative)));
    }

    [Fact]
    public void ExplicitExclusionIsPublishedEvenWhenUnrelatedSourceAccessFailsWithoutFalseDeletion()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Excluded/a.txt", "a"); f.Write("Blocked/b.txt", "b"); f.Write("Other/c.txt", "c"); f.Scan();
        using var repository = Repository.Open(f.RepositoryPath);
        var source = new SourceRootConfiguration(f.Root, f.Source, excludedDirectories: ["Excluded"]);
        var engine = new FileHistoryEngine(repository, new(), new DeniedSubtree(Path.Combine(f.Source, "Blocked")));
        var result = engine.Scan([new(source, f.Source)]); Assert.Equal(1, result.SourceSkips); Assert.Equal(0, result.VersionsCreated);
        using var reader = repository.OpenReader();
        Assert.Null(reader.GetCurrent(new(f.Root, "Excluded/a.txt"))); Assert.NotNull(reader.GetCurrent(new(f.Root, "Blocked/b.txt")));
        Assert.NotNull(reader.GetCurrent(new(f.Root, "Other/c.txt"))); Assert.Single(reader.EnumerateVersions(new(f.Root, "Excluded/a.txt")));
    }

    [Fact]
    public void ExcludedHistoryBecomesEligibleForNormalRetentionWhileUnrelatedCurrentRemainsProtected()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private/a.txt", "a"); f.Write("Other/b.txt", "b");
        using var repo = Repository.Open(f.RepositoryPath); var engine = new FileHistoryEngine(repo) { Clock = () => FoundationTests.Time };
        engine.Scan([new(new(f.Root, f.Source), f.Source)]);
        engine.Scan([new(new(f.Root, f.Source, excludedDirectories: ["Private"]), f.Source)]);
        var retention = new RetentionEngine(repo);
        Assert.Equal(0, retention.Preview(RetentionPolicy.Forever, FoundationTests.Time.AddDays(100)).Eligible);
        var preview = retention.Preview(RetentionPolicy.OneMonth, FoundationTests.Time.AddDays(100));
        Assert.Equal(1, preview.Eligible); Assert.Equal(1, preview.Protected); Assert.Equal(1, retention.Execute(preview).Removed);
        using var reader = repo.OpenReader(); Assert.Empty(reader.EnumerateVersions(new(f.Root, "Private/a.txt")));
        Assert.NotNull(reader.GetCurrent(new(f.Root, "Other/b.txt")));
    }

    private sealed class DeniedSubtree(string path) : SourceAccess
    {
        internal override IEnumerable<string> EnumerateNames(string directory) => directory == path ? throw new IOException("Controlled inaccessible subtree.") : base.EnumerateNames(directory);
    }

    private sealed class PendingFolderDialogs : IDesktopDialogs
    {
        public IAppStrings Strings { get; set; } = TestAppStrings.English;
        internal readonly TaskCompletionSource<string?> Folder = new();
        public Task<string?> FolderAsync() => Folder.Task;
        public Task<char[]?> PasswordAsync(bool creation) => Task.FromResult<char[]?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string primary) => Task.FromResult(false);
        public Task NoticeAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmLocationAsync(BackupLocationConfirmation confirmation) => Task.FromResult(false);
        public void Navigate(string page) { }
        public string DiagnosticsHeader => "Test";
        public string SettingsPath => "test";
        public void CopyText(string text) { }
    }
}
