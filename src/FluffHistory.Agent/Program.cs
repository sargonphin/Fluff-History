using FluffHistory.Application;
using FluffHistory.Windows;
using System.Reflection;
using System.Text.Json;

namespace FluffHistory.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            var client = new WindowsAgentClient();
            if (args is ["--shutdown-for-update"]) return await client.ShutdownForUpdateAsync() ? 0 : 2;
            if (args is ["--state"])
            { Console.WriteLine(JsonSerializer.Serialize((await client.SendAsync(new(AgentOperation.GetState))).State)); return 0; }
            if (args is ["--run-now"])
            { var result = await client.SendAsync(new(AgentOperation.RunNow)); return result.Admission == BackupAdmission.Started ? 0 : 2; }
            if (args.Length != 0) return 1;
            using var starting = WindowsStartupAdmission.TryAcquire();
            if (starting is null) return 2;
            using var singleton = new WindowsOperationAdmission(WindowsProductPaths.AgentLock).TryAcquire();
            if (singleton is null) return 0;
            var gate = new BackupOperationGate(new WindowsOperationAdmission(WindowsProductPaths.MutationLock));
            var policy = new WindowsSourceFolders([WindowsProductPaths.Root, WindowsProductPaths.InstallRoot]);
            await policy.InitializeAsync();
            await using var controller = new AgentController(new(WindowsProductPaths.Settings), policy,
#if FH_UPDATE_TESTING
                (configuration, schedule) => DevelopmentUpdateHttpHandler.CreateCoordinator(configuration, schedule, policy, gate),
#else
                (configuration, schedule) => WindowsBackupRuntime.CreateCoordinator(configuration, schedule, sourcePolicy: policy, operationGate: gate),
#endif
                productVersion: Assembly.GetExecutingAssembly().GetName().Version!.ToString(),
                lastStatusStore: new(WindowsProductPaths.LastBackupStatus));
            using var updateHttp = new HttpClient(
#if FH_UPDATE_TESTING
                new DevelopmentUpdateHttpHandler()
#else
                new HttpClientHandler { AllowAutoRedirect = false }
#endif
                );
            var identity = Assembly.GetExecutingAssembly();
            var signer = ApprovedUpdateSignerPolicy.FromAssembly(identity);
            var staging = new WindowsUpdateStaging(updateHttp, new WindowsUpdateFileVerifier(signer));
            using var installer = new WindowsUpdateInstaller(staging, gate, () => controller.State.Backup?.IsAttemptActive == true,
                controller.CancelActiveAsync, identity, signer);
            await using var updates = new UpdateCoordinator(new GitHubReleaseSource(updateHttp, InstalledReleaseIdentity.FromAssembly(identity)), installer,
                new(WindowsProductPaths.UpdatePreferences, UpdatePreferences.Default, p => p.Validate(), 4096),
                new(WindowsProductPaths.UpdateState, UpdateDiskState.Empty, s => s.Validate()),
                InstalledReleaseIdentity.FromAssembly(identity),
#if FH_UPDATE_TESTING
                () => new(true, true),
#else
                WindowsUpdateNetwork.Read,
#endif
                resultStore: new(WindowsProductPaths.UpdateResult, null, r => (r ?? throw new InvalidDataException()).Validate(), 4096));
            controller.AttachUpdates(updates);
            using var stopping = new CancellationTokenSource();
            // Accept status/control while slow configuration inspection is pending.
            var host = new WindowsAgentServer(controller);
            var server = host.RunAsync(stopping.Token);
            starting.Dispose(); // The listening endpoint now exists; setup can request graceful shutdown.
            var initialize = InitializeAsync();
            async Task InitializeAsync() { await controller.InitializeAsync(); updates.Start(); }
            await Task.WhenAny(host.ShutdownRequested, server, updates.HandoffReady);
            await controller.ShutdownAsync();
            await initialize;
            await stopping.CancelAsync(); await server;
            return 0;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Background agent unavailable ({0}).", error.GetType().Name);
            return 1;
        }
    }
}
