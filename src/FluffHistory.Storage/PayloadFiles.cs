using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluffHistory.Storage;

// A final payload install must never fall back to cross-volume copying or replace an existing version.
internal static class PayloadFiles
{
    internal static void MoveNew(string source, string destination)
    {
        bool moved;
        if (OperatingSystem.IsWindows()) moved = MoveFileEx(WindowsPath(source), WindowsPath(destination), 0);
        else if (OperatingSystem.IsLinux()) moved = RenameNoReplace(-100, source, -100, destination, 1) == 0;
        else throw new PlatformNotSupportedException("Immutable payload installation is implemented for Windows and Linux.");
        if (!moved)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException("Non-overwriting, same-filesystem payload installation failed.", new Win32Exception(error));
        }
    }
    private static string WindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameNoReplace(int sourceDirectory, string source, int destinationDirectory, string destination, uint flags);
}
