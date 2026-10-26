using System.Collections.Immutable;
using FluffHistory.Application;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class AgentTests
{
    [Fact]
    public async Task UpdatePolicyAndSnapshotNeverChangeBackupConfigurationRevisionOrRunBackup()
    {
        using var f = new Fixture(); await f.Store.SaveAsync(f.Settings);
        await using var agent = f.Create();
        var desired = new UpdateJsonStore<UpdatePreferences>(f.Temp.Child("update-preferences-v1.json"), UpdatePreferences.Default, p => p.Validate());
        await using var updates = new UpdateCoordinator(new NoReleases(), new NeverInstall(), desired,
            new(f.Temp.Child("update-state-v1.json"), UpdateDiskState.Empty, s => s.Validate()), null);
        agent.AttachUpdates(updates); await agent.InitializeAsync();
        var revision = agent.State.ConfigurationRevision; var settings = File.ReadAllBytes(f.Store.FilePath);
        await desired.SaveAsync(UpdatePreferences.Default with { Channel = UpdateChannel.Beta, Frequency = UpdateFrequency.Never });
        await agent.ReloadUpdatePreferencesAsync(default);
        Assert.Equal(revision, agent.State.ConfigurationRevision); Assert.Equal(0, f.Runner.Calls); Assert.Equal(settings, File.ReadAllBytes(f.Store.FilePath));
        Assert.Equal(UpdateChannel.Beta, agent.State.Updates!.Preferences.Channel);
        Assert.True(agent.RequestUpdateCheck());
        await using var wire = new MemoryStream();
        await AgentProtocol.WriteAsync(wire, new AgentReply(AgentError.None, agent.State), true, default); wire.Position = 0;
        var reply = await AgentProtocol.ReadAsync<AgentReply>(wire, true, default);
        Assert.Equal(agent.State.Updates!.Preferences, reply.State.Updates!.Preferences);
        Assert.Throws<InvalidDataException>(() => AgentProtocol.Validate(new(AgentOperation.RunNow, ConfirmUpdateCancellation: true)));
        AgentProtocol.Validate(new(AgentOperation.InstallUpdate, ConfirmUpdateCancellation: true));
        var invalid = reply with { State = reply.State with { Updates = reply.State.Updates with { CurrentReleaseVersion = new string('1', 1000) } } };
        using var bad = new MemoryStream(); await AgentProtocol.WriteAsync(bad, invalid, true, default); bad.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentProtocol.ReadAsync<AgentReply>(bad, true, default));
    }
    private sealed class NoReleases : IUpdateReleaseSource
    { public Task<ReleaseCheck> CheckAsync(string? etag, ImmutableArray<UpdateRelease> cached, CancellationToken token) => Task.FromResult(new ReleaseCheck([])); }
    private sealed class NeverInstall : IUpdateInstaller
    {
        public Task<StagedUpdate> StageAsync(UpdateRelease release, IProgress<long> progress, CancellationToken token) => throw new InvalidOperationException();
        public Task<UpdateFailure> HandoffAsync(StagedUpdate update, bool manual, bool cancelBackup, CancellationToken token) => throw new InvalidOperationException();
    }
}
