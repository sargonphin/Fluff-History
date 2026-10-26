using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

/// <summary>Each invocation owns a dedicated thread, opens one repository, runs synchronous Scan,
/// then disposes Engine operation resources and repository keys before returning. Holds no credentials/keys between calls.</summary>
public sealed class EngineBackupAttemptRunner(IWorkerImpact? workerImpact = null, ISourceFolderPolicy? sourcePolicy = null,
    BackupOperationGate? operationGate = null) : IBackupAttemptRunner
{
    private readonly AutomaticRetention _retention = new();
    internal AutomaticMetadataMaintenance MetadataMaintenance { get; init; } = new();
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public async Task<BackupAttemptResult> RunAsync(BackupConfiguration configuration, AttemptTrigger trigger,
        Action<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (operationGate is not null) await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Factory.StartNew(() => Run(configuration, trigger, progress, cancellationToken),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false); }
        finally { operationGate?.Exit(); }
    }

    private BackupAttemptResult Run(BackupConfiguration configuration, AttemptTrigger trigger,
        Action<BackupProgress>? progress, CancellationToken token)
    {
        IWorkerImpactScope? scope = null;
        var impact = new ImpactDiagnostic(ImpactState.NotRequested);
        BackupAttemptResult result;
        RepositoryInspection? opened = null;
        bool scanning = false;
        ScanSnapshot? completedScan = null;
        MaintenanceSummary? completedRetention = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // Keep product-created metadata/maintenance output inside one owned tree, including
            // callers of the Windows composition API that did not supply an explicit workspace.
            if (configuration.ScanOptions.WorkspaceRoot is null)
                configuration = new(configuration.RepositoryPath, configuration.Sources,
                    configuration.ScanOptions with { WorkspaceRoot = BackupWorkspace.DefaultRoot },
                    configuration.Safeguards, configuration.RetentionPolicy) { ForcedSourceIds = configuration.ForcedSourceIds };
            if (trigger == AttemptTrigger.Scheduled)
            {
                if (workerImpact is null) impact = new(ImpactState.Unsupported);
                else
                {
                    try { scope = workerImpact.Enter(); impact = scope.Diagnostic; }
                    catch (Exception) { impact = new(ImpactState.SetupFailed); }
                }
            }
            Report(BackupPhase.OpeningRepository);
            var sources = configuration.Sources;
            if (sourcePolicy is not null)
            {
                // Reclassify production inputs immediately before opening a writer. Crafted settings or
                // a stale UI cannot bypass hard boundaries, force confirmation or parent/child barriers.
                var mappings = sources.Select(s => SourceMapping.FromConfiguration(s.Configuration, s.DirectoryPath) with
                    { ForceInclude = configuration.ForcedSourceIds.Contains(s.Configuration.SourceRootId) });
                sources = sourcePolicy.Select(configuration.RepositoryPath, mappings, configuration.ScanOptions.WorkspaceRoot);
                sourcePolicy.ValidateAliases(configuration.RepositoryPath, sources.Select(s => SourceMapping.FromConfiguration(s.Configuration, s.DirectoryPath) with
                    { ForceInclude = configuration.ForcedSourceIds.Contains(s.Configuration.SourceRootId) }), configuration.ScanOptions.WorkspaceRoot);
            }
            using var repository = Open(configuration.RepositoryPath);
            opened = new(RepositoryReadiness.Ready, repository.IsEncrypted, repository.Identity.RepositoryId, []);
            token.ThrowIfCancellationRequested();
            scanning = true; Report(BackupPhase.Scanning);
            var scan = new FileHistoryEngine(repository, configuration.ScanOptions).Scan(sources, token);
            result = BackupAttemptResult.FromScan(scan);
            if (scan.Outcome == ScanOutcome.Completed && !scan.Partial && !scan.ReconciliationMayBeRequired)
            {
                completedScan = result.Scan;
                Report(BackupPhase.Maintaining);
                completedRetention = _retention.Run(repository, configuration, scan, Clock(), token);
                result = result with { Maintenance = completedRetention };
                result = result with { MetadataMaintenance = token.IsCancellationRequested || completedRetention.State is not
                    (MaintenanceState.NotNeeded or MaintenanceState.Throttled or MaintenanceState.Completed)
                        ? new(MetadataMaintenanceState.NotSafe)
                        : MetadataMaintenance.Run(repository, configuration, scan, completedRetention, Report, token) };
            }
        }
        catch (EncryptionMigrationRequiredException error)
        { opened = RepositorySetupService.MigrationInspection(error.Information); result = new(BackupOutcome.EncryptionMigrationRequired, SafeSummary: "Complete the encryption change before backing up."); }
        catch (WindowsUserProtectorUnavailableException)
        { result = new(BackupOutcome.RecoveryRequired, SafeSummary: "Local Windows unlock unavailable; explicit password recovery is required."); }
        catch (ScanCapacityException error)
        { result = new(BackupOutcome.ScanControlFailure, ScanSnapshot.FromResult(error.Result), SafeSummary: "Scan control failed; inspect reconciliation state.", CommittedProgressMayExist: true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { result = new(BackupOutcome.Cancelled, completedScan, CommittedProgressMayExist: completedScan is null && scanning, Maintenance: completedRetention); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Backup attempt failed ({0}).", error.GetType().Name);
            result = new(scanning ? BackupOutcome.ScanControlFailure : BackupOutcome.RepositoryUnavailable, completedScan,
                SafeSummary: completedScan is not null ? "Metadata maintenance failed. Reopen and inspect before retrying." :
                    scanning ? "Scan failed; committed progress may exist. Reopen and inspect before retrying." : "Repository could not be opened.",
                CommittedProgressMayExist: completedScan is null && scanning, Maintenance: completedRetention);
        }
        finally
        {
            if (scope is not null)
            {
                try { scope.Dispose(); impact = scope.Diagnostic; }
                catch (Exception) { impact = new(ImpactState.RestoreFailed); }
            }
        }
        Report(BackupPhase.Finished);
        return result with { Impact = impact, OpenedRepository = opened };

        void Report(BackupPhase phase)
        { try { progress?.Invoke(new(phase)); } catch (Exception) { /* Observer failures cannot abort repository work. */ } }
    }

    private static Repository Open(string path)
    {
        var discovered = Repository.Discover(path) ?? throw new DirectoryNotFoundException();
        if (!discovered.IsEncrypted) return discovered;
        discovered.Dispose();
        return Repository.OpenWithWindowsUser(path);
    }
}
