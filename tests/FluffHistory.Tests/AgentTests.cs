using System.Buffers.Binary;
using System.Text;
using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class AgentTests
{
    private static readonly DateTimeOffset Anchor = BackupCoordinatorTests.Anchor;
    private sealed class Allowed : IBackupSafeguard { public SafeguardDecision Evaluate(BackupConfiguration configuration) => SafeguardDecision.Allow; }
    private sealed class Fixture : IDisposable
    {
        internal readonly TemporaryDirectory Temp = new();
        internal readonly ManualSchedulerTime Time = new(Anchor.AddMinutes(1));
        internal readonly BackupCoordinatorTests.ControlledRunner Runner = new();
        internal readonly BackupOperationGate Gate = new();
        internal readonly List<BackupCoordinator> Created = [];
        internal AppSettingsStore Store => new(Temp.Child("settings-v2.json"));
        internal AppSettings Settings => AppSettings.Empty with { RepositoryPath = Temp.Child("repo"), Sources = [SourceMapping.New(Temp.Child("source"))], IntervalMinutes = 10, AnchorUtc = Anchor };
        internal AgentController Create(RepositoryReadiness readiness = RepositoryReadiness.Ready) => new(Store, SourceFolderPolicy.Empty, (configuration, schedule) =>
        {
            var coordinator = new BackupCoordinator(configuration, schedule, Runner, new Allowed(), Time, Gate); Created.Add(coordinator); return coordinator;
        })
        { Inspect = (_, _) => Task.FromResult(new RepositoryInspection(readiness)), ValidateWorkspace = (_, _, _) => Task.FromResult<string?>(null) };
        public void Dispose() => Temp.Dispose();
    }
    [Fact]
    public async Task ColdStartupPublishesLoadedConfigurationAndRejectsCommandsCapturedBeforeInitialization()
    {
        using var f = new Fixture(); var saved = f.Settings;
        await f.Store.SaveAsync(saved); var disk = File.ReadAllBytes(f.Store.FilePath);
        await using var agent = f.Create(); var starting = agent.State;
        var waiting = agent.WaitForStateAsync(starting.Revision, default);
        var stale = new AgentRequest(AgentOperation.ApplySettings, AppSettings.Empty,
            AgentId: starting.AgentId, ConfigurationRevision: starting.ConfigurationRevision);

        await agent.InitializeAsync(); var loaded = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loaded.ConfigurationRevision > starting.ConfigurationRevision);
        Assert.Equal(saved.RepositoryPath, loaded.Settings.RepositoryPath);
        Assert.Equal(AgentError.Conflict, (await agent.ApplySettingsAsync(stale, default)).Error);
        Assert.Equal(saved.RepositoryPath, agent.State.Settings.RepositoryPath);
        Assert.Equal(disk, File.ReadAllBytes(f.Store.FilePath));
        Assert.Equal(0, f.Runner.Calls);

        var revision = agent.State.ConfigurationRevision;
        await agent.ReinspectAsync();
        Assert.Equal(revision, agent.State.ConfigurationRevision);
    }
    [Fact]
    public async Task LoadedConfigurationUsesFutureCadenceAndNewLifetimeHasNoReplayOrAttemptHistory()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings);
        await using (var agent = f.Create())
        {
            await agent.InitializeAsync(); Assert.Single(f.Created); Assert.Equal(0, f.Runner.Calls);
            Assert.Equal(Anchor.AddMinutes(10), agent.State.Backup!.NextScheduledUtc);
            f.Time.Advance(TimeSpan.FromMinutes(9)); var call = await f.Runner.Next(); call.Complete();
            await BackupCoordinatorTests.Until(f.Created.Single(), s => !s.IsAttemptActive);
            Assert.NotNull(agent.State.Backup!.LastAttempt);
        }
        f.Time.Advance(TimeSpan.FromDays(2));
        await using var restarted = f.Create(); await restarted.InitializeAsync();
        Assert.Null(restarted.State.Backup!.LastAttempt); Assert.Equal(1, f.Runner.Calls);
        Assert.True(restarted.State.Backup.NextScheduledUtc > f.Time.GetUtcNow());
    }
    [Fact]
    public async Task UnavailableDestinationKeepsFutureCadenceAndSuccessfulOpenUpdatesDetachedReadiness()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings);
        await using var agent = f.Create(RepositoryReadiness.Unavailable); await agent.InitializeAsync();
        Assert.Single(f.Created); Assert.Equal(0, f.Runner.Calls);
        Assert.Equal(RepositoryReadiness.Unavailable, agent.State.Inspection.State);
        Assert.Equal(Anchor.AddMinutes(10), agent.State.Backup!.NextScheduledUtc);
        f.Time.Advance(TimeSpan.FromMinutes(9)); var call = await f.Runner.Next();
        var repositoryId = Guid.NewGuid();
        call.Result.SetResult(new(BackupOutcome.Completed, OpenedRepository: new(RepositoryReadiness.Ready, true, repositoryId, [])));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (agent.State.Inspection.State != RepositoryReadiness.Ready)
        {
            var state = agent.State;
            if (state.Inspection.State == RepositoryReadiness.Ready) break;
            await agent.WaitForStateAsync(state.Revision, timeout.Token);
        }
        Assert.True(agent.State.Inspection.Encrypted); Assert.Equal(repositoryId, agent.State.Inspection.RepositoryId);
    }
    [Fact]
    public async Task NewlyReachableEncryptedDestinationReportsRecoveryWithoutAnEarlierEncryptionHint()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings);
        await using var agent = f.Create(RepositoryReadiness.Unavailable); await agent.InitializeAsync();
        Assert.False(agent.State.Inspection.Encrypted);
        Assert.Equal(BackupAdmission.Started, agent.RunNow());
        (await f.Runner.Next()).Complete(BackupOutcome.RecoveryRequired);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = agent.State;
            if (state.Inspection.State == RepositoryReadiness.RecoveryRequired) { Assert.True(state.Inspection.Encrypted); break; }
            await agent.WaitForStateAsync(state.Revision, timeout.Token);
        }
        Assert.False(agent.State.Backup!.IsAttemptActive);
    }
    [Fact]
    public async Task FirstReadySaveStartsExactlyOnceEvenWithScheduleOffAndRejectsStaleWriter()
    {
        using var f = new Fixture(); await using var agent = f.Create(); await agent.InitializeAsync();
        var state = agent.State; var settings = f.Settings with { SchedulingEnabled = false };
        var request = new AgentRequest(AgentOperation.ApplySettings, settings, AgentId: state.AgentId, ConfigurationRevision: state.ConfigurationRevision);
        var reply = await agent.ApplySettingsAsync(request, default); Assert.Equal(BackupAdmission.Started, reply.Admission);
        var call = await f.Runner.Next(); Assert.False(agent.State.Backup!.ScheduleEnabled); call.Complete();
        await BackupCoordinatorTests.Until(f.Created.Single(), s => !s.IsAttemptActive);
        Assert.Equal(AgentError.Conflict, (await agent.ApplySettingsAsync(request, default)).Error);
        request = request with { ConfigurationRevision = agent.State.ConfigurationRevision };
        Assert.Null((await agent.ApplySettingsAsync(request, default)).Admission); Assert.Equal(1, f.Runner.Calls);
        Assert.Equal(SettingsLoadState.Loaded, (await f.Store.LoadAsync()).State);
    }
    [Fact]
    public async Task CorruptSettingsAreIdleAndNotOverwrittenWithoutExplicitReset()
    {
        using var f = new Fixture(); File.WriteAllText(f.Store.FilePath, "{invalid"); await using var agent = f.Create(); await agent.InitializeAsync();
        Assert.True(agent.State.ConfigurationNeedsReset); Assert.Null(agent.State.Backup); Assert.Empty(f.Created);
        var request = new AgentRequest(AgentOperation.ApplySettings, AppSettings.Empty, AgentId: agent.State.AgentId, ConfigurationRevision: agent.State.ConfigurationRevision);
        Assert.Equal(AgentError.Conflict, (await agent.ApplySettingsAsync(request, default)).Error); Assert.Equal("{invalid", File.ReadAllText(f.Store.FilePath));
        Assert.Equal(AgentError.None, (await agent.ApplySettingsAsync(request with { Reset = true }, default)).Error);
        Assert.False(agent.State.ConfigurationNeedsReset); Assert.Equal(0, f.Runner.Calls);
    }
    [Fact]
    public async Task DetachedCompleteStateSupportsReconnectWithoutReplayAndShutdownReleasesAdmission()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings); await using var agent = f.Create(); await agent.InitializeAsync();
        var previous = agent.State; var waiting = agent.WaitForStateAsync(previous.Revision, default);
        Assert.Equal(BackupAdmission.Started, agent.RunNow()); var call = await f.Runner.Next();
        var next = await waiting.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(next.Revision > previous.Revision); Assert.Null(previous.Backup!.LastAttempt);
        Assert.True(f.Gate.IsBusy); await agent.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(call.Token.IsCancellationRequested); Assert.False(f.Gate.IsBusy); Assert.True(agent.State.ShuttingDown);
        Assert.Equal(BackupAdmission.NotReady, agent.RunNow());
    }
    [Fact]
    public async Task MutationAdmissionCoversSafeguardsAndBothManualAndScheduledStartsWithoutQueueing()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings); await using var agent = f.Create(); await agent.InitializeAsync();
        using (f.Gate.TryAcquire())
        {
            Assert.Equal(BackupAdmission.Busy, agent.RunNow()); f.Time.Advance(TimeSpan.FromMinutes(9));
            Assert.Equal(0, f.Runner.Calls); Assert.Equal(Anchor.AddMinutes(20), agent.State.Backup!.NextScheduledUtc);
        }
        Assert.Equal(0, f.Runner.Calls); Assert.Equal(BackupAdmission.Started, agent.RunNow()); var call = await f.Runner.Next();
        Assert.Null(f.Gate.TryAcquire()); Assert.Equal(BackupAdmission.Busy, agent.RunNow());
        call.Complete(); await BackupCoordinatorTests.Until(f.Created.Single(), s => !s.IsAttemptActive); Assert.False(f.Gate.IsBusy);
    }
    [Fact]
    public async Task ProtocolRoundTripsConfiguredActiveAndCompletedSnapshots()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings);
        await using var agent = f.Create(); await agent.InitializeAsync();
        await RoundTrip(agent.State);
        Assert.Equal(BackupAdmission.Started, agent.RunNow()); var call = await f.Runner.Next();
        await RoundTrip(agent.State);
        var opened = new RepositoryInspection(RepositoryReadiness.Ready, true, Guid.NewGuid(), []);
        call.Result.SetResult(new(BackupOutcome.Completed, CommittedProgressMayExist: true, OpenedRepository: opened));
        await BackupCoordinatorTests.Until(f.Created.Single(), s => !s.IsAttemptActive);
        var completed = await RoundTrip(agent.State);
        Assert.Equal(opened.RepositoryId, completed.Inspection.RepositoryId);
        Assert.True(completed.Inspection.Encrypted);
        Assert.True(completed.Backup!.LastAttempt!.Result.TimelineChanged);
        Assert.Equal(f.Settings.ToSchedule(), completed.Backup.Schedule);
        static async Task<AgentState> RoundTrip(AgentState state)
        {
            using var stream = new MemoryStream();
            await AgentProtocol.WriteAsync(stream, new AgentReply(AgentError.None, state), true, default); stream.Position = 0;
            return (await AgentProtocol.ReadAsync<AgentReply>(stream, true, default)).State;
        }
    }
    [Fact]
    public async Task ProtocolRoundTripsCompleteStateAndRejectsUnknownMissingDuplicateOrOversizedInput()
    {
        using var f = new Fixture(); await using var agent = f.Create(); await agent.InitializeAsync();
        using var stream = new MemoryStream(); var expected = new AgentReply(AgentError.None, agent.State);
        await AgentProtocol.WriteAsync(stream, expected, true, default); stream.Position = 0;
        var reply = await AgentProtocol.ReadAsync<AgentReply>(stream, true, default); Assert.Equal(expected.State.AgentId, reply.State.AgentId); Assert.Equal(expected.State.ConfigurationRevision, reply.State.ConfigurationRevision);
        foreach (var json in new[] { "{}", "{\"operation\":99}", "{\"operation\":0,\"operation\":1}", "{\"operation\":0,\"path\":\"unexpected\"}", "{\"operation\":0,\"reset\":true}" })
        {
            using var bad = Frame(Encoding.UTF8.GetBytes(json));
            await Assert.ThrowsAnyAsync<Exception>(() => AgentProtocol.ReadAsync<AgentRequest>(bad, false, default));
        }
        using var oversized = Frame([], AgentProtocol.MaximumRequestBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentProtocol.ReadAsync<AgentRequest>(oversized, false, default));
        using var wrongVersion = Frame(Encoding.UTF8.GetBytes("{\"operation\":0}")); wrongVersion.GetBuffer()[4] = 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentProtocol.ReadAsync<AgentRequest>(wrongVersion, false, default));
    }
    private static MemoryStream Frame(byte[] body, int? count = null)
    {
        var stream = new MemoryStream(); var header = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x31414846);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), AgentProtocol.Version); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), count ?? body.Length);
        stream.Write(header); stream.Write(body); stream.Position = 0; return stream;
    }
}
