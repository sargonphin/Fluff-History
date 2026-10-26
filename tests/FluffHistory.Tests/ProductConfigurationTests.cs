using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ProductConfigurationTests
{
    [Fact]
    public async Task FreshSixMonthDefaultDoesNotChangeAnExplicitSavedForeverChoice()
    {
        var fresh = new ConfigurationViewModel(new ProductContext(), new ProductDialogs());
        Assert.Equal("sixMonths", fresh.SelectedRetention.Name); Assert.False(fresh.ForeverWarning);
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json"));
        await store.SaveAsync(AppSettings.Empty with { RetentionPolicyName = "forever" });
        var saved = (await store.LoadAsync()).Settings!;
        var existing = new ConfigurationViewModel(new ProductContext { Settings = saved }, new ProductDialogs());
        Assert.Equal("forever", existing.SelectedRetention.Name); Assert.True(existing.ForeverWarning);
    }
    [Fact]
    public void ClosedMenusHaveExactLabelsDefaultsAndStablePolicyNames()
    {
        Assert.Equal(new[] { 10, 30, 60, 180, 360, 720, 1440 }, ProductConfiguration.Frequencies.Select(f => f.Minutes));
        Assert.Equal(new[] { "Every 10 minutes", "Every 30 minutes", "Every hour", "Every 3 hours", "Every 6 hours", "Every 12 hours", "Every day" }, ProductConfiguration.Frequencies.Select(f => f.Label));
        Assert.Equal(60, AppSettings.Empty.IntervalMinutes); Assert.Equal(RetentionPolicy.SixMonths, AppSettings.Empty.RetentionPolicy);
        Assert.Equal(Enum.GetValues<RetentionPolicy>(), ProductConfiguration.RetentionPolicies.Select(p => p.Policy));
        Assert.Equal("Until space saturation", ProductConfiguration.RetentionPolicies[0].Label);
        Assert.Contains("safety threshold", ProductConfiguration.RetentionPolicies[0].Notice); Assert.Contains("Current versions remain protected", ProductConfiguration.RetentionPolicies[0].Notice);
        Assert.Contains("never removes Timeline history automatically", ProductConfiguration.Retention("forever").Notice);
        foreach (var interval in new[] { 1, 5, 15, 20, 29, 10080 }) Assert.Throws<InvalidDataException>(() => (AppSettings.Empty with { IntervalMinutes = interval }).Validate());
        Assert.Throws<InvalidDataException>(() => ProductConfiguration.Retention("0"));
    }

    [Theory]
    [InlineData(@"C:\Repo", @"c:\repo\", true)]
    [InlineData(@"C:\Repo", @"C:\", true)]
    [InlineData(@"C:\Repo", @"C:\Repo\nested", true)]
    [InlineData(@"C:\Repo", @"C:\Repository", false)]
    [InlineData(@"C:\Repo", @"C:\Other\..\repo\child", true)]
    [InlineData(@"\\server\share\Repo", @"\\SERVER\SHARE\repo\Child", true)]
    [InlineData(@"\\server\share\Repo", @"\\server\share2\Repo", false)]
    [InlineData(@"\\server\share\Repo", @"\\server\share\Repository", false)]
    [InlineData(@"\\server\share\Repo", "//SERVER/share/repo/child/", true)]
    public void ComponentAwareOverlapIsEnforcedInSettingsAndRuntimeComposition(string repo, string source, bool overlap)
    {
        Assert.Equal(overlap, SourcePaths.Overlaps(repo, source)); var mapping = SourceMapping.New(source);
        var settings = AppSettings.Empty with { RepositoryPath = repo, Sources = [mapping] };
        if (overlap)
        {
            Assert.Throws<InvalidDataException>(settings.Validate);
            Assert.Throws<ArgumentException>(() => new BackupConfiguration(repo, [mapping.ToSelection()!]));
        }
        else { settings.Validate(); _ = settings.ToBackupConfiguration(); }
    }

    [Fact]
    public void CatalogUsesExactResolvedIdentityAndNeverArbitraryBasenames()
    {
        var policy = Catalog();
        Assert.Equal(PersonalFolderKind.Desktop, policy.Describe(@"c:\users\alice\Desktop\").Kind);
        Assert.Equal("Desktop", policy.Describe(@"C:\Users\Alice\Desktop").Label);
        Assert.Equal(PersonalFolderKind.Folder, policy.Describe(@"D:\Projects\Desktop").Kind);
        Assert.Equal("Desktop", policy.Describe(@"D:\Projects\Desktop").Label);
        Assert.Equal("Desktop", policy.Describe(@"D:\Projects\Desktop\").Label);
        Assert.Equal("share", policy.Describe(@"\\server\share\").Label);
        Assert.Equal(@"D:\", policy.Describe(@"D:\").Label);
        foreach (var path in new[] { @"D:\Project\Windows", @"D:\Archive\Program Files", @"D:\MyData\AppData", @"D:\MyData\Temp" })
            Assert.Equal(SourceSafetyKind.Ordinary, policy.Classify(path).Kind);
        Assert.Equal(SourceSafetyKind.ApplicationData, policy.Classify(@"C:\Users\Alice\AppData").Kind);
        foreach (var path in new[] { @"C:\Windows", @"C:\Program Files" }) Assert.Equal(SourceSafetyKind.System, policy.Classify(path).Kind);
        Assert.Equal(SourceSafetyKind.SharedApplicationData, policy.Classify(@"C:\ProgramData").Kind);
        foreach (var path in new[] { @"C:\System Volume Information", @"C:\$Recycle.Bin", @"C:\pagefile.sys" }) Assert.Equal(SourceSafetyKind.HardBlocked, policy.Classify(path).Kind);
    }

    [Fact]
    public async Task MultiAddRemoveAndReactivationPreserveIdentitySizeAndExclusionProtectionWithoutSavingImplicitly()
    {
        var source = SourceMapping.New(@"C:\Users\Alice\Desktop") with { MaximumFileSize = 1234, ExcludedDirectories = ["private/sub"] };
        var owner = new ProductContext { FolderPolicy = Catalog(), Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = [source] } };
        var dialogs = new ProductDialogs { SelectedFolders = [source.RuntimePath!, @"D:\Projects\Desktop"] };
        var vm = new ConfigurationViewModel(owner, dialogs); await vm.Add.ExecuteAsync();
        Assert.Equal(1, dialogs.MultiPickerCalls); Assert.Equal(2, vm.Sources.Count); Assert.Equal(0, owner.Saves);
        Assert.Equal("Desktop", vm.Sources[0].Label); Assert.Equal("Desktop", vm.Sources[1].Label);
        Assert.Equal(PersonalFolderKind.Desktop, vm.Sources[0].IconKind); Assert.Equal(PersonalFolderKind.Folder, vm.Sources[1].IconKind);
        Assert.Equal(@"C:\Users\Alice\Desktop", vm.Sources[0].Description); Assert.Equal(@"D:\Projects\Desktop", vm.Sources[1].Description);
        vm.SelectedSource = vm.Sources[0]; await vm.Remove.ExecuteAsync(); Assert.Single(vm.Sources);
        var inactive = vm.BuildDraft(DateTimeOffset.UtcNow).Sources.Single(s => s.SourceRootId == source.SourceRootId);
        Assert.False(inactive.IsProtected); Assert.Equal(source.RuntimePath, inactive.RuntimePath);
        Assert.Equal(new[] { @"C:\Users\Alice\Desktop\private\sub" }, vm.BuildDraft(DateTimeOffset.UtcNow).ExcludedFolders);
        owner.Notify(); Assert.Single(vm.Sources); // Navigation/status retains unsaved removal.
        dialogs.SelectedFolders = [source.RuntimePath!]; await vm.Add.ExecuteAsync();
        var reactivated = vm.BuildDraft(DateTimeOffset.UtcNow).Sources.Single(s => s.SourceRootId == source.SourceRootId);
        Assert.True(reactivated.IsProtected); Assert.Equal(source.MaximumFileSize, reactivated.MaximumFileSize); Assert.Equal(2, vm.Sources.Count);
        Assert.Contains("PRIVATE/SUB", vm.BuildDraft(DateTimeOffset.UtcNow).ToBackupConfiguration(owner.FolderPolicy).Sources.Single(s => s.Configuration.SourceRootId == source.SourceRootId).Configuration.ExcludedDirectories);
        Assert.Equal(2, vm.BuildDraft(DateTimeOffset.UtcNow).Sources.Select(s => s.SourceRootId).Distinct().Count());
        await vm.Apply.ExecuteAsync(); Assert.Equal(1, owner.Saves); Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task BroadProfileExcludesAppDataAndExplicitWarningCancellationIsAtomic()
    {
        var owner = new ProductContext { FolderPolicy = Catalog(), Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup" } };
        var dialogs = new ProductDialogs { SelectedFolders = [@"C:\Users\Alice"] }; var vm = new ConfigurationViewModel(owner, dialogs);
        await vm.Add.ExecuteAsync(); var parent = vm.BuildDraft(DateTimeOffset.UtcNow).Sources.Single();
        Assert.Contains("APPDATA", parent.AutomaticExcludedDirectories); Assert.Empty(parent.ExcludedDirectories);
        dialogs.SelectedFolders = [@"C:\Users\Alice\AppData"]; dialogs.Confirm = false; await vm.Add.ExecuteAsync();
        Assert.Single(vm.Sources); Assert.Contains("AppData contains", dialogs.LastWarning); Assert.Equal("Back up anyway", dialogs.LastPrimary);
        dialogs.Confirm = true; await vm.Add.ExecuteAsync(); var draft = vm.BuildDraft(DateTimeOffset.UtcNow); draft.Validate();
        Assert.Equal(2, draft.Sources.Length); Assert.True(draft.Sources.Single(s => s.RuntimePath!.EndsWith("AppData", StringComparison.Ordinal)).ForceInclude);
        Assert.Contains("APPDATA", draft.Sources[0].AutomaticExcludedDirectories);
        dialogs.SelectedFolders = [@"C:\Users\Alice\Documents"]; await vm.Add.ExecuteAsync(); Assert.Equal(2, vm.Sources.Count); Assert.Contains("Already backed up by", vm.Message);
        dialogs.SelectedFolders = [@"C:\System Volume Information", @"C:\$Recycle.Bin"]; var confirmations = dialogs.Confirmations; await vm.Add.ExecuteAsync();
        Assert.Equal(confirmations, dialogs.Confirmations); Assert.Equal(2, vm.Sources.Count); Assert.Contains("cannot be backed up", vm.Message);
    }

    [Fact]
    public async Task AlreadyActiveFolderDoesNotDirtyASavedConfigurationOrAllocateAnotherIdentity()
    {
        var policy = Catalog(); var mappings = policy.Prepare(@"D:\Backup", [SourceMapping.New(@"C:\Users\Alice")]);
        var owner = new ProductContext { FolderPolicy = policy, Settings = AppSettings.Empty with { RepositoryPath = @"D:\Backup", Sources = mappings } };
        var vm = new ConfigurationViewModel(owner, new ProductDialogs { SelectedFolders = [@"c:\users\alice\"] });
        await vm.Add.ExecuteAsync(); Assert.False(vm.IsDirty); Assert.Single(vm.Sources); Assert.Equal(mappings[0].SourceRootId, vm.Sources[0].Mapping.SourceRootId);
    }

    [Fact]
    public async Task FullKnownRootListCanStillReactivateAnExistingFolder()
    {
        var sources = Enumerable.Range(0, 1024).Select(i => SourceMapping.New($@"D:\Folder-{i}") with { IsProtected = false }).ToImmutableArray();
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = @"E:\Backup", Sources = sources } };
        var vm = new ConfigurationViewModel(owner, new ProductDialogs { SelectedFolders = [sources[0].RuntimePath!] });
        Assert.True(vm.Add.CanExecute(null)); await vm.Add.ExecuteAsync(); var draft = vm.BuildDraft(DateTimeOffset.UtcNow);
        Assert.Equal(1024, draft.Sources.Length); Assert.Equal(sources[0].SourceRootId, draft.Sources[0].SourceRootId); Assert.True(draft.Sources[0].IsProtected);
    }

    [Fact]
    public async Task AddingParentPreservesChildAndHandEditedDuplicateCoverageFailsClosed()
    {
        var child = SourceMapping.New(@"D:\Data\Child");
        var raw = AppSettings.Empty with { RepositoryPath = @"E:\Backup", Sources = [child, SourceMapping.New(@"D:\Data")] };
        Assert.Throws<InvalidDataException>(raw.Validate);
        var policy = Catalog(); var prepared = raw with { Sources = policy.Prepare(raw.RepositoryPath, raw.Sources) }; prepared.Validate();
        Assert.Equal(child.SourceRootId, prepared.Sources[0].SourceRootId); Assert.Contains("CHILD", prepared.Sources[1].AutomaticExcludedDirectories);
        var removed = policy.Prepare(raw.RepositoryPath, prepared.Sources.Select(s => s.SourceRootId == child.SourceRootId ? s with { IsProtected = false } : s));
        Assert.Contains("CHILD", removed[1].AutomaticExcludedDirectories); // Removal must not re-ID its files into the parent's Timeline.
        var owner = new ProductContext { FolderPolicy = policy, Settings = raw with { Sources = [child] } };
        var vm = new ConfigurationViewModel(owner, new ProductDialogs { SelectedFolders = [@"D:\Data"] }); await vm.Add.ExecuteAsync();
        Assert.Equal(child.SourceRootId, vm.BuildDraft(DateTimeOffset.UtcNow).Sources[0].SourceRootId);
        Assert.Throws<InvalidDataException>(() => policy.Prepare(@"D:\Backup", [SourceMapping.New(@"C:\Windows")]));
        Assert.Throws<InvalidDataException>(() => policy.Prepare(@"D:\Backup", [SourceMapping.New(@"C:\$Recycle.Bin") with { ForceInclude = true }]));
    }

    [Fact]
    public void RootFilesAndSystemSubtreesAreSkippedBeforeSourceObservationAndChildIsCopiedOnce()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("pagefile.sys", "paging"); f.Write("hiberfil.sys", "hibernate"); f.Write("swapfile.sys", "swap");
        f.Write("AppData/cache", "cache"); f.Write("Child/data", "user"); f.Write("keep", "user");
        var policy = new SourceFolderPolicy(locations: new[] { new SourceSafetyLocation(Path.Combine(f.Source, "AppData"), SourceSafetyKind.ApplicationData) }
            .Concat(new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" }.Select(p => new SourceSafetyLocation(Path.Combine(f.Source, p), SourceSafetyKind.HardBlocked, true))));
        var mappings = new[] { SourceMapping.New(f.Source), SourceMapping.New(Path.Combine(f.Source, "Child")) };
        using var repository = Repository.Open(f.RepositoryPath); var scan = new FileHistoryEngine(repository).Scan(policy.Select(f.RepositoryPath, mappings));
        Assert.Equal(2, scan.VersionsCreated); Assert.Equal(0, scan.SourceSkips); Assert.Equal(8, scan.SourceBytesRead);
        using var reader = repository.OpenReader(); Assert.Null(reader.GetNewestVersion(new(mappings[0].SourceRootId, "pagefile.sys")));
        Assert.Null(reader.GetNewestVersion(new(mappings[0].SourceRootId, "Child/data"))); Assert.NotNull(reader.GetNewestVersion(new(mappings[1].SourceRootId, "data")));
    }

    [Fact]
    public async Task RetentionAndAutomaticSwitchRoundTripWithoutOperationHistory()
    {
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json"));
        foreach (var policy in ProductConfiguration.RetentionPolicies)
        {
            var desired = AppSettings.Empty with { RetentionPolicyName = policy.Name, SchedulingEnabled = true };
            await store.SaveAsync(desired); var loaded = (await store.LoadAsync()).Settings!;
            Assert.Equal(policy.Policy, loaded.RetentionPolicy); Assert.True(loaded.SchedulingEnabled);
        }
        var text = File.ReadAllText(store.FilePath);
        foreach (var forbidden in new[] { "lastBackup", "lastRetention", "cleanupRun", "candidate", "password", "error" }) Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(store.FilePath, text.Replace("\"forever\"", "\"UntilSaturation\"", StringComparison.Ordinal));
        Assert.Equal(SettingsLoadState.Corrupt, (await store.LoadAsync()).State);
    }

    internal static SourceFolderPolicy Catalog() => new([(@"C:\Users\Alice\Desktop", new("Desktop", PersonalFolderKind.Desktop))],
        [new(@"C:\Users\Alice\AppData", SourceSafetyKind.ApplicationData), new(@"C:\Windows", SourceSafetyKind.System), new(@"C:\Program Files", SourceSafetyKind.System),
         new(@"C:\ProgramData", SourceSafetyKind.SharedApplicationData), new(@"C:\System Volume Information", SourceSafetyKind.HardBlocked), new(@"C:\$Recycle.Bin", SourceSafetyKind.HardBlocked),
         new(@"C:\pagefile.sys", SourceSafetyKind.HardBlocked, true)]);
}

internal sealed class ProductDialogs : IDesktopDialogs
{
        public IAppStrings Strings { get; set; } = TestAppStrings.English;
    public IReadOnlyList<string> SelectedFolders { get; set; } = [];
    public string? SelectedFolder { get; set; }
    public bool Confirm { get; set; } = true;
    public int MultiPickerCalls { get; private set; }
    public int Confirmations { get; private set; }
    public string LastWarning { get; private set; } = "";
    public string LastPrimary { get; private set; } = "";
    public int Notices { get; private set; }
    public BackupLocationConfirmation? LocationConfirmation { get; private set; }
    public string? NewLocation { get; private set; }
    public bool? ProtectionChoice { get; set; } = false;
    public Task<bool?> ProtectBackupAsync(string path) { NewLocation = path; return Task.FromResult(ProtectionChoice); }
    public Task NoticeAsync(string title, string message) { Notices++; LastWarning = message; return Task.CompletedTask; }
    public Task<bool> ConfirmLocationAsync(BackupLocationConfirmation confirmation)
    { LocationConfirmation = confirmation; return ConfirmAsync("Existing backup found", confirmation.Status + "\n" + confirmation.Explanation, confirmation.Primary ?? Strings[TextId.Action_UseBackupLocation]); }
    public Task<string?> FolderAsync() => Task.FromResult(SelectedFolder);
    public Task<IReadOnlyList<string>> FoldersAsync() { MultiPickerCalls++; return Task.FromResult(SelectedFolders); }
    public Task<char[]?> PasswordAsync(bool creation) => Task.FromResult<char[]?>(null);
    public Task<bool> ConfirmAsync(string title, string message, string primary) { Confirmations++; LastWarning = message; LastPrimary = primary; return Task.FromResult(Confirm); }
    public void Navigate(string page) { }
    public string DiagnosticsHeader => "Test";
    public string SettingsPath => "settings-v2.json";
    public void CopyText(string text) { }
}

internal sealed class ProductContext : IDesktopContext
{
        public IAppStrings Strings { get; set; } = TestAppStrings.English;
    public string? DefaultSourceFolder { get; set; }
    public AppSettings Settings { get; set; } = AppSettings.Empty;
    public RepositoryInspection Inspection { get; set; } = new(RepositoryReadiness.Ready);
    public BackupStatus? Status { get; set; }
    public HistoryBrowserSession? Browser { get; set; }
    public ISourceFolderPolicy FolderPolicy { get; set; } = SourceFolderPolicy.Empty;
    public TimelineMaintenanceService? Maintenance { get; set; }
    public string LastOutcome { get; set; } = "";
    public string BrowserActivity { get; set; } = "";
    public string CurrentPage { get; set; } = "Overview";
    public ApplicationFailure? LatestFailure { get; private set; }
    public bool HasError => LatestFailure is not null;
    public bool ConfigurationNeedsReset => false;
    public long HistoryRevision { get; private set; }
    public int Saves { get; private set; }
    public event Action? Changed;
    public async Task RunAsync(Func<CancellationToken, Task> command)
    { try { await command(default); } catch (ApplicationOperationException error) { LatestFailure = error.Failure; } }
    public Task SaveAsync(AppSettings settings, CancellationToken token, bool reset = false) { settings.Validate(); Settings = settings; Saves++; Notify(); return Task.CompletedTask; }
    public Task InspectAndComposeAsync(CancellationToken token) => Task.CompletedTask;
    public async Task WithBrowserAsync(Func<HistoryBrowserSession, CancellationToken, Task> action, CancellationToken token) => await action(Browser!, token);
    public Task RecoverAsync(char[] password, CancellationToken token) { Array.Clear(password); return Task.CompletedTask; }
    public Task PreviewAsync(BrowserVersion version, CancellationToken token) => Task.CompletedTask;
    public Task RestoreSelectionAsync(BrowserSelection selection, string? destination, Guid root, bool original, CancellationToken token) => Task.CompletedTask;
    public void BackUpNow() { }
    public void StartScheduling() { }
    public void StopScheduling() { }
    public Task CancelBackupAsync() => Task.CompletedTask;
    public void CancelHistory() { }
    public void ReleasePreview() { }
    public void TimelineChanged() { HistoryRevision++; Notify(); }
    public void Notify() => Changed?.Invoke();
}
