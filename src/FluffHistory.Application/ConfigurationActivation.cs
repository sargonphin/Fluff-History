namespace FluffHistory.Application;

public enum BackupAdmission { Started, Busy, NotReady }
public enum ConfigurationSaveNotice { Saved, FoldersForScheduledBackup, FoldersForNextBackup }

/// <summary>Transient setup activation policy. No durable run marker or scheduler-cadence change.</summary>
public sealed class ConfigurationActivation(AppSettings loaded)
{
    private bool _configured = HasFolders(loaded);
    public BackupAdmission? AfterSaved(AppSettings saved, bool ready, Func<BackupAdmission> request)
    {
        if (_configured || !ready || !HasFolders(saved)) return null;
        _configured = true; return request();
    }
    public void Reset() => _configured = false;
    public static bool HasFolders(AppSettings settings) => settings.RepositoryPath.Length != 0 && settings.Sources.Any(s => s.IsProtected && s.RuntimePath is not null);
    public static ConfigurationSaveNotice SavedNotice(AppSettings previous, AppSettings saved)
    {
        static IEnumerable<(Guid, string)> Folders(AppSettings settings) => settings.Sources.Where(s => s.IsProtected && s.RuntimePath is not null)
            .Select(s => (s.SourceRootId, SourcePaths.Normalize(s.RuntimePath!).ToUpperInvariant())).OrderBy(s => s.SourceRootId);
        return previous.RepositoryPath.Length != 0 && !Folders(previous).SequenceEqual(Folders(saved))
            ? saved.SchedulingEnabled ? ConfigurationSaveNotice.FoldersForScheduledBackup : ConfigurationSaveNotice.FoldersForNextBackup
            : ConfigurationSaveNotice.Saved;
    }
    // Kept as developer/legacy diagnostic helpers; Desktop maps semantic values at display time.
    public static string SavedMessage(AppSettings previous, AppSettings saved) => SavedNotice(previous, saved) switch
    {
        ConfigurationSaveNotice.FoldersForScheduledBackup => "Folder changes saved. The updated folders will be included in the next scheduled backup.",
        ConfigurationSaveNotice.FoldersForNextBackup => "Folder changes saved. The updated folders will be included in the next backup.",
        _ => "Configuration saved"
    };
    public static string AdmissionMessage(BackupAdmission admission) => admission switch
    {
        BackupAdmission.Started => "Backup requested",
        BackupAdmission.Busy => "A backup is already running or finishing.",
        _ => "Set up a ready backup location and folders in Configuration."
    };
}
