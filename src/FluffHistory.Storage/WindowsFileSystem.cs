using System.Runtime.InteropServices;

namespace FluffHistory.Storage;

internal static class WindowsFileSystem
{
    internal static bool IsNetwork(string directory)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (directory.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal) && !directory.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        var kind = DriveType(directory);
        if (kind is 0 or 1) throw new IOException("Cannot identify the repository filesystem.");
        return kind == 4;
    }
    internal static uint DriveType(string directory) => GetDriveType(Path.GetPathRoot(directory)!);
    internal static string Native(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetDriveType(string root);
}
