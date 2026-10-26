using System.Text.Json;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class LastBackupStatusTests
{
    private static LastBackupStatus Value(string location, Guid id, long versions = 1) => new(1, id,
        LastBackupStatus.HashLocation(location), FoundationTests.Time, TimeSpan.FromSeconds(12).Ticks,
        BackupOutcome.Completed, versions, 0);

    [Fact]
    public async Task SingleAtomicDisplayValueOverwritesWithoutHistoryAndRecoversAStaleTemporaryFile()
    {
        using var t = new TemporaryDirectory(); var store = new LastBackupStatusStore(t.Child("last-status.json"));
        var first = Value(t.Root, Guid.NewGuid()); await store.SaveAsync(first);
        File.WriteAllText(store.FilePath + ".tmp", "interrupted disposable replacement");
        var next = first with { VersionsSaved = 2, FinishedUtc = first.FinishedUtc.AddDays(1) };
        await store.SaveAsync(next); Assert.Equal(next, await store.LoadAsync()); Assert.Single(Directory.GetFiles(t.Root));
        Assert.True(new FileInfo(store.FilePath).Length < 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(first, new(true)));
        Assert.Equal(next, await store.LoadAsync()); Assert.Single(Directory.GetFiles(t.Root));
    }

    [Fact]
    public async Task CorruptUnknownDuplicateMissingAndOversizedInputIsIgnoredInPlace()
    {
        using var t = new TemporaryDirectory(); var store = new LastBackupStatusStore(t.Child("last-status.json"));
        await store.SaveAsync(Value(t.Root, Guid.NewGuid())); var raw = File.ReadAllText(store.FilePath);
        var missing = System.Text.Json.Nodes.JsonNode.Parse(raw)!; missing.AsObject().Remove("durationTicks");
        foreach (var bad in new[] { "{", "{}", "null", raw.Replace("\"schema\":1", "\"schema\":999"),
            raw.Replace("\"durationTicks\":120000000", "\"durationTicks\":-1"), raw[..^1] + ",\"schema\":1}",
            raw[..^1] + ",\"history\":[]}", missing.ToJsonString(), new string(' ', LastBackupStatusStore.MaximumBytes + 1) })
        {
            File.WriteAllText(store.FilePath, bad); Assert.Null(await store.LoadAsync());
            Assert.Equal(bad, File.ReadAllText(store.FilePath)); Assert.Single(Directory.GetFiles(t.Root));
        }
    }

    [Fact]
    public void BindingRequiresBothExactRepositoryIdentityAndNormalizedLocation()
    {
        using var t = new TemporaryDirectory(); var id = Guid.NewGuid(); var value = Value(t.Child(), id);
        Assert.True(value.Matches(t.Child() + Path.DirectorySeparatorChar, id));
        Assert.False(value.Matches(t.Child(), Guid.NewGuid())); Assert.False(value.Matches(t.Child("elsewhere"), id));
        Assert.False(value.Matches(t.Child(), null));
    }

    [Fact]
    public async Task AgentRestartRecoversOnlyDisplayWithoutReplayingAttemptOrChangingRepository()
    {
        using var f = new StatusFixture(); using var repo = Repository.Create(f.Settings.RepositoryPath);
        var authority = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        await f.SettingsStore.SaveAsync(f.Settings);
        LastBackupStatus saved;
        await using (var agent = f.Create(repo.Identity.RepositoryId))
        {
            await agent.InitializeAsync(); Assert.Null(agent.State.LastBackupStatus);
            Assert.Equal(BackupAdmission.Started, agent.RunNow()); var call = await f.Runner.Next();
            for (var i = 0; i < 1000; i++) f.Runner.Progress!(new(BackupPhase.Scanning));
            Assert.False(File.Exists(f.StatusStore.FilePath));
            call.Result.SetResult(BackupAttemptResult.FromScan(new ScanResult { Outcome = ScanOutcome.Completed, VersionsCreated = 3, SourceSkips = 1 }));
            await WaitCompleted(agent); saved = agent.State.LastBackupStatus!; Assert.Equal(3, saved.VersionsSaved);
            await agent.ShutdownAsync();
        }
        Assert.Equal(saved, await f.StatusStore.LoadAsync());
        f.Time.Advance(TimeSpan.FromDays(2));
        await using (var restarted = f.Create(repo.Identity.RepositoryId))
        {
            await restarted.InitializeAsync(); Assert.Equal(saved, restarted.State.LastBackupStatus);
            Assert.Null(restarted.State.Backup!.LastAttempt); Assert.Null(restarted.State.Backup.LastSuccessfulUtc);
            Assert.Equal(0, restarted.State.TimelineRevision); Assert.Equal(1, f.Runner.Calls);
        }
        Assert.Equal(authority, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        await using var wrong = f.Create(Guid.NewGuid()); await wrong.InitializeAsync(); Assert.Null(wrong.State.LastBackupStatus);
    }

    [Fact]
    public async Task LaterFailedAttemptReplacesDisplayWithoutIncompleteSuccessCountsAndWriteFailureDoesNotFailBackup()
    {
        using var f = new StatusFixture(); var id = Guid.NewGuid(); await f.SettingsStore.SaveAsync(f.Settings);
        await using (var agent = f.Create(id))
        {
            await agent.InitializeAsync(); Assert.Equal(BackupAdmission.Started, agent.RunNow()); (await f.Runner.Next()).Complete(); await WaitCompleted(agent);
            f.Time.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(BackupAdmission.Started, agent.RunNow());
            (await f.Runner.Next()).Complete(BackupOutcome.Cancelled); await WaitCompleted(agent);
            Assert.Equal(BackupOutcome.Cancelled, agent.State.LastBackupStatus!.Outcome); Assert.Null(agent.State.LastBackupStatus.VersionsSaved);
            await agent.ShutdownAsync();
        }
        Assert.Equal(BackupOutcome.Cancelled, (await f.StatusStore.LoadAsync())!.Outcome);
        File.Delete(f.StatusStore.FilePath); Directory.CreateDirectory(f.StatusStore.FilePath);
        await using var blocked = f.Create(id); await blocked.InitializeAsync(); Assert.Equal(BackupAdmission.Started, blocked.RunNow());
        (await f.Runner.Next()).Complete(); await WaitCompleted(blocked); await blocked.ShutdownAsync();
        Assert.Equal(BackupOutcome.Completed, blocked.State.LastBackupStatus!.Outcome);
        Assert.True(Directory.Exists(f.StatusStore.FilePath)); Assert.False(File.Exists(f.StatusStore.FilePath + ".tmp"));
    }

    [Fact]
    public async Task ConfigurationLocationChangeHidesPriorStatusWithoutCreatingAnotherRecord()
    {
        using var f = new StatusFixture(); var id = Guid.NewGuid(); var original = f.Settings;
        await f.SettingsStore.SaveAsync(original); var value = Value(original.RepositoryPath, id); await f.StatusStore.SaveAsync(value);
        await using var agent = f.Create(id); await agent.InitializeAsync(); Assert.Equal(value, agent.State.LastBackupStatus);
        var state = agent.State;
        var reply = await agent.ApplySettingsAsync(new(AgentOperation.ApplySettings, original with { RepositoryPath = f.Temp.Child("other"), Sources = [] },
            AgentId: state.AgentId, ConfigurationRevision: state.ConfigurationRevision), default);
        Assert.Equal(AgentError.None, reply.Error); Assert.Null(reply.State.LastBackupStatus);
        Assert.Equal(value, await f.StatusStore.LoadAsync()); Assert.Single(Directory.GetFiles(f.Temp.Root, "last-status*"));
    }

    [Fact]
    public async Task ProtocolCarriesOnlyBoundedDisplayAndRejectsInvalidValues()
    {
        using var f = new StatusFixture(); var id = Guid.NewGuid(); await f.SettingsStore.SaveAsync(f.Settings);
        await using var agent = f.Create(id); await agent.InitializeAsync(); var value = Value(f.Settings.RepositoryPath, id);
        foreach (var saved in new[] { value, value with { Schema = 99 }, value with { Outcome = (BackupOutcome)999 },
            value with { DurationTicks = -1 }, value with { VersionsSaved = -1 }, value with { LocationHash = "invalid" }, value with { RepositoryId = Guid.NewGuid() } })
        {
            using var stream = new MemoryStream(); await AgentProtocol.WriteAsync(stream, new AgentReply(AgentError.None, agent.State with { LastBackupStatus = saved }), true, default);
            stream.Position = 0;
            if (saved == value) Assert.Equal(value, (await AgentProtocol.ReadAsync<AgentReply>(stream, true, default)).State.LastBackupStatus);
            else await Assert.ThrowsAsync<InvalidDataException>(() => AgentProtocol.ReadAsync<AgentReply>(stream, true, default));
        }
    }

    private static async Task WaitCompleted(AgentController agent)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (agent.State.Backup?.IsAttemptActive != false || agent.State.LastBackupStatus is null ||
            agent.State.LastBackupStatus.FinishedUtc != agent.State.Backup.LastAttempt?.FinishedUtc)
        { var state = agent.State; await agent.WaitForStateAsync(state.Revision, deadline.Token); }
    }
    private sealed class Allowed : IBackupSafeguard { public SafeguardDecision Evaluate(BackupConfiguration configuration) => SafeguardDecision.Allow; }
    private sealed class StatusFixture : IDisposable
    {
        internal readonly TemporaryDirectory Temp = new();
        internal readonly ManualSchedulerTime Time = new(FoundationTests.Time);
        internal readonly BackupCoordinatorTests.ControlledRunner Runner = new();
        internal AppSettingsStore SettingsStore => new(Temp.Child("settings.json"));
        internal LastBackupStatusStore StatusStore => new(Temp.Child("last-status.json"));
        internal AppSettings Settings => AppSettings.Empty with { RepositoryPath = Temp.Child("repo"), Sources = [SourceMapping.New(Temp.Child("source"))], SchedulingEnabled = false };
        internal AgentController Create(Guid id) => new(SettingsStore, SourceFolderPolicy.Empty,
            (configuration, schedule) => new BackupCoordinator(configuration, schedule, Runner, new Allowed(), Time), lastStatusStore: StatusStore)
        { Inspect = (_, _) => Task.FromResult(new RepositoryInspection(RepositoryReadiness.Ready, RepositoryId: id)), ValidateWorkspace = (_, _, _) => Task.FromResult<string?>(null) };
        public void Dispose() => Temp.Dispose();
    }
}
