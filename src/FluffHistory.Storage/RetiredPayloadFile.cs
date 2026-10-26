using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Storage;

// Exact generated names only. Never clears attributes/permissions or follows a final symlink.
internal static class RetiredPayloadFile
{
    internal static void Delete(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var native = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
                path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            using var handle = CreateFile(native, 0x10000 | 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) throw Failure();
            var attributes = File.GetAttributes(handle);
            Format.Require(GetFileType(handle) == 1 &&
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) == 0, "retired payload handle type");
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw Failure();
            return; // Deletion of this opened file occurs on close, not a second path-based lookup.
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Retired payload cleanup supports Windows and Linux.");
        var descriptor = OpenUnix(Path.GetDirectoryName(path)!, 0x80000 | 0x20000 | 0x10000);
        if (descriptor < 0) throw Failure();
        using var parent = new SafeFileHandle((IntPtr)descriptor, true);
        using var file = OpenRetired(path);
        Format.Require(file.CanSeek, "non-ordinary retired payload");
        if (Statx(file.SafeFileHandle.DangerousGetHandle().ToInt32(), "", 0x1000 | 0x100, 0x7ff, out var opened) != 0) throw Failure();
        if (Statx(descriptor, Path.GetFileName(path), 0x100, 0x7ff, out var named) != 0) throw Failure();
        Format.Require((opened.Mask & 0x101) == 0x101 && (named.Mask & 0x101) == 0x101 &&
            (opened.Mode & 0xf000) == 0x8000 && (named.Mode & 0xf000) == 0x8000 &&
            opened.Inode == named.Inode && opened.DeviceMajor == named.DeviceMajor && opened.DeviceMinor == named.DeviceMinor, "retired payload substitution/type");
        if (UnlinkAt(descriptor, Path.GetFileName(path), 0) != 0) throw Failure();
    }
    private static FileStream OpenRetired(string path)
    {
        try { return PayloadInput.Open(path); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 40 })
        { throw Format.Invalid("retired payload symlink substitution"); }
    }
    private static IOException Failure()
    {
        var error = Marshal.GetLastPInvokeError();
        return error == 2 || OperatingSystem.IsWindows() && error == 3
            ? new FileNotFoundException("Retired payload name is absent.")
            : new IOException("Cannot delete retired payload.", new Win32Exception(error));
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int OpenUnix(string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, string path, int flags, uint mask, out LinuxStatx result);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int UnlinkAt(int descriptor, string path, int flags);
}
