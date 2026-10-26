using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using FluffHistory.Core;

namespace FluffHistory.Windows;

/// <summary>Optional read-only display estimates, independent of repository/retention authority.
/// At most one native drive query and one metadata traversal may remain outstanding per window.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsStorageUsage
{
    private readonly object _gate = new();
    private Task<CapacityObservation?>? _drive;
    private Task<long?>? _backup;
    private string _drivePath = "", _backupPath = "";
    public Task<CapacityObservation?> ObserveDriveAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<CapacityObservation?> task;
        lock (_gate)
        {
            if (_drive is { IsCompleted: false })
            {
                if (!string.Equals(path, _drivePath, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<CapacityObservation?>(null);
                task = _drive;
            }
            else { _drivePath = path; task = _drive = Task.Run(() => ReadDrive(path)); }
        }
        return BoundedAsync(task, TimeSpan.FromSeconds(3), token);
    }
    public Task<long?> ObserveBackupAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<long?> task;
        lock (_gate)
        {
            if (_backup is { IsCompleted: false })
            {
                if (!string.Equals(path, _backupPath, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<long?>(null);
                task = _backup;
            }
            else { _backupPath = path; task = _backup = Task.Run(() => ReadBackup(path)); }
        }
        return BoundedAsync(task, TimeSpan.FromSeconds(5), token);
    }
    private static async Task<T?> BoundedAsync<T>(Task<T?> task, TimeSpan timeout, CancellationToken token)
    {
        try { return await task.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { return default; }
    }
    private static CapacityObservation? ReadDrive(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
                path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            if (!Path.EndsInDirectorySeparator(path)) path += Path.DirectorySeparatorChar;
            return GetDiskFreeSpaceEx(path, out var free, out var capacity, IntPtr.Zero) &&
                capacity is > 0 and <= long.MaxValue && free <= capacity ? new((long)capacity, (long)free) : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    private static long? ReadBackup(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) return null;
            var clock = Stopwatch.StartNew(); long bytes = 0; var entries = 0;
            // Transform native directory entries directly: no per-file path/FileInfo allocation,
            // extra stat/open, payload reads, key unlock or repository writer.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint, BufferSize = 64 * 1024 };
            var sizes = new FileSystemEnumerable<long>(path, static (ref FileSystemEntry entry) => entry.IsDirectory ? 0 : entry.Length, options);
            foreach (var length in sizes)
            {
                if (++entries > 1_000_000 || clock.Elapsed > TimeSpan.FromSeconds(5)) return null;
                bytes = checked(bytes + length);
            }
            return bytes;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or OverflowException) { return null; }
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableToCaller, out ulong totalToCaller, IntPtr physicalFree);
}
