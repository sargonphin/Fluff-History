using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Engine;

internal readonly record struct SourceObservation(FileAttributes Attributes, long Length, DateTimeOffset CreationUtc, DateTimeOffset LastWriteUtc)
{
    internal bool Directory => (Attributes & FileAttributes.Directory) != 0;
    internal bool Unsupported => (Attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.Device)) != 0;
    internal bool Matches(SourceObservation other) => !other.Unsupported && !other.Directory &&
        Length == other.Length && LastWriteUtc == other.LastWriteUtc && CreationUtc == other.CreationUtc;
}

// Narrow native source seam for deterministic enumeration/read/instability tests, not a provider framework.
internal class SourceAccess
{
    internal virtual IEnumerable<string> EnumerateNames(string directory)
    {
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory,
            "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false }))
            yield return Path.GetFileName(path);
    }
    internal virtual SourceObservation Observe(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) != 0)
            return new(attributes, 0, default, default);
        var file = new FileInfo(path); file.Refresh();
        return new(attributes, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);
    }
    internal virtual Stream OpenRead(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            // Nonblocking open prevents unsupported FIFOs/devices from hanging a scan;
            // O_NOFOLLOW also closes the final-component symlink race on Linux.
            var descriptor = OpenUnix(path, 0x80000 | 0x20000 | 0x800);
            if (descriptor < 0) throw OpenError(Marshal.GetLastPInvokeError());
            var safe = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            try
            {
                var file = new FileStream(safe, FileAccess.Read, 1, isAsync: false);
                if (file.CanSeek) return file;
                file.Dispose(); throw new IOException("Unsupported nonseekable source.");
            }
            catch { safe.Dispose(); throw; }
        }
        if (!OperatingSystem.IsWindows()) return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        // Do not follow a final-component reparse replacement between observation and open.
        var handle = CreateFile(PhysicalPaths.ForNativeOpen(path), 0x80000000, 7, IntPtr.Zero, 3, 0x00200000 | 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
        { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw OpenError(error); }
        try { return new FileStream(handle, FileAccess.Read, 1, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }
    private static IOException OpenError(int code)
    {
        var native = new Win32Exception(code); return new($"Cannot open source ({code}): {native.Message}", native);
    }

    internal virtual SourceObservation ObserveHandle(Stream stream)
    {
        var file = (FileStream)stream;
        return new(File.GetAttributes(file.SafeFileHandle), file.Length,
            File.GetCreationTimeUtc(file.SafeFileHandle), File.GetLastWriteTimeUtc(file.SafeFileHandle));
    }
    internal virtual int Read(Stream stream, Span<byte> buffer) => stream.Read(buffer);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(string path, int flags);
}

internal sealed class SourceFailureException(Exception cause, SourceProblemKind kind = SourceProblemKind.Access) : IOException("Source observation/copy did not succeed.", cause)
{ internal SourceProblemKind Kind { get; } = kind; }
