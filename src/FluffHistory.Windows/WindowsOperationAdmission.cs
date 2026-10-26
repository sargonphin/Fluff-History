using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>Persistent marker, transient exclusive handle. Never delete/recreate a lock to acquire it.
/// Process death releases the handle; lease release has no async/thread-affinity requirement.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsOperationAdmission(string filePath) : IOperationAdmission
{
    private readonly string _path = Path.GetFullPath(filePath);
    public IDisposable? TryAcquire()
    {
        var directory = Path.GetDirectoryName(_path)!;
        CheckParents(directory); Directory.CreateDirectory(directory); CheckParents(directory);
        if (File.Exists(_path) && (File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Application admission marker is not an ordinary file.");
        try { return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }
    internal static void CheckParents(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Application directories cannot be reparse points.");
    }
}

/// <summary>Canonical retained user state, independent of package removal and installed binary version.</summary>
public static class WindowsProductPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluff-History");
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Fluff-History");
    public static string Data => Path.Combine(Root, "AppData");
    public static string Settings => Path.Combine(Data, "settings-v2.json");
    public static string Preferences => Path.Combine(Data, "preferences-v1.json");
    public static string LastBackupStatus => Path.Combine(Data, "last-backup-status-v1.json");
    public static string UpdatePreferences => Path.Combine(Data, "update-preferences-v1.json");
    public static string UpdateState => Path.Combine(Data, "update-state-v1.json");
    public static string UpdateResult => Path.Combine(Data, "update-result-v1.json");
    public static string UpdateLock => Path.Combine(Root, "Runtime", "update.lock");
    public static string MutationLock => Path.Combine(Root, "Runtime", "mutation.lock");
    public static string AgentLock => Path.Combine(Root, "Runtime", "agent.lock");
    public static string InstallationLock => Path.Combine(Root, "Runtime", "installation.lock");
}

/// <summary>Brief shared startup lease; setup takes the same marker exclusively before stopping processes.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsStartupAdmission
{
    public static IDisposable? TryAcquire()
    {
        var update = TryShared(WindowsProductPaths.UpdateLock);
        if (update is null) return null;
        try
        {
            var installation = TryShared(WindowsProductPaths.InstallationLock);
            if (installation is not null) return new StartupLease(update, installation);
            update.Dispose(); return null;
        }
        catch { update.Dispose(); throw; }
    }
    private sealed class StartupLease(IDisposable update, IDisposable installation) : IDisposable
    { public void Dispose() { installation.Dispose(); update.Dispose(); } }
    private static IDisposable? TryShared(string path)
    {
        WindowsOperationAdmission.CheckParents(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (!File.Exists(path)) { using var created = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid installation admission marker.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }
}
