using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Fact]
    public void SavedStatusIsDisplayFallbackLiveAttemptWinsAndUnavailableStatusHasNoPlaceholder()
    {
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = "backup" }, Status = StatusForUi(false) };
        var model = new OverviewViewModel(owner, new Dialogs()); Assert.Empty(model.CurrentBackup); Assert.False(model.HasCurrentBackup);
        Assert.Equal("Automatic backups are enabled", model.Schedule);
        owner.Settings = owner.Settings with { SchedulingEnabled = false }; Assert.Equal("Automatic backups are disabled", model.Schedule);
        owner.SavedBackupStatus = new(1, Guid.NewGuid(), new string('A',64), FoundationTests.Time, TimeSpan.FromMinutes(12).Ticks, BackupOutcome.Completed, 2, 1);
        Assert.True(model.HasCurrentBackup); Assert.StartsWith("Last backed up on ", model.CurrentBackup);
        Assert.Contains("took 12 minutes", model.CurrentBackup); Assert.Equal("2 new versions saved", model.Counts); Assert.StartsWith("1 file problem · ", model.ResultDetail);
        owner.Status = owner.Status with { IsAttemptActive = true }; Assert.Equal("Backup running", model.CurrentBackup); Assert.Empty(model.Counts);
        owner.Status = owner.Status with { IsAttemptActive = false, LastAttempt = new(AttemptTrigger.Manual, null, FoundationTests.Time, FoundationTests.Time, TimeSpan.Zero, new(BackupOutcome.Cancelled)) };
        Assert.Contains("Backup cancelled", model.CurrentBackup); Assert.Empty(model.Counts); Assert.DoesNotContain("file problem", model.ResultDetail);
    }
    [Theory]
    [InlineData(BackupPhase.CheckingSafeguards, "Checking backup conditions…")]
    [InlineData(BackupPhase.OpeningRepository, "Opening backup…")]
    [InlineData(BackupPhase.Scanning, "Scanning and backing up files…")]
    [InlineData(BackupPhase.Maintaining, "Applying retention policy…")]
    [InlineData(BackupPhase.CheckingMetadata, "Checking metadata…")]
    [InlineData(BackupPhase.CompactingMetadata, "Compacting metadata…")]
    [InlineData(BackupPhase.CleaningMetadata, "Cleaning up metadata…")]
    [InlineData(BackupPhase.Finished, "Finishing backup…")]
    public void ActiveStatusUsesTruthfulPhaseAndOnlyRealDenominators(BackupPhase phase, string text)
    {
        var owner = new Context { Status = StatusForUi(true) with { Progress = new(phase) } };
        var model = new OverviewViewModel(owner, new Dialogs());
        Assert.Equal(text, model.CurrentPhase); Assert.True(model.IsProgressIndeterminate);
        Assert.Empty(model.Counts); Assert.Empty(model.ResultDetail);
        owner.Status = owner.Status with { Progress = new(phase, 2, 4) };
        Assert.False(model.IsProgressIndeterminate); Assert.Equal(50, model.ProgressPercent);
        owner.Status = owner.Status with { IsAttemptActive = false };
        Assert.Empty(model.CurrentPhase); Assert.False(model.IsActive); Assert.False(model.IsProgressIndeterminate); Assert.False(model.HasCompletedCounts); Assert.Equal(0, model.ProgressPercent);
    }

    [Theory]
    [InlineData(0, "0 new versions saved", "0 file problems")]
    [InlineData(1, "1 new version saved", "1 file problem")]
    [InlineData(3, "3 new versions saved", "3 file problems")]
    public async Task CompletedStatusCombinesProblemsAndMeasuredSize(long count, string versions, string problems)
    {
        var scan = new ScanResult { Outcome = ScanOutcome.Completed, VersionsCreated = count, SourceSkips = count };
        var now = DateTimeOffset.UtcNow;
        var owner = new Context { Status = StatusForUi(false) with { LastAttempt = new(AttemptTrigger.Manual, null, now, now, TimeSpan.Zero, BackupAttemptResult.FromScan(scan)) },
            BackupObserver = (_, _) => Task.FromResult<long?>(20) };
        var model = new OverviewViewModel(owner, new Dialogs());
        await model.Storage.ActivateAsync("backup");
        Assert.Equal(versions, model.Counts); Assert.Equal(problems + " · Backup occupies 20 bytes", model.ResultDetail);
        owner.Status = owner.Status with { LastAttempt = owner.Status.LastAttempt with { Result = owner.Status.LastAttempt.Result with { Outcome = BackupOutcome.Cancelled } } };
        Assert.Empty(model.Counts); Assert.DoesNotContain("file problem", model.ResultDetail);
        model.Storage.Deactivate();
    }

    [Fact]
    public async Task StorageRefreshesOnCompletionAndLogicalMaintenanceButNotRepeatedSnapshots()
    {
        var drives = 0; var sizes = 0; var now = DateTimeOffset.UtcNow;
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = "backup" }, Status = StatusForUi(false),
            DriveObserver = (_, _) => { drives++; return Task.FromResult<CapacityObservation?>(new(1000, 500)); },
            BackupObserver = (_, _) => { sizes++; return Task.FromResult<long?>(sizes * 10); } };
        var overview = new OverviewViewModel(owner, new Dialogs());
        var configuration = new ConfigurationViewModel(owner, new Dialogs());
        await overview.Storage.ActivateAsync("backup"); await configuration.Storage.ActivateAsync("backup");
        Assert.Equal(2, drives); Assert.Equal(1, sizes);
        owner.Status = owner.Status with { IsAttemptActive = true }; owner.Notify();
        for (var i = 0; i < 20; i++) owner.Notify();
        Assert.Equal(2, drives); Assert.Equal(1, sizes);
        owner.Status = owner.Status with { IsAttemptActive = false, LastAttempt = new(AttemptTrigger.Manual, null, now, now.AddSeconds(1), TimeSpan.FromSeconds(1), new(BackupOutcome.Completed)) };
        owner.HistoryRevision++; owner.Notify();
        Assert.Equal(4, drives); Assert.Equal(2, sizes);
        for (var i = 0; i < 20; i++) owner.Notify();
        Assert.Equal(4, drives); Assert.Equal(2, sizes);
        owner.HistoryRevision++; owner.Notify(); Assert.Equal(6, drives); Assert.Equal(3, sizes);
        overview.Storage.Deactivate(); configuration.Storage.Deactivate();
        owner.HistoryRevision++; owner.Notify(); Assert.Equal(6, drives);
    }

    [Fact]
    public async Task VisibleActiveDrivePollingIsSerializedAndNeverRecursivelyMeasuresSize()
    {
        var time = new ManualSchedulerTime(DateTimeOffset.UtcNow);
        var calls = 0; var sizes = 0; var slow = new TaskCompletionSource<CapacityObservation?>();
        var owner = new Context { Status = StatusForUi(true),
            DriveObserver = (_, _) => Interlocked.Increment(ref calls) == 2 ? slow.Task : Task.FromResult<CapacityObservation?>(new(1000, 100)),
            BackupObserver = (_, _) => { sizes++; return Task.FromResult<long?>(20); } };
        var model = new StorageUsageViewModel(owner, true, time);
        await model.ActivateAsync("backup"); Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(2)); await Eventually(() => calls == 2);
        time.Advance(TimeSpan.FromSeconds(20)); Assert.Equal(2, calls); Assert.Equal(1, sizes);
        slow.SetResult(new(1000, 200)); await Eventually(() => !model.DriveSpace.StartsWith("Checking", StringComparison.Ordinal));
        model.Deactivate(); time.Advance(TimeSpan.FromSeconds(20)); Assert.Equal(2, calls); Assert.Equal(1, sizes);
    }

    [Fact]
    public async Task TimelineRefreshesOnNavigationWithoutReusingStaleExactSelections()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("folder/a.txt", "one"); f.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = Ready(f, browser); var model = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(model);
        var capture = browser.State.CaptureId; var folder = model.Children.Single();
        f.Write("folder/b.txt", "two"); f.Scan(); owner.HistoryRevision++; owner.Notify();
        Assert.True(model.NeedsRefresh); Assert.False(model.ShowRefreshNotice); Assert.Same(folder, Assert.Single(model.Children));
        await model.OpenFolderAsync(folder);
        Assert.NotEqual(capture, browser.State.CaptureId); Assert.False(model.NeedsRefresh);
        Assert.Equal(new[] { "a.txt", "b.txt" }, model.Children.Select(c => c.Name));
        await model.SelectChildrenAsync([model.Children[0]]); var selected = model.SelectedVersion;
        owner.HistoryRevision++; owner.Notify(); Assert.False(model.Preview.CanExecute(null)); Assert.False(model.RestoreExact.CanExecute(null));
        await model.ActivateAsync(); Assert.Empty(model.SelectedItems); Assert.Empty(model.Versions); Assert.Null(model.SelectedVersion);
        Assert.False(model.SelectContextVersion(selected!));
        Assert.Equal("folder", model.Breadcrumbs.Last().Folder);
    }

    [Fact]
    public async Task TimelinePageReentryPreservesExplicitTimeRootAndFolder()
    {
        using var f = new HistoryFixture(); f.Add("folder/a.txt", day: 0); f.Add("folder/a.txt", day: 1);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var model = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(model); await model.OpenFolderAsync(model.Children[0]);
        await model.ChangeTimeAsync(HistoryFixture.Time); var root = model.CurrentRoot!.Id; var capture = browser.State.CaptureId;
        model.Deactivate(); f.Add("folder/b.txt", day: 2); owner.HistoryRevision++; owner.Notify();
        await model.ActivateAsync();
        Assert.NotEqual(capture, browser.State.CaptureId); Assert.Equal(root, model.CurrentRoot!.Id);
        Assert.Equal(HistoryFixture.Time, model.AtUtc); Assert.Equal("folder", model.Breadcrumbs.Last().Folder);
        Assert.Single(model.Children); Assert.Equal("a.txt", model.Children[0].Name);
    }

    [Fact]
    public async Task TimelineFailedSilentRefreshStopsLoadingAndLeavesNeutralRetryState()
    {
        using var f = new HistoryFixture(); f.Add();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var model = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(model);
        owner.HistoryRevision++; owner.BrowserFailure = ApplicationFailure.Unavailable; owner.Notify();
        await model.ActivateAsync(); Assert.False(model.Busy); Assert.False(model.CanNavigate);
        Assert.NotEmpty(model.Message); Assert.False(model.Preview.CanExecute(null)); Assert.False(model.RestoreExact.CanExecute(null));
        owner.BrowserFailure = null; await model.ActivateAsync(); Assert.True(model.CanNavigate); Assert.False(model.Busy);
    }

    [Fact]
    public async Task PhysicalCompactionAloneDoesNotTriggerSilentTimelineRecapture()
    {
        using var f = new HistoryFixture(); f.Add();
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var model = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(model); var capture = browser.State.CaptureId;
        using (var writer = f.Repository.OpenWriter()) writer.CompactMetadata();
        owner.Notify(); await model.ActivateAsync(); Assert.Equal(capture, browser.State.CaptureId);
        Assert.False(model.NeedsRefresh); Assert.False(model.ShowRefreshNotice);
    }

    [Fact]
    public async Task StaleSingleTimestampScopeAllowsNaturalEarlierNavigationToRefreshFirst()
    {
        using var f = new HistoryFixture(); f.Add(day: 0);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.Temp.Child());
        var owner = new Context { Browser = browser, Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() } };
        var model = new HistoryViewModel(owner, new Dialogs()); await ActivateSourceAsync(model); Assert.False(model.Older.CanExecute(null));
        f.Add(day: 1); owner.HistoryRevision++; owner.Notify(); Assert.True(model.Older.CanExecute(null)); Assert.False(model.Newer.CanExecute(null));
        await model.Older.ExecuteAsync(); Assert.Equal(HistoryFixture.Time, model.AtUtc); Assert.False(model.NeedsRefresh);
    }

    private static BackupStatus StatusForUi(bool active) => new(1, SchedulerLifecycle.Stopped, false,
        new(TimeSpan.FromHours(1), DateTimeOffset.UtcNow), null, active, false, null, active ? AttemptTrigger.Manual : null,
        new(active ? BackupPhase.Scanning : BackupPhase.None), null, null, 0, SafeguardDecision.Allow, null);

    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(1, deadline.Token); }
    }
}
