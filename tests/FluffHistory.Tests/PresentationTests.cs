using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Theory]
    [InlineData(0, "less than a second")]
    [InlineData(999, "less than a second")]
    [InlineData(1000, "1 second")]
    [InlineData(30999, "30 seconds")]
    [InlineData(59999, "59 seconds")]
    [InlineData(60000, "1 minute")]
    [InlineData(779999, "12 minutes")]
    [InlineData(3599999, "59 minutes")]
    [InlineData(3600000, "1 hour")]
    [InlineData(3659999, "1 hour")]
    [InlineData(3660000, "1 hour and 1 minute")]
    [InlineData(10020000, "2 hours and 47 minutes")]
    public void BackupDurationUsesWholeSecondsThenWholeMinutes(int milliseconds, string expected)
        => Assert.Equal(expected, PresentationText.Duration(TestAppStrings.English, TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void OverviewPutsCompletionTimeFirstCountsOnlyFilesAndDoesNotCallFailedAttemptsSuccessful()
    {
        var now = DateTimeOffset.UtcNow;
        var scan = new ScanResult { Outcome = ScanOutcome.Completed, VersionsCreated = 3, Reactivations = 2, Removals = 1 };
        var attempt = new AttemptSnapshot(AttemptTrigger.Manual, null, now.AddMinutes(-12), now, TimeSpan.FromMinutes(12), BackupAttemptResult.FromScan(scan));
        var owner = new Context { Status = new(1, SchedulerLifecycle.Idle, false, new(TimeSpan.FromHours(1), now), null,
            false, false, null, null, new(BackupPhase.Finished), attempt, now, 0, SafeguardDecision.Allow, null) };
        var vm = new OverviewViewModel(owner, new Dialogs());
        Assert.Equal($"Last backed up on {now.ToLocalTime():f} and took 12 minutes.", vm.CurrentBackup);
        Assert.Equal("3 new versions saved", vm.Counts); Assert.StartsWith("0 file problems · ", vm.ResultDetail);
        owner.Status = owner.Status with { LastAttempt = attempt with { Result = new(BackupOutcome.Cancelled) } };
        Assert.StartsWith("Last backup attempt on ", vm.CurrentBackup); Assert.Contains("Backup cancelled", vm.CurrentBackup);
        Assert.Empty(vm.Counts);
        owner.Status = owner.Status with { LastAttempt = attempt, IsAttemptActive = true, Progress = new(BackupPhase.Scanning) };
        Assert.Contains("Backup running", vm.CurrentBackup); Assert.Contains("Scanning and backing up", vm.CurrentPhase); Assert.Empty(vm.Counts); Assert.Empty(vm.ResultDetail);
    }

    [Fact]
    public async Task StorageObservesOnlyPageEntryOrAChangedVisibleLocationAndNeverStatusNotifications()
    {
        var driveCalls = 0; var backupCalls = 0;
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("backup") },
            DriveObserver = (_, _) => { driveCalls++; return Task.FromResult<CapacityObservation?>(new(10000, 2500)); },
            BackupObserver = (_, _) => { backupCalls++; return Task.FromResult<long?>(1500); } };
        var vm = new OverviewViewModel(owner, new Dialogs()); owner.Notify();
        Assert.Equal(0, driveCalls); Assert.Equal(0, backupCalls);
        await vm.Storage.ActivateAsync(vm.StoragePath);
        Assert.Equal(75, vm.Storage.UsedPercent); Assert.Equal($"Backup occupies {PresentationText.Size(TestAppStrings.English, 1500)}", vm.Storage.BackupSize);
        for (var i = 0; i < 20; i++) { owner.Notify(); _ = vm.Storage.DriveSpace; _ = vm.Storage.BackupSize; }
        await vm.Storage.ActivateAsync(vm.StoragePath);
        Assert.Equal(1, driveCalls); Assert.Equal(1, backupCalls);
        vm.Storage.Deactivate();
        owner.Settings = owner.Settings with { RepositoryPath = Path.GetFullPath("other") }; owner.Notify();
        Assert.Equal(1, driveCalls);
        await vm.Storage.ActivateAsync(vm.StoragePath); Assert.Equal(2, driveCalls);
        owner.Settings = owner.Settings with { RepositoryPath = Path.GetFullPath("third") }; owner.Notify();
        Assert.Equal(3, driveCalls); Assert.Equal(3, backupCalls);
        vm.Storage.Deactivate();
        var configuration = new ConfigurationViewModel(owner, new Dialogs());
        await configuration.Storage.ActivateAsync(configuration.RepositoryPath);
        Assert.Equal(4, driveCalls); Assert.Equal(3, backupCalls);
        configuration.Storage.Deactivate();
    }

    [Fact]
    public async Task LateStorageRepliesCannotReplaceAnotherLocationOrAnUnloadedPage()
    {
        var oldDrive = new TaskCompletionSource<CapacityObservation?>(); var oldBackup = new TaskCompletionSource<long?>();
        var owner = new Context {
            DriveObserver = (path, _) => path == "old" ? oldDrive.Task : Task.FromResult<CapacityObservation?>(new(1000, 100)),
            BackupObserver = (path, _) => path == "old" ? oldBackup.Task : Task.FromResult<long?>(20) };
        var vm = new StorageUsageViewModel(owner, true);
        var old = vm.ActivateAsync("old");
        var changed = vm.SetPathAsync("new"); Assert.False(vm.HasDriveSpace);
        oldDrive.SetResult(new(1000, 900)); oldBackup.SetResult(999);
        await Task.WhenAll(old, changed); Assert.Equal(90, vm.UsedPercent); Assert.Equal("Backup occupies 20 bytes", vm.BackupSize);
        oldDrive = new(); oldBackup = new(); var pending = vm.SetPathAsync("old"); vm.Deactivate();
        oldDrive.SetResult(new(1000, 900)); oldBackup.SetResult(999); await pending;
        Assert.False(vm.HasDriveSpace); Assert.DoesNotContain("999", vm.BackupSize);
    }

    [Fact]
    public async Task MissingStorageIsNeutralAndEmptyLocationsCauseNoObservations()
    {
        var calls = 0;
        var owner = new Context { DriveObserver = (_, _) => { calls++; throw new IOException(); }, BackupObserver = (_, _) => { calls++; throw new IOException(); } };
        var vm = new StorageUsageViewModel(owner, true); await vm.ActivateAsync(""); Assert.False(vm.HasLocation); Assert.Equal(0, calls);
        await vm.SetPathAsync("missing"); Assert.Equal(2, calls); Assert.False(vm.HasDriveSpace);
        Assert.Contains("unavailable", vm.DriveSpace); Assert.Contains("unavailable", vm.BackupSize); Assert.False(owner.HasError);
        vm.Deactivate();
    }
    [Fact]
    public async Task ContextGestureTargetsItsRowPreservesSelectedGroupAndRejectsRetiredRows()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a.txt", "a"); f.Write("b.txt", "b"); f.Write("Folder/c.txt", "c"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); var vm = new HistoryViewModel(owner, new Dialogs()); await vm.ActivateAsync();
        Assert.True(await vm.SelectContextItemAsync(vm.Roots[0])); Assert.True(vm.OpenSelected.CanExecute(null)); Assert.False(vm.RestoreOriginal.CanExecute(null));
        await vm.OpenSelected.ExecuteAsync();
        var a = vm.Children.Single(row => row.Name == "a.txt"); var b = vm.Children.Single(row => row.Name == "b.txt");
        await vm.SelectChildrenAsync([a, b]); Assert.True(await vm.SelectContextItemAsync(a)); Assert.Equal(2, vm.SelectedItems.Count);
        Assert.True(vm.RestoreSelected.CanExecute(null)); Assert.False(vm.PreviewDisplayed.CanExecute(null));
        var folder = vm.Children.Single(row => row.Child.IsFolder);
        Assert.True(await vm.SelectContextItemAsync(folder)); Assert.Same(folder, Assert.Single(vm.SelectedItems));
        Assert.True(vm.OpenSelected.CanExecute(null)); Assert.False(vm.PreviewDisplayed.CanExecute(null));
        Assert.True(await vm.SelectContextItemAsync(b)); Assert.Same(b, Assert.Single(vm.SelectedItems)); Assert.True(vm.PreviewDisplayed.CanExecute(null));
        Assert.False(await vm.SelectContextItemAsync(b with { }));
        await vm.RefreshAsync(); Assert.False(await vm.SelectContextItemAsync(b));
    }

    [Fact]
    public async Task ItemContextPreviewsDisplayedTimeWhileVersionContextUsesItsExactRow()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed("a.txt", "old"); var current = f.Seed("a.txt", "newer", 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Repository.DirectoryPath,
            Sources = [SourceMapping.New(f.Source) with { SourceRootId = FoundationTests.Root }] }, VersionRemoval = new(new()) };
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        var row = Assert.Single(vm.Children); Assert.True(await vm.SelectContextItemAsync(row));
        var older = vm.Versions[1]; Assert.True(vm.SelectContextVersion(older)); Assert.True(vm.DeleteVersion.CanExecute(null));
        await vm.PreviewDisplayed.ExecuteAsync(); Assert.Same(row.Child.Version, owner.PreviewedVersion); Assert.Equal(current.StoredUtc, owner.PreviewedVersion!.StoredUtc);
        await vm.Preview.ExecuteAsync(); Assert.Same(older.Version, owner.PreviewedVersion);
        Assert.False(vm.SelectContextVersion(new(older.Version, owner.Strings))); Assert.Same(older, vm.SelectedVersion);
        Assert.True(vm.SelectContextVersion(vm.Versions[0])); Assert.False(vm.DeleteVersion.CanExecute(null));
        await vm.ChangeTimeAsync(old.StoredUtc); Assert.True(await vm.SelectContextItemAsync(Assert.Single(vm.Children)));
        await vm.PreviewDisplayed.ExecuteAsync(); Assert.Equal(old.StoredUtc, owner.PreviewedVersion!.StoredUtc);
        Assert.False(vm.SelectContextVersion(older));
        owner.HistoryRevision++; owner.Notify(); Assert.False(vm.PreviewDisplayed.CanExecute(null));
        Assert.False(await vm.SelectContextItemAsync(vm.Children[0])); Assert.False(vm.SelectContextVersion(vm.Versions[0]));
    }

    [Fact]
    public async Task ContextPreviewBlocksSelectionReplacementUntilItsExistingOperationCompletes()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a.txt", "a"); f.Write("b.txt", "b"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); owner.PreviewCompletion = new();
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        var a = vm.Children[0]; var b = vm.Children[1]; Assert.True(await vm.SelectContextItemAsync(a));
        var preview = vm.PreviewDisplayed.ExecuteAsync(); Assert.False(vm.CanNavigate);
        Assert.False(await vm.SelectContextItemAsync(b)); Assert.Same(a, Assert.Single(vm.SelectedItems));
        owner.PreviewCompletion.SetResult(); await preview; Assert.True(await vm.SelectContextItemAsync(b));
    }

    [Fact]
    public async Task OneProtectionTimeHasNoEarlierNavigationAndKnownChildrenUseOriginIdentity()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Desktop/a", "a"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); owner.FolderPolicy = new SourceFolderPolicy([(Path.Combine(f.Source, "Desktop"), new("Desktop", PersonalFolderKind.Desktop))]);
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        Assert.Equal("known:Desktop", Assert.Single(vm.Children).IconKey);
        Assert.False(vm.Older.CanExecute(null)); await vm.Older.ExecuteAsync(); Assert.Equal("Latest", vm.TimeLabel); Assert.False(vm.Latest.CanExecute(null));
    }

    [Fact]
    public async Task ExactRestoreAndDeletionUseSelectedVersionAndKeepFolderAndTimeAfterRefresh()
    {
        using var f = new LiveCapacityFixture(); f.Seed("Folder/a", "old"); var current = f.Seed("Folder/a", "newer", 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Repository.DirectoryPath);
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Repository.DirectoryPath,
            Sources = [SourceMapping.New(f.Source) with { SourceRootId = FoundationTests.Root }] }, VersionRemoval = new(new()) };
        var dialogs = new Dialogs { NextFolder = f.Temp.Child("out") }; var vm = new HistoryViewModel(owner, dialogs); await ActivateSourceAsync(vm);
        await vm.ChangeFolderAsync("Folder"); await vm.ChangeTimeAsync(current.StoredUtc);
        await vm.SelectChildrenAsync([Assert.Single(vm.Children)]);
        Assert.False(vm.DeleteVersion.CanExecute(null)); Assert.Contains("protected", vm.VersionDeletionNotice);
        vm.SelectedVersion = vm.Versions[1]; Assert.True(vm.DeleteVersion.CanExecute(null)); Assert.Empty(vm.VersionDeletionNotice);
        await vm.RestoreExactOriginal.ExecuteAsync(); Assert.Same(vm.Versions[1].Version, owner.RestoredVersion); Assert.True(owner.RestoredOriginal);
        await vm.RestoreExact.ExecuteAsync(); Assert.False(owner.RestoredOriginal); Assert.Equal(dialogs.NextFolder, owner.RestoreDestination);
        dialogs.Confirm = false; await vm.DeleteVersion.ExecuteAsync(); Assert.Equal(2, vm.Versions.Count);
        dialogs.Confirm = true; await vm.DeleteVersion.ExecuteAsync();
        Assert.Equal(current.StoredUtc, vm.AtUtc); Assert.Equal("Folder", vm.Breadcrumbs[^1].Label); Assert.Single(vm.Children); Assert.Contains("deleted", vm.Message);
        await vm.SelectChildrenAsync([vm.Children[0]]); Assert.Single(vm.Versions); Assert.True(vm.Versions[0].Version.IsCurrent);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(f.Source, "Folder", "a")));
    }

    [Fact]
    public async Task CommandPreventsConcurrentInvocationAndRestoresEnablement()
    {
        var release = new TaskCompletionSource(); var calls = 0;
        var command = new AsyncCommand(async () => { calls++; await release.Task; });
        var first = command.ExecuteAsync(); Assert.True(command.IsBusy); Assert.False(command.CanExecute(null));
        await command.ExecuteAsync(); Assert.Equal(1, calls); release.SetResult(); await first;
        Assert.False(command.IsBusy); Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task DraftRetainsStableIdsAndAdvancedFieldsAndDoesNotSaveUntilApply()
    {
        using var temp = new TemporaryDirectory();
        var mapping = SourceMapping.New(temp.Child("source")) with { MaximumFileSize = 54321, ExcludedDirectories = ["private/sub"] };
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = temp.Child(), Sources = [mapping] }, Inspection = new(RepositoryReadiness.Ready) };
        var dialogs = new Dialogs { NextFolder = mapping.RuntimePath }; var vm = new ConfigurationViewModel(owner, dialogs);
        Assert.False(vm.IsDirty); vm.SelectedSource = vm.Sources[0]; await vm.Remove.ExecuteAsync();
        Assert.True(vm.IsDirty); Assert.Equal(0, owner.Saves); Assert.Equal(mapping, owner.Settings.Sources[0]);
        // Ordinary status changes/navigation must not erase the draft.
        owner.Notify(); Assert.Empty(vm.Sources); Assert.False(vm.BuildDraft(DateTimeOffset.UtcNow).Sources[0].IsProtected);
        await vm.Apply.ExecuteAsync(); Assert.Equal(1, owner.Saves); Assert.False(vm.IsDirty);
        var saved = owner.Settings.Sources.Single(); Assert.Equal(mapping.SourceRootId, saved.SourceRootId);
        Assert.Equal(mapping.MaximumFileSize, saved.MaximumFileSize); Assert.Empty(saved.ExcludedDirectories);
        Assert.Equal(new[] { Path.Combine(mapping.RuntimePath!, "private", "sub") }, owner.Settings.ExcludedFolders);
        vm.IntervalMinutes = 30; Assert.True(vm.IsDirty); await vm.Discard.ExecuteAsync(); Assert.False(vm.IsDirty); Assert.Equal(60, vm.IntervalMinutes);
    }

    [Fact]
    public void DraftValidationAndStableAnchorPolicyDoNotChangeSchema()
    {
        using var temp = new TemporaryDirectory(); var settings = AppSettings.Empty with { RepositoryPath = temp.Child() };
        var owner = new Context { Settings = settings }; var vm = new ConfigurationViewModel(owner, new Dialogs());
        vm.IntervalMinutes = double.NaN; Assert.NotEmpty(vm.Validation); Assert.False(vm.Apply.CanExecute(null));
        vm.IntervalMinutes = 0.5; Assert.NotEmpty(vm.Validation);
        var applied = DateTimeOffset.UtcNow; vm.IntervalMinutes = 30;
        Assert.Equal(applied, vm.BuildDraft(applied).AnchorUtc); Assert.Equal(2, vm.BuildDraft(applied).Schema);
        vm.IntervalMinutes = settings.IntervalMinutes; Assert.Equal(settings.AnchorUtc, vm.BuildDraft(applied).AnchorUtc);
        vm.RepositoryPath = temp.Child("not-inspected"); Assert.NotEmpty(vm.Validation); Assert.False(vm.Apply.CanExecute(null));
    }

    [Fact]
    public async Task DuplicateMappingsAreRejectedAndToggleUnmapPreserveConfiguration()
    {
        using var temp = new TemporaryDirectory(); var mapping = SourceMapping.New(temp.Child("source")) with { MaximumFileSize = 876, ExcludedDirectories = ["skip"] };
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = temp.Child(), Sources = [mapping] } };
        var vm = new ConfigurationViewModel(owner, new Dialogs { NextFolder = mapping.RuntimePath });
        await vm.Add.ExecuteAsync(); Assert.Single(vm.Sources);
        vm.SelectedSource = vm.Sources[0]; await vm.Remove.ExecuteAsync();
        var draft = vm.BuildDraft(DateTimeOffset.UtcNow).Sources[0]; Assert.False(draft.IsProtected); Assert.Equal(mapping.RuntimePath, draft.RuntimePath);
        Assert.Equal(mapping.MaximumFileSize, draft.MaximumFileSize); Assert.Empty(draft.ExcludedDirectories);
        Assert.Equal(new[] { Path.Combine(mapping.RuntimePath!, "skip") }, vm.BuildDraft(DateTimeOffset.UtcNow).ExcludedFolders);
    }

    [Theory]
    [InlineData(RepositoryReadiness.Missing, "Backup location unavailable")]
    [InlineData(RepositoryReadiness.Invalid, "Invalid backup location")]
    [InlineData(RepositoryReadiness.RecoveryRequired, "Recovery required")]
    public void OverviewMapsReadinessAndNeverClaimsProtectedWithoutEvidence(RepositoryReadiness state, string expected)
    {
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("fixture") }, Inspection = new(state) };
        var vm = new OverviewViewModel(owner, new Dialogs()); Assert.Equal(expected, vm.Title); Assert.False(vm.BackUp.CanExecute(null));
        Assert.Empty(vm.CurrentBackup); Assert.False(vm.HasCurrentBackup);
        Assert.Equal(state == RepositoryReadiness.RecoveryRequired, vm.Recover.CanExecute(null));
    }

    [Fact]
    public async Task HistoryAppendsAndResetsRowsWithoutDuplicatePagesAndRetainsReader()
    {
        using var f = new ApplicationAdapterTests.Fixture(); for (var i = 0; i < 270; i++) f.Write($"{i:D3}.txt", "x"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        Assert.Equal(128, vm.Children.Count); var reader = browser.ReaderForTests;
        await vm.LoadChildren.ExecuteAsync(); Assert.Equal(256, vm.Children.Count);
        await vm.LoadChildren.ExecuteAsync(); Assert.Equal(270, vm.Children.Count);
        Assert.Equal(270, vm.Children.Select(c => c.Name).Distinct().Count()); Assert.Same(reader, browser.ReaderForTests);
        Assert.False(vm.LoadChildren.CanExecute(null)); await vm.ChangeTimeAsync(vm.Times[1].Utc);
        Assert.Equal(128, vm.Children.Count); Assert.Same(reader, browser.ReaderForTests);
        await vm.Latest.ExecuteAsync(); Assert.Null(vm.SelectedTime?.Utc);
        await vm.SelectChildrenAsync([vm.Children[0], vm.Children[1]]); Assert.True(vm.RestoreSelected.CanExecute(null)); Assert.False(vm.Preview.CanExecute(null));
        await vm.Clear.ExecuteAsync(); Assert.False(vm.RestoreSelected.CanExecute(null));
    }

    [Fact]
    public void OverviewShowsActiveCancellationDeferredAndCapacityStatesInUserLanguage()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("fixture"), Sources = [SourceMapping.New(Path.GetFullPath("source"))] } };
        var vm = new OverviewViewModel(owner, new Dialogs()); Assert.True(vm.BackUp.CanExecute(null));
        owner.Status = new(1, SchedulerLifecycle.Running, true, new(TimeSpan.FromHours(1), now), now.AddHours(1), true, false, now,
            AttemptTrigger.Manual, new(BackupPhase.Scanning), null, null, 0, SafeguardDecision.Allow, null);
        owner.Notify(); Assert.Equal("Backup running", vm.Title); Assert.False(vm.BackUp.CanExecute(null)); Assert.True(vm.Cancel.CanExecute(null));
        owner.Status = owner.Status with { CancellationRequested = true }; owner.Notify(); Assert.False(vm.Cancel.CanExecute(null)); Assert.Contains("Cancellation requested", vm.CurrentBackup);
        owner.Status = owner.Status with { IsAttemptActive = false, LastSafeguard = new(false, SafeguardReason.OnBattery) }; owner.Notify();
        Assert.Equal("Backup deferred", vm.Title); Assert.Contains("external power", vm.Attention);
        var scan = new FluffHistory.Engine.ScanResult { Outcome = FluffHistory.Engine.ScanOutcome.CapacityBlocked };
        owner.Status = owner.Status with { LastSafeguard = SafeguardDecision.Allow, LastAttempt = new(AttemptTrigger.Manual, null, now, now, TimeSpan.Zero, BackupAttemptResult.FromScan(scan)) };
        owner.Notify(); Assert.Contains("space", vm.Attention); Assert.Contains("space", vm.Title);
        scan = new() { SourceSkips = 2, Outcome = FluffHistory.Engine.ScanOutcome.Completed };
        owner.Status = owner.Status with { LastAttempt = owner.Status.LastAttempt with { Result = BackupAttemptResult.FromScan(scan) } }; owner.Notify();
        Assert.Contains("paths and reasons", vm.Attention);
    }

    [Fact]
    public async Task FileVersionPaginationAppendsExactHandlesAndClearsOnFolderSelection()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 130; i++) f.Add(day: i);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        await vm.SelectChildrenAsync([vm.Children[0]]); Assert.Equal(128, vm.Versions.Count); Assert.True(vm.LoadVersions.CanExecute(null));
        await vm.LoadVersions.ExecuteAsync(); Assert.Equal(130, vm.Versions.Count); Assert.False(vm.LoadVersions.CanExecute(null));
        Assert.True(vm.Versions[0].Version.StoredUtc > vm.Versions[^1].Version.StoredUtc);
        vm.SelectedVersion = vm.Versions[^1]; Assert.True(vm.Preview.CanExecute(null));
        await vm.Clear.ExecuteAsync(); Assert.Empty(vm.Versions); Assert.False(vm.Preview.CanExecute(null));
    }

    [Fact]
    public async Task SupersededColdQueryCannotReplaceNewerFolderAndCancellationIsNotAnError()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("One/a", "a"); f.Write("Two/b", "b"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath); var owner = Ready(f, browser);
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0; browser.QueryEntered = () => { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } };
        var old = vm.ChangeFolderAsync("One"); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var newer = vm.ChangeFolderAsync("Two"); release.Set(); await Task.WhenAll(old, newer);
        Assert.Equal("b", Assert.Single(vm.Children).Name); Assert.False(vm.Busy); Assert.False(owner.HasError);
        vm.Deactivate(); Assert.False(vm.Busy); Assert.DoesNotContain("cancelled", vm.Message);
    }

    [Fact]
    public async Task LoadedCollectionsAndCombinedSelectionRespectTheirHardBounds()
    {
        using var temp = new TemporaryDirectory();
        using (var repository = FluffHistory.Storage.Repository.Create(temp.Child()))
        using (var writer = repository.OpenWriter())
        {
            for (var start = 0; start < 4224; start += 128)
            {
                using var reader = repository.OpenReader();
                var batch = Enumerable.Range(start, 128).Select(i =>
                {
                    var version = writer.PrepareVersion(new(FoundationTests.Root, $"{i:D4}.txt", 0, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
                        FoundationTests.Time.AddSeconds(i), new("EF46DB3751D8E999"));
                    var path = writer.GetPreparedPayloadPath(version); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, []); return version;
                }).ToArray();
                writer.Apply(new(batch, sourceRoots: [new(FoundationTests.Root, "Representative root")]), reader.Authority);
            }
        }
        await using var browser = await HistoryBrowserSession.OpenAsync(temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = temp.Child() } };
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        while (vm.LoadChildren.CanExecute(null)) await vm.LoadChildren.ExecuteAsync();
        Assert.Equal(4096, vm.Children.Count); Assert.Equal(4096, vm.Children.Select(c => c.Name).Distinct().Count());
        Assert.Contains("refine", vm.CountText); Assert.Equal(4096, browser.CreateSelection(vm.Children.Select(c => c.Child)).Request.Items.Count);
        while (vm.LoadTimes.CanExecute(null)) await vm.LoadTimes.ExecuteAsync();
        Assert.Equal(1025, vm.Times.Count); Assert.Contains("1,024", vm.TimeLimit);
        await vm.ChangeTimeAsync(vm.Times[^1].Utc!.Value.AddTicks(-1));
        Assert.True(vm.Older.CanExecute(null)); await vm.Older.ExecuteAsync(); Assert.True(vm.AtUtc < vm.Times[^1].Utc); Assert.NotEqual("Latest", vm.TimeLabel);
        // The arbitrary instant is older than all 1024 retained timestamps. Both directions must
        // find the globally nearest strict neighbor on the same reader without appending history.
        var deep = FoundationTests.Time.AddTicks(TimeSpan.TicksPerSecond / 2); var retained = vm.Times.ToArray(); var sameReader = browser.ReaderForTests;
        await vm.ChangeTimeAsync(deep); Assert.Single(vm.Children); await vm.Older.ExecuteAsync(); Assert.Equal(FoundationTests.Time, vm.AtUtc);
        await vm.ChangeTimeAsync(deep); await vm.Newer.ExecuteAsync(); Assert.Equal(FoundationTests.Time.AddSeconds(1), vm.AtUtc);
        Assert.Equal(retained, vm.Times); Assert.Same(sameReader, browser.ReaderForTests);
        using (var cancelled = new CancellationTokenSource())
        { cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.FindTimelineNeighborAsync(new(FoundationTests.Root), deep, false, cancelled.Token)); }
        await vm.ChangeFolderAsync("missing"); Assert.Empty(vm.Children); Assert.Single(vm.Times);
    }

    [Fact]
    public async Task ExpiredCaptureRequiresRefreshAndNewHistoryIsOnlyAnExplicitHint()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath); var owner = Ready(f, browser);
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        f.Write("b", "b"); f.Scan(); owner.HistoryRevision++; owner.Notify();
        Assert.True(vm.NeedsRefresh); Assert.Single(vm.Children);
        await vm.Refresh.ExecuteAsync(); Assert.Equal(2, vm.Children.Count); Assert.False(vm.NeedsRefresh);
        using (var repo = FluffHistory.Storage.Repository.Open(f.RepositoryPath)) using (var writer = repo.OpenWriter()) writer.CompactMetadata();
        await vm.ChangeFolderAsync(""); Assert.True(vm.NeedsRefresh); Assert.False(vm.RestoreSelected.CanExecute(null));
        await vm.Refresh.ExecuteAsync(); Assert.False(vm.NeedsRefresh);
    }

    [Fact]
    public async Task ArbitraryTimeStepsChooseStrictNeighborsAndNeverFallThroughToLatest()
    {
        using var f = new HistoryFixture(); f.Add(day: 0); f.Add(day: 2);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        var newest = vm.Times[1].Utc!.Value; var oldest = vm.Times[2].Utc!.Value;
        var arbitrary = oldest.AddDays(1);
        await vm.ChangeTimeAsync(arbitrary); Assert.Null(vm.SelectedTime);
        await vm.Older.ExecuteAsync(); Assert.Equal(oldest, vm.SelectedTime!.Utc);
        await vm.ChangeTimeAsync(arbitrary); await vm.Newer.ExecuteAsync(); Assert.Equal(newest, vm.SelectedTime!.Utc);
        await vm.ChangeTimeAsync(newest.AddHours(1));
        Assert.False(vm.Newer.CanExecute(null)); await vm.Newer.ExecuteAsync(); Assert.Null(vm.SelectedTime);
        await vm.Older.ExecuteAsync(); Assert.Equal(newest, vm.SelectedTime!.Utc);
        await vm.ChangeTimeAsync(oldest.AddHours(-1));
        Assert.False(vm.Older.CanExecute(null)); await vm.Older.ExecuteAsync(); Assert.Null(vm.SelectedTime);
        Assert.NotEqual("Latest", vm.TimeLabel);
        await vm.Newer.ExecuteAsync(); Assert.Equal(oldest, vm.SelectedTime!.Utc);
        await vm.Latest.ExecuteAsync(); Assert.Null(vm.SelectedTime!.Utc);
        Assert.False(vm.Newer.CanExecute(null)); await vm.Older.ExecuteAsync(); Assert.Equal(oldest, vm.SelectedTime!.Utc);
        Assert.True(vm.Newer.CanExecute(null)); await vm.Newer.ExecuteAsync(); Assert.Equal(newest, vm.AtUtc);
        Assert.Equal(3, vm.Times.Count); // No synthetic timestamp was added.
    }

    [Fact]
    public async Task ArbitraryTimeStepsLoadBoundedContinuationPagesOnTheSameCapture()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 130; i++) f.Add(day: i);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        var reader = browser.ReaderForTests; var lastLoaded = vm.Times[^1].Utc!.Value;
        Assert.Equal(129, vm.Times.Count);
        await vm.ChangeTimeAsync(lastLoaded.AddHours(-12)); await vm.Older.ExecuteAsync();
        Assert.Equal(lastLoaded.AddDays(-1), vm.AtUtc); Assert.Equal(129, vm.Times.Count);
        Assert.Same(reader, browser.ReaderForTests);
        await vm.Refresh.ExecuteAsync(); Assert.Equal(129, vm.Times.Count);
        await vm.ChangeTimeAsync(lastLoaded.AddHours(-36)); await vm.Newer.ExecuteAsync();
        Assert.Equal(lastLoaded.AddDays(-1), vm.AtUtc); Assert.Equal(129, vm.Times.Count);
        await vm.ChangeTimeAsync(lastLoaded.AddDays(-3)); await vm.Older.ExecuteAsync();
        Assert.False(vm.Older.CanExecute(null)); Assert.Null(vm.SelectedTime); Assert.NotEqual("Latest", vm.TimeLabel);
    }

    [Theory]
    [InlineData(RepositoryReadiness.Missing)]
    [InlineData(RepositoryReadiness.Unavailable)]
    [InlineData(RepositoryReadiness.RecoveryRequired)]
    [InlineData(RepositoryReadiness.Invalid)]
    public async Task NonReadyOwnerClearsRetainedHistoryAndDisablesItsCommands(RepositoryReadiness readiness)
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath); var owner = Ready(f, browser);
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        await vm.SelectChildrenAsync([vm.Children[0]]); vm.SelectedVersion = vm.Versions[0];
        owner.HistoryRevision++; Assert.True(vm.NeedsRefresh);
        var releases = owner.PreviewReleases;
        if (readiness == RepositoryReadiness.Missing) owner.Settings = AppSettings.Empty;
        owner.Inspection = new(readiness); owner.Browser = null;
        await ActivateSourceAsync(vm);
        AssertHistoryCleared(vm); Assert.True(owner.PreviewReleases > releases); Assert.False(vm.IsConfigured);
        Assert.False(vm.Refresh.CanExecute(null)); Assert.NotEmpty(vm.Message);
        await vm.ChangeTimeAsync(DateTimeOffset.UtcNow); await vm.ChangeFolderAsync("old");
        AssertHistoryCleared(vm);
    }

    [Fact]
    public async Task RepositoryReplacementClearsOldRowsBeforeTheNewCaptureCanComplete()
    {
        using var a = new ApplicationAdapterTests.Fixture(); a.Write("only-A", "a"); a.Scan();
        using var b = new ApplicationAdapterTests.Fixture(); b.Write("only-B", "b"); b.Scan();
        await using var browserA = await HistoryBrowserSession.OpenAsync(a.RepositoryPath);
        await using var browserB = await HistoryBrowserSession.OpenAsync(b.RepositoryPath);
        var owner = Ready(a, browserA); var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm);
        await vm.SelectChildrenAsync([vm.Children[0]]);
        owner.Settings = owner.Settings with { RepositoryPath = b.RepositoryPath }; owner.Browser = browserB; owner.Notify();
        AssertHistoryCleared(vm);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        browserB.QueryEntered = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var activation = ActivateSourceAsync(vm); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Empty(vm.Children); Assert.Empty(vm.Versions); Assert.False(vm.Preview.CanExecute(null));
        release.Set(); await activation;
        Assert.Equal("only-B", Assert.Single(vm.Children).Name); Assert.Empty(vm.Versions);
        Assert.DoesNotContain(vm.Roots, r => r.Id == a.Root);
    }

    [Fact]
    public async Task PublishedHistoryDisablesAllExactAndRestoreActionsUntilExplicitRefresh()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath); var owner = Ready(f, browser);
        owner.Settings = owner.Settings with { Sources = [SourceMapping.New(f.Source) with { SourceRootId = f.Root }] };
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm); await vm.SelectChildrenAsync([vm.Children[0]]);
        var commands = new[] { vm.Preview, vm.RestoreExact, vm.RestoreSelected, vm.RestoreOriginal };
        Assert.All(commands, c => Assert.True(c.CanExecute(null)));
        f.Write("b", "b"); f.Scan(); owner.HistoryRevision++; owner.Notify();
        Assert.True(vm.NeedsRefresh); Assert.Single(vm.Children); Assert.All(commands, c => Assert.False(c.CanExecute(null)));
        await vm.Refresh.ExecuteAsync(); Assert.False(vm.NeedsRefresh); Assert.Equal(2, vm.Children.Count);
        await vm.SelectChildrenAsync([vm.Children[0]]); Assert.All(commands, c => Assert.True(c.CanExecute(null)));
    }

    [Fact]
    public async Task UnselectAllRequestsNativeUnselectionReleasesPreviewAndAllowsImmediateReselection()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("a", "a"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath); var owner = Ready(f, browser);
        var vm = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(vm); var row = vm.Children[0];
        await vm.SelectChildrenAsync([row]); vm.SelectedVersion = vm.Versions[0]; var releases = owner.PreviewReleases;
        var requests = 0; vm.SelectionCleared += () => { requests++; Assert.Empty(vm.Versions); Assert.Null(vm.SelectedVersion); };
        await vm.Clear.ExecuteAsync(); Assert.Equal(1, requests); Assert.True(owner.PreviewReleases > releases);
        Assert.False(vm.HasFileSelection); Assert.False(vm.Clear.CanExecute(null)); Assert.False(vm.Preview.CanExecute(null));
        await vm.SelectChildrenAsync([row]); Assert.True(vm.HasFileSelection); Assert.Single(vm.Versions); Assert.True(vm.Preview.CanExecute(null));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        browser.QueryEntered = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var pending = vm.SelectChildrenAsync([row]); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await vm.Clear.ExecuteAsync(); release.Set(); await pending;
        Assert.Empty(vm.Versions); Assert.False(vm.Busy); Assert.False(vm.HasFileSelection);
    }

    [Fact]
    public async Task VirtualRootIncludesEmptyActiveAndHistoricalInactiveRootsWithDistinctDuplicateLabels()
    {
        using var f = new HistoryFixture(); var active = Guid.NewGuid(); var empty = Guid.NewGuid(); var removed = Guid.NewGuid();
        f.Add("folder/a.txt", root: active); f.Add("old.txt");
        using (var reader = f.Repository.OpenReader()) using (var writer = f.Repository.OpenWriter())
            writer.Apply(new(sourceRoots: [new(removed, f.Temp.Child("never-backed-up"), false)]), reader.Authority);
        var activePath = f.Temp.Child("one/Projects"); var inactivePath = f.Temp.Child("two/Projects");
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources =
            [SourceMapping.New(activePath) with { SourceRootId = active }, SourceMapping.New(f.Temp.Child("Empty")) with { SourceRootId = empty },
             SourceMapping.New(inactivePath) with { SourceRootId = HistoryFixture.Root, IsProtected = false, RuntimePath = null }] } };
        var vm = new HistoryViewModel(owner, new Dialogs()); await vm.ActivateAsync();
        Assert.True(vm.IsAtVirtualRoot); Assert.False(vm.Parent.CanExecute(null)); Assert.Same(vm.Roots, vm.Items); Assert.Equal(3, vm.Roots.Count);
        Assert.DoesNotContain(vm.Roots, r => r.Id == removed); var inactive = Assert.Single(vm.Roots, r => r.Id == HistoryFixture.Root);
        Assert.False(inactive.IsActive); Assert.True(inactive.HasHistory); Assert.Contains("Not currently backed up", inactive.Description);
        var duplicate = vm.Roots.Where(r => r.Label == "Projects").ToArray(); Assert.Equal(2, duplicate.Length);
        Assert.All(duplicate, r => Assert.True(r.Disambiguate)); Assert.NotEqual(duplicate[0].Description, duplicate[1].Description);
        await vm.SelectItemsAsync([inactive]); Assert.False(vm.RestoreSelected.CanExecute(null)); Assert.Empty(vm.Versions);
        await vm.OpenItemAsync(vm.Roots.Single(r => r.Id == empty)); Assert.Empty(vm.Children); Assert.Contains("no saved versions yet", vm.EmptyMessage);
        await vm.Parent.ExecuteAsync(); Assert.True(vm.IsAtVirtualRoot); Assert.Single(vm.Breadcrumbs); Assert.True(vm.Breadcrumbs[0].VirtualRoot);
    }

    [Fact]
    public async Task ParentAndViewPreferencesPreserveAsOfCaptureAndSelectedVersion()
    {
        using var f = new HistoryFixture(); f.Add("folder/a.txt", day: 0); f.Add("folder/a.txt", content: "later", day: 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var vm = new HistoryViewModel(owner, new Dialogs()); await vm.ActivateAsync(); var reader = browser.ReaderForTests;
        await vm.ChangeTimeAsync(HistoryFixture.Time); await vm.OpenItemAsync(vm.Roots[0]); await vm.OpenItemAsync(vm.Children[0]);
        Assert.Equal(new[] { "Timeline", vm.CurrentRoot!.Label, "folder" }, vm.Breadcrumbs.Select(b => b.Label));
        await vm.SelectChildrenAsync([vm.Children[0]]); vm.SelectedVersion = vm.Versions[0];
        var child = vm.Children[0]; var version = vm.SelectedVersion; var queries = 0; browser.QueryEntered = () => queries++;
        await vm.SetViewModeAsync(TimelineViewMode.List); await new SettingsViewModel(owner).SetAppearanceAsync(AppearancePreference.Dark);
        Assert.True(vm.IsList); Assert.Equal(AppearancePreference.Dark, owner.Preferences.Appearance); Assert.Equal(0, queries);
        Assert.Same(child, Assert.Single(vm.SelectedItems)); Assert.Same(version, vm.SelectedVersion); Assert.Same(reader, browser.ReaderForTests);
        Assert.Equal(HistoryFixture.Time, vm.AtUtc); await vm.SetViewModeAsync(TimelineViewMode.LargeIcons); Assert.True(vm.IsLargeIcons);
        await vm.Parent.ExecuteAsync(); Assert.Equal(2, vm.Breadcrumbs.Count); Assert.False(vm.IsAtVirtualRoot);
        await vm.Parent.ExecuteAsync(); Assert.True(vm.IsAtVirtualRoot); Assert.Equal(HistoryFixture.Time, vm.AtUtc); Assert.Same(reader, browser.ReaderForTests);
        Assert.False(vm.Parent.CanExecute(null)); Assert.Equal(0, owner.Saves);
    }

    [Fact]
    public async Task LiveLanguageChoicePreservesDraftAndExactTimelineSelection()
    {
        using var f = new HistoryFixture(); f.Add("folder/a.txt", day: 0); f.Add("folder/a.txt", content: "later", day: 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var strings = new MutableStrings();
        var owner = new Context { Strings = strings, Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var config = new ConfigurationViewModel(owner, new Dialogs()); config.IntervalMinutes = 17;
        var history = new HistoryViewModel(owner, new Dialogs()); var settings = new SettingsViewModel(owner);
        await history.ActivateAsync(); await history.ChangeTimeAsync(HistoryFixture.Time);
        await history.OpenItemAsync(history.Roots[0]); await history.OpenItemAsync(history.Children[0]);
        await history.SelectChildrenAsync([history.Children[0]]); history.SelectedVersion = history.Versions[0];
        var child = history.Children[0]; var version = history.SelectedVersion; var root = history.CurrentRoot;
        var reader = browser.ReaderForTests; var queries = 0; browser.QueryEntered = () => queries++;
        var notifications = 0; child.PropertyChanged += (_, _) => notifications++;
        owner.LanguageSelection = strings.Select;
        Assert.Equal(45, settings.Languages.Count); Assert.Null(settings.SelectedLanguage.Value);
        await settings.SetAppearanceAsync(AppearancePreference.Dark); await history.SetViewModeAsync(TimelineViewMode.List);
        await settings.SetLanguageAsync("fi-FI");
        Assert.Equal("fi-FI", settings.SelectedLanguage.Value); Assert.Equal("fi-FI", owner.Strings.Language);
        Assert.Equal(new TestAppStrings("fi-FI")[TextId.Language_UseWindows], settings.Languages[0].Label);
        Assert.Equal(17, config.IntervalMinutes); Assert.True(config.IsDirty);
        Assert.Same(root, history.CurrentRoot); Assert.Same(child, Assert.Single(history.SelectedItems)); Assert.Same(version, history.SelectedVersion);
        Assert.Same(reader, browser.ReaderForTests); Assert.Equal(HistoryFixture.Time, history.AtUtc); Assert.Equal("folder", history.Breadcrumbs[^1].Folder);
        Assert.Equal(0, queries); Assert.True(notifications > 0); Assert.Equal(0, owner.Saves);
        Assert.Equal(AppearancePreference.Dark, owner.Preferences.Appearance); Assert.True(history.IsList);
        owner.FailPreferencesSave = true;
        await Assert.ThrowsAsync<IOException>(() => settings.SetLanguageAsync("ar-SA"));
        Assert.Equal("fi-FI", owner.Preferences.Language); Assert.Equal("fi-FI", owner.Strings.Language);
        owner.FailPreferencesSave = false; await settings.SetLanguageAsync(null);
        Assert.Null(settings.SelectedLanguage.Value); Assert.Equal("en-US", owner.Strings.Language);
    }

    private sealed class MutableStrings : IAppStrings
    {
        private IAppStrings _current = TestAppStrings.English;
        public string Language => _current.Language;
        public System.Globalization.CultureInfo FormatCulture => _current.FormatCulture;
        public string this[TextId id] => _current[id];
        public void Select(string? language) => _current = new TestAppStrings(language ?? "en-US");
    }

    [Fact]
    public async Task LiveNativeSelectionIsCapturedBeforeStatusNotificationsCanSynchronizeIt()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("file.txt", "saved"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var vm = new HistoryViewModel(Ready(f, browser), new Dialogs()); await ActivateSourceAsync(vm);
        var row = vm.Children[0]; var liveSelection = new List<ChildRow> { row };
        vm.PropertyChanged += (_, _) => liveSelection.Clear();
        await vm.SelectChildrenAsync(liveSelection);
        Assert.Same(row, Assert.Single(vm.SelectedItems)); Assert.Single(vm.Versions); Assert.True(vm.HasFileSelection);
    }

    [Fact]
    public void OverviewSourceProblemsShowBoundedPathsAndReasonsInsteadOfOnlyACount()
    {
        var mapping = SourceMapping.New(Path.GetFullPath("source")); var scan = new ScanResult { Outcome = ScanOutcome.Completed };
        for (var i = 0; i < 40; i++) scan.Skip(mapping.SourceRootId, $"folder/{i}.txt",
            new IOException("Developer detail\r\ncontinued", new System.ComponentModel.Win32Exception(5)), SourceProblemKind.Access);
        var now = DateTimeOffset.UtcNow; var attempt = new AttemptSnapshot(AttemptTrigger.Manual, null, now, now, TimeSpan.Zero, BackupAttemptResult.FromScan(scan));
        var owner = new Context { Settings = AppSettings.Empty with { Sources = [mapping] }, Status = new(1, SchedulerLifecycle.Idle, true,
            new(TimeSpan.FromHours(1), now), now.AddHours(1), false, false, null, null, new(BackupPhase.Finished), attempt, now, 0, SafeguardDecision.Allow, null) };
        var vm = new OverviewViewModel(owner, new Dialogs()); Assert.True(vm.HasSourceProblems);
        Assert.Contains(Path.Combine(mapping.RuntimePath!, "folder", "0.txt"), vm.SourceProblems);
        Assert.Contains(owner.Strings[TextId.SourceProblem_AccessDenied], vm.SourceProblems); Assert.DoesNotContain("Developer detail", vm.SourceProblems);
        Assert.Contains(owner.Strings.Format(TextId.Overview_ProblemsLimited, 32, 40), vm.SourceProblems);
        Assert.DoesNotContain(Path.Combine("folder", "39.txt"), vm.SourceProblems); Assert.Equal(owner.Strings[TextId.Overview_SourceProblemsAdvice], vm.Attention);
    }

    private static async Task ActivateSourceAsync(HistoryViewModel vm)
    {
        await vm.ActivateAsync();
        if (vm.IsAtVirtualRoot && vm.Roots.FirstOrDefault() is { } root) await vm.ChangeRootAsync(root);
    }

    private static void AssertHistoryCleared(HistoryViewModel vm)
    {
        Assert.Empty(vm.Roots); Assert.Empty(vm.Breadcrumbs); Assert.Empty(vm.Children); Assert.Empty(vm.Versions);
        Assert.Single(vm.Times); Assert.Null(vm.Times[0].Utc); Assert.Equal("Latest", vm.TimeLabel);
        Assert.Null(vm.CurrentRoot); Assert.Null(vm.SelectedVersion); Assert.False(vm.HasFileSelection); Assert.False(vm.NeedsRefresh);
        Assert.False(vm.Busy); Assert.False(vm.CanNavigate);
        Assert.All(new[] { vm.Latest, vm.Older, vm.Newer, vm.LoadChildren, vm.LoadTimes, vm.LoadVersions, vm.Clear,
            vm.Preview, vm.RestoreExact, vm.RestoreSelected, vm.RestoreOriginal }, c => Assert.False(c.CanExecute(null)));
    }

    [Fact]
    public void DiagnosticsAreBoundedAndDoNotIncludeControlCharactersFromPaths()
    {
        var owner = new Context { BrowserActivity = "folder\r\nspoof", LastOutcome = "safe" };
        var vm = new DiagnosticsViewModel(owner, new Dialogs()); Assert.Contains("folder  spoof", vm.Summary); Assert.True(vm.Summary.Length <= 24000);
    }

    [Fact]
    public async Task ShellFocusNavigatesExactFileBeyondFirstPageAndKeepsLatest()
    {
        using var f = new ApplicationAdapterTests.Fixture();
        for (var i = 0; i < 140; i++) f.Write($"Folder/{i:D3}.txt", "saved");
        f.Scan(); await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); owner.Settings = owner.Settings with { Sources = [SourceMapping.New(f.Source) with { SourceRootId = f.Root }] };
        var model = new HistoryViewModel(owner, new Dialogs());
        await model.ActivateAsync();
        await model.OpenShellAsync(new(Path.Combine(f.Source, "Folder", "139.txt"), ShellItemKind.File), () => true);
        Assert.Equal(f.Root, model.CurrentRoot!.Id); Assert.Null(model.AtUtc); Assert.True(model.HasFileSelection);
        Assert.Equal("139.txt", model.SelectedItems.Single().Name); Assert.Single(model.Versions);
        await model.LoadChildren.ExecuteAsync(); Assert.Single(model.Children, c => c.Name == "139.txt");
        await model.OpenShellAsync(new(f.Temp.Child("unprotected"), ShellItemKind.File), () => true);
        Assert.Contains("not currently backed up", model.Message);
    }

    private static Context Ready(ApplicationAdapterTests.Fixture f, HistoryBrowserSession browser) => new()
    { Settings = AppSettings.Empty with { RepositoryPath = f.RepositoryPath }, Inspection = new(RepositoryReadiness.Ready), Browser = browser };

    private sealed class Dialogs : IDesktopDialogs
    {
        public IAppStrings Strings { get; set; } = TestAppStrings.English;
        public string? NextFolder { get; init; }
        public bool Confirm { get; set; } = true;
        public List<string> Notices { get; } = [];
        public Task<string?> FolderAsync() => Task.FromResult(NextFolder);
        public Task<char[]?> PasswordAsync(bool creation) => Task.FromResult<char[]?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string primary) => Task.FromResult(Confirm);
        public Task NoticeAsync(string title, string message) { Notices.Add(message); return Task.CompletedTask; }
        public Task<bool> ConfirmLocationAsync(BackupLocationConfirmation confirmation) => Task.FromResult(Confirm);
        public void Navigate(string page) { }
        public string DiagnosticsHeader => "Test preview";
        public string SettingsPath => "settings-v1.json";
        public void CopyText(string text) { }
    }
    private sealed class Context : IDesktopContext
    {
        public IAppStrings Strings { get; set; } = TestAppStrings.English;
        private CancellationTokenSource? _query;
        public AppSettings Settings { get; set; } = AppSettings.Empty;
        public ApplicationPreferences Preferences { get; set; } = ApplicationPreferences.Default;
        public UpdateSnapshot? Updates { get; set; }
        public int UpdateChecks { get; private set; }
        public List<bool> UpdateInstalls { get; } = [];
        public Task SaveUpdatePreferencesAsync(UpdatePreferences preferences, CancellationToken token)
        { Updates = (Updates ?? new(null, UpdatePreferences.Default, null, UpdatePhase.Idle, null)) with { Preferences = preferences }; Notify(); return Task.CompletedTask; }
        public Task CheckUpdatesAsync(CancellationToken token) { UpdateChecks++; return Task.CompletedTask; }
        public Task InstallUpdateAsync(bool cancelBackup, CancellationToken token) { UpdateInstalls.Add(cancelBackup); return Task.CompletedTask; }
        public ISourceFolderPolicy FolderPolicy { get; set; } = SourceFolderPolicy.Empty;
        public Func<string, CancellationToken, Task<CapacityObservation?>>? DriveObserver { get; init; }
        public Func<string, CancellationToken, Task<long?>>? BackupObserver { get; init; }
        public Task<CapacityObservation?> ObserveDriveSpaceAsync(string path, CancellationToken token) => DriveObserver?.Invoke(path, token) ?? Task.FromResult<CapacityObservation?>(null);
        public Task<long?> ObserveBackupSizeAsync(string path, CancellationToken token) => BackupObserver?.Invoke(path, token) ?? Task.FromResult<long?>(null);
        public Func<CancellationToken, Task<ExperimentalRecoveryState>>? SecurityInspector { get; set; }
        public Task<ExperimentalRecoveryState> InspectSecurityAsync(CancellationToken token) => SecurityInspector?.Invoke(token) ?? Task.FromResult(ExperimentalRecoveryState.Off);
        public Func<RepositorySecurityOperation, SecurityOperationResult>? SecurityExecutor { get; set; }
        public Task<SecurityOperationResult> ExecuteSecurityAsync(RepositorySecurityOperation operation, char[]? password, CancellationToken token)
            => Task.FromResult(SecurityExecutor?.Invoke(operation) ?? throw new NotSupportedException());
        public ExactVersionRemovalService? VersionRemoval { get; set; }
        public BrowserVersion? RestoredVersion { get; private set; }
        public BrowserVersion? PreviewedVersion { get; private set; }
        public TaskCompletionSource? PreviewCompletion { get; set; }
        public bool RestoredOriginal { get; private set; }
        public string? RestoreDestination { get; private set; }
        public Action<string?>? LanguageSelection { get; set; }
        public bool FailPreferencesSave { get; set; }
        public Task SavePreferencesAsync(ApplicationPreferences preferences, CancellationToken token)
        {
            if (FailPreferencesSave) throw new IOException("Preference save failed.");
            preferences.Validate(); Preferences = preferences; LanguageSelection?.Invoke(preferences.Language); Notify(); return Task.CompletedTask;
        }
        public RepositoryInspection Inspection { get; set; } = new(RepositoryReadiness.Ready);
        public BackupStatus? Status { get; set; }
        public LastBackupStatus? SavedBackupStatus { get; set; }
        public HistoryBrowserSession? Browser { get; set; }
        public ApplicationFailure? BrowserFailure { get; set; }
        public string LastOutcome { get; set; } = "";
        public string BrowserActivity { get; set; } = "";
        public string CurrentPage { get; set; } = "Overview";
        public ApplicationFailure? LatestFailure => null;
        public bool HasError => false;
        public bool ConfigurationNeedsReset => false;
        public long HistoryRevision { get; set; }
        public int Saves { get; private set; }
        public int PreviewReleases { get; private set; }
        public event Action? Changed;
        public Task RunAsync(Func<CancellationToken, Task> command) => command(CancellationToken.None);
        public Task SaveAsync(AppSettings settings, CancellationToken token, bool reset = false) { Settings = settings; Saves++; Notify(); return Task.CompletedTask; }
        public Task InspectAndComposeAsync(CancellationToken token) => Task.CompletedTask;
        public async Task WithBrowserAsync(Func<HistoryBrowserSession, CancellationToken, Task> action, CancellationToken token)
        {
            if (BrowserFailure is { } failure) throw new ApplicationOperationException(failure);
            using var query = CancellationTokenSource.CreateLinkedTokenSource(token); _query = query;
            try { await action(Browser!, query.Token); } finally { if (ReferenceEquals(_query, query)) _query = null; }
        }
        public Task RecoverAsync(char[] password, CancellationToken token) { Array.Clear(password); return Task.CompletedTask; }
        public Task PreviewAsync(BrowserVersion version, CancellationToken token)
        { PreviewedVersion = version; return PreviewCompletion?.Task ?? Task.CompletedTask; }
        public Func<BrowserSelection, string?, Task>? RestoreSelection { get; set; }
        public Task RestoreSelectionAsync(BrowserSelection selection, string? destination, Guid root, bool original, CancellationToken token)
            => RestoreSelection?.Invoke(selection, destination) ?? Task.CompletedTask;
        public Task RestoreExactVersionAsync(BrowserVersion version, string? destination, bool original, CancellationToken token)
        { RestoredVersion = version; RestoredOriginal = original; RestoreDestination = destination; return Task.CompletedTask; }
        public void TimelineChanged() { HistoryRevision++; Notify(); }
        public void BackUpNow() { }
        public void StartScheduling() { }
        public void StopScheduling() { }
        public Task CancelBackupAsync() => Task.CompletedTask;
        public void CancelHistory() => _query?.Cancel();
        public void ReleasePreview() => PreviewReleases++;
        public void Notify() => Changed?.Invoke();
    }
}
