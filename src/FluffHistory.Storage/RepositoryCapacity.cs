using System.ComponentModel;
using System.Runtime.InteropServices;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal static class RepositoryCapacity
{
    internal static CapacityObservation Observe(Repository repository)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native caller/quota-visible capacity is currently supported on Windows. No quota-blind volume fallback is used.");
        _ = repository.ReadAuthority(); // Includes identity, ordinary directory/ancestor and control validation.
        var observation = ObserveDirectory(repository.DirectoryPath);
        _ = repository.ReadAuthority();
        return observation;
    }
    internal static CapacityObservation ObserveDirectory(string path)
    {
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        if (!Path.EndsInDirectorySeparator(path)) path += Path.DirectorySeparatorChar;
        if (!GetDiskFreeSpaceEx(path, out var free, out var capacity, IntPtr.Zero))
            throw new IOException("Cannot observe caller-visible repository capacity.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new(checked((long)capacity), checked((long)free));
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableToCaller, out ulong totalToCaller, IntPtr physicalFree);
}
