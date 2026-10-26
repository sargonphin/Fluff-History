using System.Text.RegularExpressions;
using FluffHistory.Storage;

namespace FluffHistory.Tests.Infrastructure;

internal static class SmbDiagnosticCopies
{
    private static readonly HashSet<string> ServerCreatedChildren = new(StringComparer.OrdinalIgnoreCase);
    // Frozen fixtures only. A server-side copy gives a new name/inode never previously
    // read/written by this Windows client; server/OS caches remain uncontrolled.
    internal static string? CreateServerClone(string sourceName, string targetName)
    {
        var serverRoot = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_SERVER_ROOT");
        if (string.IsNullOrEmpty(serverRoot)) return null;
        if (!Regex.IsMatch(serverRoot, "^/[a-zA-Z0-9/_-]+$") || !Regex.IsMatch(sourceName, "^[a-zA-Z0-9_-]+$") || !Regex.IsMatch(targetName, "^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("Diagnostic clone paths must be exact safe internally generated names.");
        SmbServerCounters.Command($"sudo -n cp -a --reflink=auto -- '{serverRoot}/{sourceName}' '{serverRoot}/{targetName}'");
        ServerCreatedChildren.Add(targetName);
        return Path.Combine(SmbNetworkMeasurementTests.Remote, targetName);
    }

    internal static string? CreateFreshFile(string ownedDirectoryName, string sourceName)
    {
        var serverRoot = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_SERVER_ROOT");
        if (string.IsNullOrEmpty(serverRoot)) return null;
        if (!Regex.IsMatch(serverRoot, "^/[a-zA-Z0-9/_-]+$") ||
            !Regex.IsMatch(ownedDirectoryName, "^[a-zA-Z0-9_-]+[0-9a-f]{32}$") ||
            !Regex.IsMatch(sourceName, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"))
            throw new ArgumentException("Fresh-file diagnostics require exact safe generated child and file names.");
        var target = "cold-" + Guid.NewGuid().ToString("N") + "-" + sourceName;
        SmbServerCounters.Command($"sudo -n cp -a --reflink=auto -- '{serverRoot}/{ownedDirectoryName}/{sourceName}' '{serverRoot}/{ownedDirectoryName}/{target}'");
        ServerCreatedChildren.Add(ownedDirectoryName);
        return Path.Combine(SmbNetworkMeasurementTests.Remote, ownedDirectoryName, target);
    }

    internal static bool DeleteServerCreatedChild(string root, string child)
    {
        var name = Path.GetFileName(child);
        if (!ServerCreatedChildren.Contains(name)) return false;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(SmbNetworkMeasurementTests.Remote)), StringComparison.OrdinalIgnoreCase)) return false;
        var serverRoot = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_SERVER_ROOT")!;
        // Windows can retain stale negative directory attributes for directories copied
        // out of band on the server, even while direct child file reads succeed. Clean
        // ONLY this process's generated diagnostic copies through the same server that
        // created them. Product retirement continues to use ordinary SMB delete/sharing.
        var exact = serverRoot + "/" + name;
        SmbServerCounters.Command($"test \"$(sudo -n realpath -e -- '{exact}')\" = '{exact}' && sudo -n rm -rf -- '{exact}'");
        ServerCreatedChildren.Remove(name); return true;
    }
}
