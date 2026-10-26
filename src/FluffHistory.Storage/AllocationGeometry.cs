using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Trusted filesystem allocation unit; not an exact filesystem-overhead model.</summary>
public sealed record AllocationGeometry
{
    public long AllocationUnit { get; }
    public AllocationGeometry(long allocationUnit)
    { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allocationUnit); AllocationUnit = allocationUnit; }
    public static long RoundUp(long bytes, long allocationUnit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allocationUnit);
        return checked(bytes / allocationUnit * allocationUnit + (bytes % allocationUnit == 0 ? 0 : allocationUnit));
    }
}

public sealed partial class Repository
{
    /// <summary>Repository-bound native Windows geometry. Capacity writers stream existing
    /// metadata and charge future scratch appends, permitting same-volume workspace. UNC requires local workspace and native
    /// directory geometry/volume observations. Unsupported identity probes fail explicitly.</summary>
    public AllocationGeometry ObserveAllocationGeometry(string? workspaceRoot = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native allocation geometry is currently Windows-only. Inject a trusted geometry probe on other platforms.");
        _ = ReadAuthority();
        var workspace = RepositoryLayout.NormalizeDirectory(workspaceRoot ?? Path.GetTempPath());
        // NormalizeDirectory validates ordinary existing ancestors and allows an absent final root.
        while (!Directory.Exists(workspace)) workspace = Path.GetDirectoryName(workspace) ?? throw new IOException("No existing workspace ancestor.");
        if (WindowsFileSystem.IsNetwork(workspace) || WindowsFileSystem.DriveType(workspace) is not (2 or 3 or 6))
            throw new NotSupportedException("Capacity-enabled backup requires a local metadata workspace with an observable volume identity.");
        var workspaceVolume = Mount(workspace);
        _ = Volume(workspaceVolume); // Preserve native local volume-GUID support requirements.
        string geometryPath;
        if (WindowsFileSystem.IsNetwork(DirectoryPath))
        {
            geometryPath = WindowsFileSystem.Native(DirectoryPath);
            if (!Path.EndsInDirectorySeparator(geometryPath)) geometryPath += Path.DirectorySeparatorChar;
            // Remote workspaces remain unsupported. The local scratch append bound
            // is charged conservatively even though the repository is remote.
        }
        else
        {
            geometryPath = Mount(DirectoryPath);
            _ = Volume(geometryPath);
        }
        if (!GetDiskFreeSpace(geometryPath, out var sectors, out var sectorBytes, out _, out _)) throw NativeError();
        if (sectors == 0 || sectorBytes == 0) throw new IOException("The filesystem reported invalid allocation geometry.");
        _ = ReadAuthority(); return new(checked((long)sectors * sectorBytes));
    }
    private static string Mount(string path)
    {
        var buffer = new StringBuilder(32768);
        if (!GetVolumePathName(path, buffer, buffer.Capacity)) throw NativeError();
        return buffer.ToString();
    }
    private static string Volume(string mount)
    {
        var buffer = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(mount, buffer, buffer.Capacity)) throw NativeError();
        return buffer.ToString();
    }
    private static IOException NativeError() => new("Cannot observe repository allocation geometry/volume identity.", new Win32Exception(Marshal.GetLastPInvokeError()));
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpace(string root, out uint sectors, out uint sectorBytes, out uint free, out uint total);
}
