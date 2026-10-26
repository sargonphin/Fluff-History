#if FH_UPDATE_TESTING
using System.Net;
using FluffHistory.Application;
using FluffHistory.Windows;

namespace FluffHistory.Agent;

/// <summary>Compiled ONLY into explicitly marked disposable campaign builds. Never enabled by user settings/environment.</summary>
internal sealed class DevelopmentUpdateHttpHandler : HttpMessageHandler
{
    internal static BackupCoordinator CreateCoordinator(BackupConfiguration configuration, BackupSchedule schedule, ISourceFolderPolicy policy, BackupOperationGate gate) =>
        new(configuration, schedule, new ControlledRunner(new EngineBackupAttemptRunner(new WindowsWorkerImpact(), policy)),
            new BackupSafeguards(new WindowsPowerStateProvider(), new WindowsNetworkStateProvider(), new RepositoryPathNetworkClassifier()), operationGate: gate);
    private sealed class ControlledRunner(IBackupAttemptRunner actual) : IBackupAttemptRunner
    {
        public async Task<BackupAttemptResult> RunAsync(BackupConfiguration configuration, AttemptTrigger trigger, Action<BackupProgress>? progress, CancellationToken cancellationToken)
        {
            var marker = Path.Combine(WindowsProductPaths.Root, "UpdateTest", "backup-hold");
            Check(marker);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
            try
            {
                if (File.Exists(marker)) File.WriteAllText(marker + ".entered", "entered");
                while (File.Exists(marker) && DateTimeOffset.UtcNow < deadline) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await actual.RunAsync(configuration, trigger, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(BackupOutcome.Cancelled); }
        }
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.Combine(WindowsProductPaths.Root, "UpdateTest");
        Check(root);
        var path = Path.Combine(root, "releases.json");
        if (request.RequestUri != GitHubReleaseSource.Endpoint)
        {
            if (File.Exists(path) && new FileInfo(path).Length > GitHubReleaseSource.MaximumJsonBytes) throw new IOException("Controlled release fixture too large.");
            var releases = File.Exists(path) ? GitHubReleaseSource.Parse(File.ReadAllBytes(path)) : [];
            var release = releases.SingleOrDefault(r => r.DownloadUrl == request.RequestUri?.AbsoluteUri);
            if (release is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            path = Path.Combine(root, "Assets", release.Version.ToString(), UpdateRelease.InstallerName);
        }
        Check(path);
        if (!File.Exists(path)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) };
        return Task.FromResult(response);
    }
    private static void Check(string path)
    {
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Controlled update source cannot be a link.");
    }
}
#endif
