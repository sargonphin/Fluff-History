using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>Windows composition for the agent's backup owner. Caller owns/disposes the coordinator.</summary>
public static class WindowsBackupRuntime
{
    public static BackupCoordinator CreateCoordinator(BackupConfiguration configuration, BackupSchedule schedule, TimeProvider? timeProvider = null,
        ISourceFolderPolicy? sourcePolicy = null, BackupOperationGate? operationGate = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows backup runtime requires Windows.");
        return new(configuration, schedule, new EngineBackupAttemptRunner(new WindowsWorkerImpact(), sourcePolicy ?? new WindowsSourceFolders()),
            new BackupSafeguards(new WindowsPowerStateProvider(), new WindowsNetworkStateProvider(), new RepositoryPathNetworkClassifier()),
            timeProvider, operationGate);
    }
}
