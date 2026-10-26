using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Engine;

internal static class PhysicalPaths
{
    internal static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);
    private static bool Within(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows()) return full;
        full = OrdinaryWindowsPath(full);
        // Expand existing DOS short-name ancestors as well as extended drive/UNC spellings
        // before root overlap checks. Missing/inaccessible source scopes remain observable skips.
        var suffix = new Stack<string>(); var candidate = full; var buffer = new StringBuilder(32768);
        while (true)
        {
            var length = GetLongPathName(ForNativeOpen(candidate), buffer, buffer.Capacity);
            if (length > 0 && length < buffer.Capacity)
            {
                var expanded = OrdinaryWindowsPath(buffer.ToString());
                while (suffix.TryPop(out var name)) expanded = Path.Combine(expanded, name);
                return Path.TrimEndingDirectorySeparator(expanded);
            }
            if (Marshal.GetLastWin32Error() is not (2 or 3)) return full;
            var parent = Path.GetDirectoryName(candidate); if (parent is null) return full;
            suffix.Push(Path.GetFileName(candidate)); candidate = parent;
        }
    }
    internal static string ForNativeOpen(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    internal static string OverlapKey(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var suffix = new Stack<string>(); var candidate = path; var buffer = new StringBuilder(32768);
        while (true)
        {
            // Metadata-only directory handles resolve drive substitutions/mount spellings.
            // This is transient root safety, never logical or persisted file identity.
            using var handle = OpenDirectory(ForNativeOpen(candidate), 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var length = FinalPath(handle, buffer, buffer.Capacity, 1); // volume GUID for local aliases
                if (length == 0) length = FinalPath(handle, buffer, buffer.Capacity, 0); // UNC/DOS fallback
                if (length > 0 && length < buffer.Capacity)
                {
                    var resolved = buffer.ToString();
                    while (suffix.TryPop(out var name)) resolved = Path.Combine(resolved, name);
                    return Path.TrimEndingDirectorySeparator(resolved);
                }
                return path;
            }
            if (Marshal.GetLastPInvokeError() is not (2 or 3)) return path;
            var parent = Path.GetDirectoryName(candidate); if (parent is null) return path;
            suffix.Push(Path.GetFileName(candidate)); candidate = parent;
        }
    }
    private static string OrdinaryWindowsPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 && path[5] == ':') path = path[4..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Use an ordinary absolute drive or UNC source/repository location.", nameof(path));
        if (path.Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith(' ') || p.EndsWith('.')))
            throw new ArgumentException("Ambiguous trailing-dot/space physical components are unsupported.", nameof(path));
        return path;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int length);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FinalPath(SafeFileHandle handle, StringBuilder path, int capacity, uint flags);
}
