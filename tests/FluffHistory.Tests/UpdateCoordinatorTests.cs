using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class UpdateCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private sealed class Source : IUpdateReleaseSource
    {
        public int Calls;
        public ReleaseCheck Result = new(UpdateReleaseTests.Parse(UpdateReleaseTests.Release("v1.10.0")));
        public Task<ReleaseCheck> CheckAsync(string? etag, ImmutableArray<UpdateRelease> cached, CancellationToken token)
        { Calls++; return Task.FromResult(Result); }
    }
    private sealed class Installer : IUpdateInstaller
    {
        public int Stages, Handoffs; public bool Manual, Cancel;
        public UpdateFailure Result = UpdateFailure.None;
        public Func<Task>? OnHandoff;
        public Task<StagedUpdate> StageAsync(UpdateRelease release, IProgress<long> progress, CancellationToken token)
        { Stages++; progress.Report(release.Size); return Task.FromResult(new StagedUpdate(release, "test-only")); }
        public async Task<UpdateFailure> HandoffAsync(StagedUpdate update, bool manual, bool cancelBackup, CancellationToken token)
        { Handoffs++; Manual = manual; Cancel = cancelBackup; if (OnHandoff is not null) await OnHandoff(); return Result; }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TemporaryDirectory Temp = new();
        public readonly ManualSchedulerTime Clock = new(Now);
        public readonly Source Source = new(); public readonly Installer Installer = new();
        public UpdateJsonStore<UpdatePreferences> Preferences => new(Temp.Child("update-preferences-v1.json"), UpdatePreferences.Default, p => p.Validate(), 4096);
        public UpdateJsonStore<UpdateDiskState> Disk => new(Temp.Child("update-state-v1.json"), UpdateDiskState.Empty, s => s.Validate());
        public UpdateJsonStore<UpdateInstallResult?> Results => new(Temp.Child("update-result-v1.json"), null, r => (r ?? throw new InvalidDataException()).Validate());
        public UpdateCoordinator Create(bool development = false, Func<UpdateNetworkPolicy>? network = null) =>
            new(Source, Installer, Preferences, Disk, development ? null : ReleaseVersion.Parse("1.9.0"), network, Clock, Results);
        public void Dispose() => Temp.Dispose();
    }
    [Theory]
    [InlineData(UpdateFrequency.Daily, 1)][InlineData(UpdateFrequency.Weekly, 7)][InlineData(UpdateFrequency.Monthly, 30)]
    public void AnchoredCadenceRestartClockAndFailureBackoff(UpdateFrequency frequency, int days)
    {
        var p = UpdatePreferences.Default with { Frequency = frequency };
        var s = UpdateDiskState.Empty with { LastAttemptUtc = Now, LastSuccessUtc = Now };
        Assert.False(UpdateSchedule.IsDue(p, s, Now.AddDays(days).AddTicks(-1)));
        Assert.True(UpdateSchedule.IsDue(p, s, Now.AddDays(days)));
        Assert.True(UpdateSchedule.IsDue(p, s, Now.AddDays(days * 4)));
        Assert.False(UpdateSchedule.IsDue(p, s, Now.AddDays(-1)));
        var rebased = UpdateSchedule.ReanchorFuture(s, Now.AddDays(-1));
        Assert.False(UpdateSchedule.IsDue(p, rebased, Now.AddDays(-1)));
        var failed = s with { LastAttemptUtc = Now.AddDays(days) };
        Assert.False(UpdateSchedule.IsDue(p, failed, Now.AddDays(days).AddHours(5)));
        Assert.True(UpdateSchedule.IsDue(p, failed, Now.AddDays(days).AddHours(6)));
    }
    [Fact]
    public async Task DefaultsWeeklyStableAndOneDueCheckNoCatchup()
    {
        using var f = new Fixture(); f.Installer.Result = UpdateFailure.Busy;
        await using var updater = f.Create(); await updater.TickAsync();
        Assert.Equal(UpdatePreferences.Default, updater.State.Preferences);
        Assert.Equal(1, f.Source.Calls); Assert.Equal(UpdatePhase.WaitingForIdle, updater.State.Phase);
        Assert.False(f.Installer.Cancel); Assert.False(f.Installer.Manual);
        f.Clock.Advance(TimeSpan.FromDays(20)); await updater.TickAsync();
        Assert.Equal(2, f.Source.Calls); await updater.TickAsync(); Assert.Equal(2, f.Source.Calls);
    }
    [Fact]
    public async Task CommittedAutomaticHandoffThenRollbackCannotRetryUntilNextWeeklyCheck()
    {
        using var f = new Fixture(); bool? pendingAtHandoff = null;
        f.Installer.OnHandoff = async () => pendingAtHandoff = (await f.Disk.LoadAsync()).Value.AutomaticPending;
        await using (var updater = f.Create())
        {
            await updater.TickAsync(); Assert.True(updater.HandoffReady.IsCompletedSuccessfully);
        }
        var failed = new UpdateInstallResult(1, "1.10.0", Now, 1, false, UpdateFailure.Installation, false);
        await f.Results.SaveAsync(failed);
        // Restart as the OLD version with the same real JSON stores and ordinary Weekly policy.
        await using var restarted = f.Create();
        for (var i = 0; i < 3; i++) { await restarted.TickAsync(); f.Clock.Advance(TimeSpan.FromMinutes(1)); }
        Assert.Equal(UpdatePreferences.Default, restarted.State.Preferences);
        Assert.Equal(failed, restarted.State.LastInstall); Assert.Equal(UpdateFailure.Installation, restarted.State.Failure);
        Assert.Equal(1, f.Source.Calls); Assert.Equal(1, f.Installer.Handoffs);
        Assert.Equal(false, pendingAtHandoff); Assert.False((await f.Disk.LoadAsync()).Value.AutomaticPending);
        f.Clock.Advance(TimeSpan.FromDays(7)); await restarted.TickAsync();
        Assert.Equal(2, f.Source.Calls); Assert.Equal(2, f.Installer.Handoffs);
        Assert.False((await f.Disk.LoadAsync()).Value.AutomaticPending);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FailedDetachedInstallStillAllowsImmediateManualRetry(bool cancelBackup)
    {
        using var f = new Fixture();
        await using (var updater = f.Create()) await updater.TickAsync();
        await f.Results.SaveAsync(new(1, "1.10.0", Now, 1, false, UpdateFailure.Installation, false));
        await using var restarted = f.Create(); await restarted.TickAsync();
        Assert.Equal(1, f.Installer.Handoffs);
        await restarted.InstallNowAsync(cancelBackup);
        Assert.Equal(2, f.Installer.Handoffs); Assert.Equal(1, f.Source.Calls);
        Assert.True(f.Installer.Manual); Assert.Equal(cancelBackup, f.Installer.Cancel);
        Assert.True(restarted.HandoffReady.IsCompletedSuccessfully);
        Assert.False((await f.Disk.LoadAsync()).Value.AutomaticPending);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingDigestIsAnnouncedWithoutAutomaticAttemptsIncludingCachedPending(bool stalePending)
    {
        using var f = new Fixture();
        f.Source.Result = new([f.Source.Result.Releases.Single() with { Sha256 = null }]);
        if (stalePending)
            await f.Disk.SaveAsync(UpdateDiskState.Empty with { LastAttemptUtc = Now, LastSuccessUtc = Now,
                Releases = f.Source.Result.Releases, AutomaticPending = true });
        await using var updater = f.Create();
        for (var i = 0; i < 4; i++) { await updater.TickAsync(); f.Clock.Advance(TimeSpan.FromMinutes(1)); }
        Assert.NotNull(updater.State.Available); Assert.False(updater.State.Available.CanDownload);
        Assert.Equal(UpdatePhase.Available, updater.State.Phase); Assert.Equal(UpdateFailure.None, updater.State.Failure);
        Assert.False((await f.Disk.LoadAsync()).Value.AutomaticPending);
        Assert.Equal(stalePending ? 0 : 1, f.Source.Calls); Assert.Equal(0, f.Installer.Stages); Assert.Equal(0, f.Installer.Handoffs);
        await updater.InstallNowAsync(false); Assert.Equal(UpdateFailure.Integrity, updater.State.Failure);
        Assert.Equal(0, f.Installer.Handoffs);
    }
    [Theory]
    [InlineData(UpdateFailure.Busy)][InlineData(UpdateFailure.BackupStillRunning)]
    public async Task BusyAutomaticHandoffRearmsDurablyAcrossRestartThenConsumesWhenIdle(UpdateFailure busy)
    {
        using var f = new Fixture(); f.Installer.Result = busy;
        await using (var updater = f.Create())
        {
            await updater.TickAsync(); Assert.Equal(UpdatePhase.WaitingForIdle, updater.State.Phase);
            Assert.False(updater.HandoffReady.IsCompleted); Assert.True((await f.Disk.LoadAsync()).Value.AutomaticPending);
        }
        await using var restarted = f.Create(); await restarted.TickAsync();
        Assert.Equal(2, f.Installer.Handoffs); Assert.True((await f.Disk.LoadAsync()).Value.AutomaticPending);
        f.Installer.Result = UpdateFailure.None; await restarted.TickAsync();
        Assert.Equal(3, f.Installer.Handoffs); Assert.Equal(1, f.Source.Calls);
        Assert.False(f.Installer.Manual); Assert.False(f.Installer.Cancel);
        Assert.True(restarted.HandoffReady.IsCompletedSuccessfully); Assert.False((await f.Disk.LoadAsync()).Value.AutomaticPending);
    }
    [Theory]
    [InlineData(UpdateFailure.Busy)][InlineData(UpdateFailure.BackupStillRunning)]
    public async Task BusyManualOnlyRequestDoesNotArmAutomaticRetry(UpdateFailure busy)
    {
        using var f = new Fixture(); f.Installer.Result = busy;
        await using var updater = f.Create(); await updater.CheckNowAsync(); await updater.InstallNowAsync(true);
        Assert.Equal(UpdatePhase.WaitingForIdle, updater.State.Phase);
        Assert.False((await f.Disk.LoadAsync()).Value.AutomaticPending);
        await updater.TickAsync(); Assert.Equal(1, f.Installer.Handoffs);
        f.Installer.Result = UpdateFailure.None; await updater.InstallNowAsync(true);
        Assert.Equal(2, f.Installer.Handoffs); Assert.True(f.Installer.Manual); Assert.True(f.Installer.Cancel);
        Assert.True(updater.HandoffReady.IsCompletedSuccessfully);
    }
    [Fact]
    public async Task NeverAllowsManualCheckButNeverAutomaticInstall()
    {
        using var f = new Fixture(); await f.Preferences.SaveAsync(UpdatePreferences.Default with { Frequency = UpdateFrequency.Never });
        await using var updater = f.Create(); await updater.TickAsync(); Assert.Equal(0, f.Source.Calls);
        await updater.CheckNowAsync(); Assert.Equal(1, f.Source.Calls); Assert.Equal(0, f.Installer.Handoffs);
        Assert.Equal(UpdateFrequency.Never, updater.State.Preferences.Frequency);
        await updater.InstallNowAsync(true); Assert.True(f.Installer.Manual); Assert.True(f.Installer.Cancel);
    }
    [Fact]
    public async Task DevelopmentBuildCanDiscoverButNeverInstall()
    {
        using var f = new Fixture(); await using var updater = f.Create(development: true);
        await updater.TickAsync(); Assert.NotNull(updater.State.Available); Assert.Equal(0, f.Installer.Stages);
        await updater.InstallNowAsync(false); Assert.Equal(UpdateFailure.DevelopmentBuild, updater.State.Failure); Assert.Equal(0, f.Installer.Handoffs);
    }
    [Fact]
    public async Task ReloadAndRestartUseIndependentDesiredPolicyAndPreservedObservation()
    {
        using var f = new Fixture();
        await using (var updater = f.Create())
        {
            await updater.CheckNowAsync();
            await f.Preferences.SaveAsync(UpdatePreferences.Default with { Frequency = UpdateFrequency.Never, Channel = UpdateChannel.Beta });
            await updater.ReloadPreferencesAsync(); Assert.Equal(UpdateFrequency.Never, updater.State.Preferences.Frequency);
            await updater.TickAsync(); Assert.Equal(1, f.Source.Calls);
        }
        await using var restarted = f.Create(); await restarted.TickAsync();
        Assert.Equal(Now, restarted.State.LastCheckedUtc); Assert.NotNull(restarted.State.Available); Assert.Equal(1, f.Source.Calls);
        Assert.Equal(UpdateChannel.Beta, restarted.State.Preferences.Channel);
    }
    [Fact]
    public async Task FailedCheckBackoffAndManualBypassDoNotClaimSuccess()
    {
        using var f = new Fixture(); f.Source.Result = new([], Failure: UpdateFailure.Offline);
        await using var updater = f.Create(); await updater.TickAsync();
        Assert.Null(updater.State.LastCheckedUtc); Assert.Equal(UpdateFailure.Offline, updater.State.Failure);
        f.Clock.Advance(TimeSpan.FromHours(5)); await updater.TickAsync(); Assert.Equal(1, f.Source.Calls);
        await updater.CheckNowAsync(); Assert.Equal(2, f.Source.Calls);
        f.Clock.Advance(TimeSpan.FromHours(6)); await updater.TickAsync(); Assert.Equal(3, f.Source.Calls);
    }
    [Fact]
    public async Task OfflineAndMeteredPolicyDefersAutomaticTrafficButManualIsExplicit()
    {
        using var f = new Fixture(); var policy = new UpdateNetworkPolicy(false, false);
        await using var updater = f.Create(network: () => policy); await updater.TickAsync(); Assert.Equal(0, f.Source.Calls);
        policy = new(true, false); await updater.TickAsync(); Assert.Equal(1, f.Source.Calls); Assert.Equal(0, f.Installer.Stages);
        await updater.InstallNowAsync(false); Assert.Equal(1, f.Installer.Handoffs);
    }
    [Fact]
    public async Task StrictStoresRoundTripAndRejectUnknownMissingDuplicateOrOversizedData()
    {
        using var f = new Fixture(); await f.Preferences.SaveAsync(UpdatePreferences.Default);
        Assert.Equal(UpdatePreferences.Default, (await f.Preferences.LoadAsync()).Value);
        var state = UpdateDiskState.Empty with { LastAttemptUtc = Now, LastSuccessUtc = Now, Releases = f.Source.Result.Releases, ETag = "\"cache\"" };
        await f.Disk.SaveAsync(state); var loaded = await f.Disk.LoadAsync(); Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        Assert.Equal(state.Releases.Single(), loaded.Value.Releases.Single());
        foreach (var invalid in new[] { "{}", "{\"schema\":1,\"channel\":\"stable\",\"frequency\":\"weekly\",\"unknown\":0}",
            "{\"schema\":1,\"schema\":1,\"channel\":\"stable\",\"frequency\":\"weekly\"}",
            "{\"schema\":1,\"channel\":0,\"frequency\":\"weekly\"}", new string(' ', 4097) })
        {
            await File.WriteAllTextAsync(f.Preferences.FilePath, invalid);
            Assert.Equal(SettingsLoadState.Corrupt, (await f.Preferences.LoadAsync()).State);
        }
        await using var updater = f.Create(); await updater.TickAsync(); Assert.Equal(0, f.Source.Calls);
        Assert.Equal(UpdateFrequency.Never, updater.State.Preferences.Frequency);
    }
    [Fact]
    public async Task SlowReleaseSourceDoesNotOwnCallingThreadAndDisposalDrainsIt()
    {
        using var f = new Fixture(); var source = new BlockingSource();
        var updater = new UpdateCoordinator(source, f.Installer, f.Preferences, f.Disk, ReleaseVersion.Parse("1.0.0"), clock: f.Clock);
        updater.Start(); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpdatePhase.Checking, updater.State.Phase);
        await updater.DisposeAsync(); Assert.True(source.Cancelled);
    }
    private sealed class BlockingSource : IUpdateReleaseSource
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Cancelled;
        public async Task<ReleaseCheck> CheckAsync(string? etag, ImmutableArray<UpdateRelease> cached, CancellationToken token)
        { Entered.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { Cancelled = token.IsCancellationRequested; } return new([]); }
    }
    [Fact]
    public async Task DetachedResultWrittenAfterAgentStartupIsObservedEvenWhenNetworkChecksAreNever()
    {
        using var f = new Fixture(); await f.Preferences.SaveAsync(UpdatePreferences.Default with { Frequency = UpdateFrequency.Never });
        var results = new UpdateJsonStore<UpdateInstallResult?>(f.Temp.Child("update-result-v1.json"), null, r => (r ?? throw new InvalidDataException()).Validate());
        await using var updater = new UpdateCoordinator(f.Source, f.Installer, f.Preferences, f.Disk, ReleaseVersion.Parse("1.9.0"), clock: f.Clock, resultStore: results);
        await updater.TickAsync(); Assert.Null(updater.State.LastInstall);
        var failed = new UpdateInstallResult(1, "1.10.0", Now, 1, false, UpdateFailure.Installation, false);
        await results.SaveAsync(failed); await updater.TickAsync();
        Assert.Equal(failed, updater.State.LastInstall); Assert.Equal(UpdateFailure.Installation, updater.State.Failure); Assert.Equal(0, f.Source.Calls);
        await results.SaveAsync(failed with { ReleaseVersion = "1.9.0", Installed = true, Failure = UpdateFailure.None });
        await updater.TickAsync(); Assert.Equal(UpdateFailure.None, updater.State.Failure); Assert.Equal(0, f.Source.Calls);
    }
}
