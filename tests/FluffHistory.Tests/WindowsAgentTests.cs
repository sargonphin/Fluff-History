using System.IO.Pipes;
using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using FluffHistory.Windows;
using Xunit;

namespace FluffHistory.Tests;

[Collection("NativeBackupPlatform")]
public sealed class WindowsAgentTests
{
    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task OtherClientCannotEndHostBeforeShutdownReplyFinishes(bool disconnect)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json"));
        // Exceed the native 4KiB pipe buffer so the shutdown reply demonstrably waits for its reader.
        var sources = Enumerable.Range(0, 128).Select(i => SourceMapping.New(@"C:\unmapped\" + i + new string('a', 6000))
            with { IsProtected = false, RuntimePath = null }).ToImmutableArray();
        await store.SaveAsync(AppSettings.Empty with { Sources = sources });
        await using var controller = new AgentController(store, SourceFolderPolicy.Empty, (_, _) => throw new InvalidOperationException());
        await controller.InitializeAsync();
        var endpoint = WindowsAgentClient.PipeName + ".shutdown." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new WindowsAgentServer(controller) { Endpoint = endpoint }; var serving = server.RunAsync(stop.Token);
        using var shutdown = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await shutdown.ConnectAsync(deadline.Token);
            await AgentProtocol.WriteAsync(shutdown, new AgentRequest(AgentOperation.ShutdownForUpdate), false, deadline.Token);
            while (!controller.State.ShuttingDown) await Task.Delay(10, deadline.Token);
            Assert.False(server.ShutdownRequested.IsCompleted);
            var other = await new WindowsAgentClient { Endpoint = endpoint }.SendAsync(new(AgentOperation.GetState), deadline.Token);
            Assert.True(other.State.ShuttingDown);
            await Task.WhenAny(server.ShutdownRequested, Task.Delay(100, deadline.Token));
            Assert.False(server.ShutdownRequested.IsCompleted); // That unrelated client's finally must not cancel our reply.
            if (disconnect) shutdown.Dispose(); // A lost shutdown requester must still let the host exit.
            else
            {
                var reply = await AgentProtocol.ReadAsync<AgentReply>(shutdown, true, deadline.Token);
                Assert.True(reply.State.ShuttingDown);
            }
            await server.ShutdownRequested.WaitAsync(deadline.Token);
        }
        finally { shutdown.Dispose(); await stop.CancelAsync(); await serving.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class Allowed : IBackupSafeguard { public SafeguardDecision Evaluate(BackupConfiguration configuration) => SafeguardDecision.Allow; }
    [WindowsFact]
    public async Task SeparateGatesExcludeBackupAndUiMutationsInBothDirectionsAndReleaseAcrossAsyncThreads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var path = temp.Child("admission.lock");
        var agentGate = new BackupOperationGate(new WindowsOperationAdmission(path));
        var uiGate = new BackupOperationGate(new WindowsOperationAdmission(path));
        var runner = new BackupCoordinatorTests.ControlledRunner(); var time = new ManualSchedulerTime(BackupCoordinatorTests.Anchor);
        await using var coordinator = new BackupCoordinator(BackupCoordinatorTests.Configuration(),
            new(TimeSpan.FromMinutes(10), BackupCoordinatorTests.Anchor), runner, new Allowed(), time, agentGate);
        coordinator.Start();
        var uiLease = uiGate.TryAcquire(); Assert.NotNull(uiLease);
        Assert.Equal(BackupAdmission.Busy, coordinator.RequestRunNow()); time.Advance(TimeSpan.FromMinutes(10)); Assert.Equal(0, runner.Calls);
        await Task.Run(uiLease!.Dispose); Assert.False(agentGate.IsBusy);
        Assert.Equal(BackupAdmission.Started, coordinator.RequestRunNow()); var call = await runner.Next(); Assert.Null(uiGate.TryAcquire());
        var cleanup = new TimelineMaintenanceService(uiGate);
        var removal = new ExactVersionRemovalService(uiGate);
        var cleanupFailure = await Assert.ThrowsAsync<ApplicationOperationException>(() =>
            cleanup.PreviewAsync(temp.Child("not-opened"), FluffHistory.Core.HistoryCleanupMode.KeepOnlyMostRecent, DateTimeOffset.UtcNow));
        Assert.Equal(ApplicationFailure.Busy, cleanupFailure.Failure);
        var removalFailure = await Assert.ThrowsAsync<ApplicationOperationException>(() => removal.IsCurrentAsync(temp.Child("not-opened"), null!));
        Assert.Equal(ApplicationFailure.Busy, removalFailure.Failure);
        call.Complete(); await BackupCoordinatorTests.Until(coordinator, s => !s.IsAttemptActive);
        using var after = uiGate.TryAcquire(); Assert.NotNull(after); Assert.Equal(0, new FileInfo(path).Length);
    }
    [WindowsFact]
    public async Task RealChildProcessDeathReleasesAdmissionAndStaleMarkerIsReusable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory(); var path = Path.Combine(temp.Child(), "writer.lock");
        var admission = new WindowsOperationAdmission(path);
        await using var child = WriterProcess.Start("admission", temp.Child()); await child.WaitUntilReadyAsync();
        Assert.Null(admission.TryAcquire()); await child.KillAsync();
        using var lease = admission.TryAcquire(); Assert.NotNull(lease); Assert.True(File.Exists(path)); Assert.Equal(0, new FileInfo(path).Length);
    }
    [WindowsFact]
    public async Task NativeCurrentUserPipeReturnsCompleteStateAndSurvivesMalformedAndDisconnectedClients()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        await using var controller = new AgentController(new(temp.Child("settings.json")), SourceFolderPolicy.Empty, (_, _) => throw new InvalidOperationException());
        await controller.InitializeAsync();
        var endpoint = WindowsAgentClient.PipeName + ".test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); var server = new WindowsAgentServer(controller) { Endpoint = endpoint };
        var serving = server.RunAsync(stop.Token); var client = new WindowsAgentClient { Endpoint = endpoint };
        try
        {
            var first = await client.SendAsync(new(AgentOperation.GetState)); Assert.Equal(controller.State.AgentId, first.State.AgentId);
            using (var bad = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await bad.ConnectAsync(5000); await bad.WriteAsync(new byte[12]);
                Assert.Equal(0, await bad.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            using (var disconnected = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous))
            { await disconnected.ConnectAsync(5000); await disconnected.WriteAsync(new byte[] { 1, 2 }); }
            var next = await client.SendAsync(new(AgentOperation.GetState)); Assert.Equal(first.State.AgentId, next.State.AgentId);
            var shutdown = await client.SendAsync(new(AgentOperation.ShutdownForUpdate)); Assert.True(shutdown.State.ShuttingDown);
            await server.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await stop.CancelAsync(); await serving.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
