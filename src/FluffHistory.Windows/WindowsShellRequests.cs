using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>Transient same-user input, never authority. Exact token, bounded file and no reparse traversal.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsShellRequests
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluff-History", "ShellRequests");
    public static Task<ShellOpenRequest?> ConsumeAsync(string token) => Task.Run(() => Consume(token));
    public static Task CleanupStaleAsync() => Task.Run(() =>
    {
        try
        {
            CheckParents(DirectoryPath); var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(DirectoryPath).Take(64))
            {
                var name = Path.GetFileName(path);
                if (!name.EndsWith(".request", StringComparison.Ordinal) || !ShellWire.IsToken(name[..^8])) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                CheckParents(DirectoryPath); File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    });
    private static ShellOpenRequest? Consume(string token)
    {
        if (!ShellWire.IsToken(token)) return null;
        var path = Path.Combine(DirectoryPath, token + ".request");
        try
        {
            CheckParents(DirectoryPath);
            using var handle = CreateFileW(path, 0x80000000, 1, 0, 3, 0x00200000, 0); // OPEN_REPARSE_POINT
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTag>()) ||
                (info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0) return null;
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length < ShellWire.HeaderBytes || stream.Length > ShellWire.HeaderBytes + ShellWire.MaximumBodyBytes) return null;
            var data = new byte[(int)stream.Length]; stream.ReadExactly(data);
            return ShellWire.Decode(data) as ShellOpenRequest;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        finally
        {
            // Only this generated name, and only after verifying its parent namespace.
            try { CheckParents(DirectoryPath); File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private static void CheckParents(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException("Shell request directory is unavailable.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
}
