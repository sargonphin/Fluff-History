using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class RetentionOrchestrationTests
{
    private static DateTimeOffset Utc => FoundationTests.Time.AddDays(1000);
    private static BackupConfiguration Configuration(HistoryFixture f, RetentionPolicy policy) => new(f.Temp.Child(),
        [new(new(HistoryFixture.Root, f.Temp.Child("source")), f.Temp.Child("source"))], retentionPolicy: policy);

    [Theory]
    [InlineData(RetentionPolicy.Forever)] [InlineData(RetentionPolicy.UntilSaturation)]
    public void NonAgePoliciesNeverRunAutomaticAgePass(RetentionPolicy policy)
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var before = TemporaryDirectory.Snapshot(f.Temp.Child());
        var result = new AutomaticRetention().Run(f.Repository, Configuration(f, policy), new() { Outcome = ScanOutcome.Completed }, Utc, default);
        Assert.Equal(MaintenanceState.NotNeeded, result.State); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Temp.Child()));
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [SourceMapping.New(f.Temp.Child("source"))], RetentionPolicyName = ProductConfiguration.RetentionPolicies.Single(p => p.Policy == policy).Name };
        Assert.Equal(policy, settings.ToBackupConfiguration().ScanOptions.Capacity!.Policy);
    }

    [Fact]
    public void AutomaticAgeUsesExactCurrentProtectionAndTransientDailyPolicyThrottle()
    {
        using var f = new HistoryFixture(); var old = f.Add(); var current = f.Add(day: 1); var ancient = f.Add("ancient-current", day: -100);
        var deleted = f.Add("deleted"); f.Remove("deleted");
        var auto = new AutomaticRetention(); var completed = new ScanResult { Outcome = ScanOutcome.Completed }; var config = Configuration(f, RetentionPolicy.OneMonth);
        var result = auto.Run(f.Repository, config, completed, Utc, default);
        Assert.Equal(MaintenanceState.Completed, result.State); Assert.Equal(2, result.Removed); Assert.True(result.ChangesMayExist);
        using (var reader = f.Repository.OpenReader())
        { Assert.Null(reader.GetVersion(old.Identity)); Assert.Null(reader.GetVersion(deleted.Identity)); Assert.NotNull(reader.GetVersion(current.Identity)); Assert.NotNull(reader.GetVersion(ancient.Identity)); }
        Assert.Equal(MaintenanceState.Throttled, auto.Run(f.Repository, config, completed, Utc.AddHours(1), default).State);
        Assert.Equal(MaintenanceState.Completed, auto.Run(f.Repository, config, completed, Utc.AddDays(1), default).State);
        Assert.Equal(MaintenanceState.Completed, auto.Run(f.Repository, Configuration(f, RetentionPolicy.ThreeMonths), completed, Utc.AddDays(1), default).State);
    }

    [Theory]
    [InlineData(ScanOutcome.Cancelled, false)] [InlineData(ScanOutcome.ControlError, true)]
    [InlineData(ScanOutcome.CapacityBlocked, false)] [InlineData(ScanOutcome.Completed, true)]
    public void CancelledPartialAndUncertainScansNeverAuthorizeAgeRetention(ScanOutcome outcome, bool uncertain)
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var before = TemporaryDirectory.Snapshot(f.Temp.Child());
        var scan = new ScanResult { Outcome = outcome, BatchesCommitted = 1, ReconciliationMayBeRequired = uncertain, Cancelled = outcome == ScanOutcome.Cancelled };
        var result = new AutomaticRetention().Run(f.Repository, Configuration(f, RetentionPolicy.OneMonth), scan, Utc, default);
        Assert.Equal(MaintenanceState.NotNeeded, result.State); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Temp.Child()));
    }

    [Fact]
    public void IncompleteAndStaleAutomaticMaintenanceReturnBoundedSafeAttention()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1); var complete = new ScanResult { Outcome = ScanOutcome.Completed };
        var limited = new AutomaticRetention { Options = new(MaximumExaminedVersions: 1) };
        Assert.Equal(MaintenanceState.Incomplete, limited.Run(f.Repository, Configuration(f, RetentionPolicy.OneMonth), complete, Utc, default).State);
        using (var reader = f.Repository.OpenReader()) Assert.NotNull(reader.GetVersion(old.Identity));
        var stale = new AutomaticRetention { PreviewCompleted = () => f.Add("authority-change") };
        Assert.Equal(MaintenanceState.Stale, stale.Run(f.Repository, Configuration(f, RetentionPolicy.OneMonth), complete, Utc, default).State);
        using var fresh = f.Repository.OpenReader(); Assert.NotNull(fresh.GetVersion(old.Identity));
    }

    [Fact]
    public async Task RealRunnerRunsAgeAfterCompletedScanAndKeepsBackupSuccessWhenMaintenanceCancels()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("file", "old");
        using (var repository = Repository.Open(f.RepositoryPath))
        {
            var scanner = new FileHistoryEngine(repository) { Clock = () => Utc.AddDays(-100) }; scanner.Scan([new(new(f.Root, f.Source), f.Source)]);
        }
        f.Write("file", "new contents");
        var config = new BackupConfiguration(f.RepositoryPath, [new(new(f.Root, f.Source), f.Source)], retentionPolicy: RetentionPolicy.OneMonth);
        var runner = new EngineBackupAttemptRunner { Clock = () => Utc };
        var result = await runner.RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.Completed, result.Outcome); Assert.Equal(1, result.Maintenance!.Removed);
        using var cancel = new CancellationTokenSource();
        var cancelledMaintenance = await new EngineBackupAttemptRunner { Clock = () => Utc }.RunAsync(config, AttemptTrigger.Manual,
            progress => { if (progress.Phase == BackupPhase.Maintaining) cancel.Cancel(); }, cancel.Token);
        Assert.Equal(BackupOutcome.Completed, cancelledMaintenance.Outcome); Assert.Equal(MaintenanceState.Cancelled, cancelledMaintenance.Maintenance!.State);
    }

    [Fact]
    public async Task CleanupPreviewIsReadOnlyIncompleteCannotExecuteAndForeignPreviewIsRejected()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var gate = new BackupOperationGate();
        var service = new TimelineMaintenanceService(gate); var before = TemporaryDirectory.Snapshot(f.Temp.Child());
        using (var writer = f.Repository.OpenWriter())
        {
            var preview = await service.PreviewAsync(f.Temp.Child(), HistoryCleanupMode.OlderThanOneMonth, Utc);
            Assert.True(preview.Complete); Assert.Equal(1, preview.Candidates); Assert.Equal(1, preview.ProtectedCurrent);
        }
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Temp.Child()));
        var incomplete = new TimelineMaintenanceService(gate) { Options = new(MaximumExaminedVersions: 1) };
        var partial = await incomplete.PreviewAsync(f.Temp.Child(), HistoryCleanupMode.OlderThanOneMonth, Utc); Assert.False(partial.Complete);
        Assert.Equal(ApplicationFailure.InvalidSelection, (await Assert.ThrowsAsync<ApplicationOperationException>(() => incomplete.ExecuteAsync(partial))).Failure);
        var valid = await service.PreviewAsync(f.Temp.Child(), HistoryCleanupMode.KeepOnlyMostRecent, Utc);
        Assert.Equal(ApplicationFailure.InvalidSelection, (await Assert.ThrowsAsync<ApplicationOperationException>(() => incomplete.ExecuteAsync(valid))).Failure);
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Temp.Child()));
    }

    [Theory]
    [InlineData(HistoryCleanupMode.OlderThanOneMonth)] [InlineData(HistoryCleanupMode.KeepOnlyMostRecent)]
    public async Task ManualCleanupUsesExactCleanupModesAndRejectsAuthorityChanges(HistoryCleanupMode mode)
    {
        using var f = new HistoryFixture(); var old = f.Add(); var latest = f.Add(day: 1);
        var service = new TimelineMaintenanceService(new()); var stale = await service.PreviewAsync(f.Temp.Child(), mode, Utc);
        f.Add("other"); var unchanged = TemporaryDirectory.Snapshot(f.Temp.Child());
        Assert.Equal(MaintenanceState.Stale, (await service.ExecuteAsync(stale)).State); Assert.Equal(unchanged, TemporaryDirectory.Snapshot(f.Temp.Child()));
        var preview = await service.PreviewAsync(f.Temp.Child(), mode, Utc); var result = await service.ExecuteAsync(preview);
        Assert.Equal(MaintenanceState.Completed, result.State); Assert.Equal(1, result.Removed);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(old.Identity)); Assert.NotNull(reader.GetVersion(latest.Identity));
    }

    [Fact]
    public async Task SharedApplicationGateExcludesManualWorkAndCancellationReleasesIt()
    {
        using var f = new HistoryFixture(); f.Add(); var gate = new BackupOperationGate(); var service = new TimelineMaintenanceService(gate);
        await gate.EnterAsync(default);
        try { Assert.Equal(ApplicationFailure.Busy, (await Assert.ThrowsAsync<ApplicationOperationException>(() => service.PreviewAsync(f.Temp.Child(), HistoryCleanupMode.KeepOnlyMostRecent, Utc))).Failure); }
        finally { gate.Exit(); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<ApplicationOperationException>(() => service.PreviewAsync(f.Temp.Child(), HistoryCleanupMode.KeepOnlyMostRecent, Utc, cancel.Token));
        Assert.True((await service.PreviewAsync(f.Temp.Child(), HistoryCleanupMode.KeepOnlyMostRecent, Utc)).Complete);
    }

    [Fact]
    public async Task ProductionPreflightCannotBypassHardSystemPolicyWithCraftedSelections()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("data", "never copy");
        var policy = new ObservingHardPolicy(f.Source);
        var config = new BackupConfiguration(f.RepositoryPath, [new(new(f.Root, f.Source), f.Source)]) { ForcedSourceIds = [f.Root] };
        var before = TemporaryDirectory.Snapshot(f.RepositoryPath);
        var result = await new EngineBackupAttemptRunner(sourcePolicy: policy).RunAsync(config, AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.RepositoryUnavailable, result.Outcome); Assert.Null(result.Scan); Assert.Equal(before, TemporaryDirectory.Snapshot(f.RepositoryPath));
        Assert.True(SourcePaths.Same(Path.Combine(Path.GetTempPath(), "Fluff-History", "Work"), policy.Workspace));
    }

    [Fact]
    public async Task RemovalPublishesInactiveRootWithoutDeletingHistoryAndCleanupRequiresConfirmationAndRefresh()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("file", "old"); f.Scan(); f.Write("file", "new contents"); f.Scan();
        var mapping = SourceMapping.FromConfiguration(new(f.Root, f.Source, false), f.Source);
        using (var repository = Repository.Open(f.RepositoryPath))
        { new FileHistoryEngine(repository).Scan([mapping.ToSelection()!]); using var reader = repository.OpenReader(); Assert.False(reader.GetSourceRoot(f.Root)!.IsProtected); Assert.NotNull(reader.GetNewestVersion(new(f.Root, "file"))); }
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = f.RepositoryPath, Sources = [mapping] }, Browser = browser, Maintenance = new(new()) };
        var history = new HistoryViewModel(owner, new ProductDialogs()); await history.ActivateAsync(); Assert.Contains("Not currently backed up", history.Roots.Single().Description);
        await history.ChangeRootAsync(history.Roots.Single());
        await history.SelectChildrenAsync([history.Children.Single()]); Assert.True(history.RestoreSelected.CanExecute(null)); Assert.False(history.RestoreOriginal.CanExecute(null));
        var cleanup = new TimelineCleanupViewModel(owner, new ProductDialogs()) { SelectedChoice = ProductConfiguration.CleanupChoices[^1] };
        await cleanup.PreviewCompletion; Assert.Contains("1 file", cleanup.Summary); Assert.Contains("This would free up about", cleanup.Summary); Assert.DoesNotContain("protected", cleanup.Summary); await cleanup.Execute.ExecuteAsync(); Assert.True(cleanup.ConfirmRequested);
        Assert.Equal(0, owner.HistoryRevision); await cleanup.CancelConfirmation.ExecuteAsync(); Assert.Equal(0, owner.HistoryRevision);
        await cleanup.Execute.ExecuteAsync(); await cleanup.ConfirmExecution.ExecuteAsync(); Assert.Equal(1, owner.HistoryRevision); Assert.True(history.NeedsRefresh);
        Assert.False(history.RestoreSelected.CanExecute(null)); Assert.Contains("1 file version removed", cleanup.Result);
    }

    [Fact]
    public async Task SharedCleanupPreviewIsInvalidatedWhenOwnerPublishesTimelineRevision()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1);
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() }, Maintenance = new(new()) };
        var cleanup = new TimelineCleanupViewModel(owner, new ProductDialogs()) { SelectedChoice = ProductConfiguration.CleanupChoices[^1] };
        await cleanup.PreviewCompletion; Assert.True(cleanup.Execute.CanExecute(null));
        await cleanup.Execute.ExecuteAsync(); Assert.True(cleanup.ConfirmRequested);
        owner.TimelineChanged(); Assert.False(cleanup.Execute.CanExecute(null)); Assert.False(cleanup.ConfirmExecution.CanExecute(null)); Assert.False(cleanup.ConfirmRequested);
        Assert.True(cleanup.CanOpen);
    }

    [Fact]
    public async Task CleanupStartsUnselectedAndChoiceAutomaticallyPreviewsWithoutPublishing()
    {
        using var f = new HistoryFixture(); f.Add(content: "old"); f.Add(content: "older", day: 1); f.Add(content: "current", day: 2);
        var owner = new ProductContext { Settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child() }, Maintenance = new(new()) };
        var cleanup = new TimelineCleanupViewModel(owner, new ProductDialogs());
        using var before = f.Repository.OpenReader();
        Assert.False(cleanup.HasSpaceEstimate);
        Assert.Null(cleanup.SelectedChoice); Assert.False(cleanup.Execute.CanExecute(null)); Assert.False(cleanup.Preview.CanExecute(null));
        cleanup.SelectedChoice = ProductConfiguration.CleanupChoices[^1]; await cleanup.PreviewCompletion;
        Assert.True(cleanup.HasSpaceEstimate);
        Assert.True(cleanup.Execute.CanExecute(null)); Assert.Contains("1 file", cleanup.Summary); Assert.Contains("2 saved versions", cleanup.Summary); Assert.Contains("8 bytes", cleanup.Summary);
        Assert.DoesNotContain("candidate", cleanup.Summary); Assert.DoesNotContain("protected", cleanup.Summary);
        using var after = f.Repository.OpenReader(); Assert.Equal(before.Authority, after.Authority);
        owner.TimelineChanged(); Assert.Null(cleanup.SelectedChoice); Assert.False(cleanup.Execute.CanExecute(null)); Assert.False(cleanup.HasSpaceEstimate);
        cleanup.SelectedChoice = ProductConfiguration.CleanupChoices[^1]; await cleanup.PreviewCompletion; Assert.True(cleanup.Execute.CanExecute(null));
        cleanup.Reset(); Assert.Null(cleanup.SelectedChoice); Assert.False(cleanup.Execute.CanExecute(null)); Assert.False(cleanup.HasSpaceEstimate);
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 byte")]
    [InlineData(999L, "999 bytes")]
    [InlineData(1000L, "1 kB")]
    [InlineData(1000000L, "1 MB")]
    [InlineData(1000000000L, "1 GB")]
    [InlineData(1000000000000L, "1 TB")]
    [InlineData(999950L, "1 MB")]
    public void CleanupSizesUseReadableDecimalUnits(long bytes, string expected) => Assert.Equal(expected, PresentationText.Size(TestAppStrings.English, bytes));

    private sealed class ObservingHardPolicy(string path) : SourceFolderPolicy(locations: [new(path, SourceSafetyKind.HardBlocked)])
    {
        internal string? Workspace { get; private set; }
        public override System.Collections.Immutable.ImmutableArray<SourceSelection> Select(string repository, IEnumerable<SourceMapping> sources, string? workspace = null)
        { Workspace = workspace; return base.Select(repository, sources, workspace); }
    }
}
