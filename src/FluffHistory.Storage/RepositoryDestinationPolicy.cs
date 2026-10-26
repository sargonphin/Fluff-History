using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;

namespace FluffHistory.Storage;

/// <summary>Destination admission only. Never traverses or changes OS-owned root metadata.</summary>
public static class RepositoryDestinationPolicy
{
    private static readonly HashSet<string> Directories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    {
        "System Volume Information", "$RECYCLE.BIN", ".fseventsd", ".Spotlight-V100", ".Trashes", ".TemporaryItems",
        ".DocumentRevisions-V100", "lost+found", ".Trash"
    };
    private static readonly HashSet<string> Files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    { ".DS_Store", ".metadata_never_index", ".metadata_never_index_unless_rootfs" };

    public static bool IsEffectivelyEmpty(string directory) => IsEffectivelyEmpty(directory, false);

    internal static bool IsEffectivelyEmpty(string directory, bool ignoreOwnedWriterLock)
    {
        var root = IsLocalVolumeRoot(directory);
        // Native enumeration supplies attributes without opening or descending into protected entries.
        var entries = new FileSystemEnumerable<Entry>(directory, static (ref FileSystemEntry entry) =>
            new(entry.FileName.ToString(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0),
            new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false });
        return !HasUnrelatedEntries(entries, root, ignoreOwnedWriterLock);
    }

    internal readonly record struct Entry(string Name, bool Directory, bool Reparse = false);
    internal static bool HasUnrelatedEntries(IEnumerable<Entry> entries, bool isVolumeRoot, bool ignoreOwnedWriterLock = false) =>
        entries.Any(entry => !(ignoreOwnedWriterLock && entry.Name == RepositoryLayout.WriterLock && !entry.Directory && !entry.Reparse) &&
            !(isVolumeRoot && IsRootArtifact(entry)));

    internal static bool IsRootArtifact(Entry entry)
    {
        if (entry.Reparse) return false;
        if (!entry.Directory) return Files.Contains(entry.Name);
        return Directories.Contains(entry.Name) || entry.Name.StartsWith(".Trash-", StringComparison.Ordinal) &&
            entry.Name.Length > 7 && entry.Name.AsSpan(7).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    internal static bool IsLocalVolumeRoot(string directory)
    {
        try
        {
            var full = RepositoryLayout.NormalizeDirectory(directory);
            if (!OperatingSystem.IsWindows())
                return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal);
            // Mapped drives and UNC shares are not local volume roots. Mounted-directory
            // reparses retain the repository path policy's existing conservative rejection.
            if (WindowsFileSystem.IsNetwork(full)) return false;
            var volume = new StringBuilder(32768);
            return GetVolumePathName(full, volume, volume.Capacity) &&
                string.Equals(full, Path.TrimEndingDirectorySeparator(volume.ToString()), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
}
