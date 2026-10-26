using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Storage;

/// <summary>Format-independent persistent OS-handle ownership. Creation deliberately uses CreateNew elsewhere.</summary>
internal static class RepositoryWriterLock
{
    internal static FileStream Open(string directory)
    {
        var path = Path.Combine(directory, RepositoryLayout.WriterLock);
        RepositoryLayout.RequireFile(path);
        return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    internal static void Check(FileStream handle) => _ = RandomAccess.GetLength(handle.SafeFileHandle);
    internal const int ProbeBufferBytes = 16 * 1024;
    // FileStreamInfo reaches the tested SMB server on every query, including prewarmed
    // handles; length/empty reads/flush/byte locking can succeed after revocation.
    // Unsupported servers fail closed. The retained handle is never reopened here.
    internal static void ProbeServerHandle(FileStream handle, byte[]? buffer = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        buffer ??= new byte[ProbeBufferBytes];
        if (!GetFileInformation(handle.SafeFileHandle, 7, buffer, (uint)buffer.Length))
            throw new IOException("Cannot validate the retained network writer handle.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformation(SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint size);
}
