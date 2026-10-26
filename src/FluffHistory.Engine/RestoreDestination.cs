using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluffHistory.Engine;

internal static class RestoreDestination
{
    internal static string Validate(string destination, string repository)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("An absolute restore destination is required.", nameof(destination));
        var path = PhysicalPaths.Normalize(destination);
        if (PhysicalPaths.Overlaps(PhysicalPaths.OverlapKey(path), PhysicalPaths.OverlapKey(PhysicalPaths.Normalize(repository))))
            throw new ArgumentException("Restore destination/repository nesting is forbidden.", nameof(destination));
        return path;
    }
    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static void CheckParents(string parent, bool create)
    {
        var components = new Stack<string>();
        for (string? p = parent; p is not null; p = Path.GetDirectoryName(p)) components.Push(p);
        while (components.TryPop(out var path))
        {
            var attributes = Attributes(path);
            if (attributes is null && create) { Directory.CreateDirectory(path); attributes = File.GetAttributes(path); }
            if (attributes is not null && (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != FileAttributes.Directory)
                throw new IOException("Restore parent is not an ordinary directory.");
        }
    }
    internal static bool TargetExists(string path)
    {
        var attributes = Attributes(path); if (attributes is null) return false;
        if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) != 0)
            throw new IOException("Restore target conflicts with a directory/reparse/unsupported object.");
        return true;
    }
    internal static void Install(string temporary, string destination, bool overwrite)
    {
        bool success;
        if (OperatingSystem.IsWindows()) success = MoveFileEx(PhysicalPaths.ForNativeOpen(temporary), PhysicalPaths.ForNativeOpen(destination), overwrite ? 1u : 0u);
        else if (OperatingSystem.IsLinux()) success = overwrite ? Rename(temporary, destination) == 0 : RenameNew(-100, temporary, -100, destination, 1) == 0;
        else throw new PlatformNotSupportedException("Restore installation is supported on Windows and Linux.");
        if (!success) throw new IOException("Restore installation failed; final destination outcome was not confirmed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string source, string destination);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameNew(int sourceDirectory, string source, int destinationDirectory, string destination, uint flags);
}
