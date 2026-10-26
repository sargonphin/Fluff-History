using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Tests.Infrastructure;

// Engineering admission only. This must never constrain product repository locations.
internal static class LargeDiagnosticRoot
{
    // Over six times the observed 2.42 GB decoded Baseline, allowing concurrent old/new
    // metadata, compaction scratch and comfortable margin. Not a reservation or retention policy.
    internal const long MinimumAvailableBytes = 16L * 1024 * 1024 * 1024;
    private const string Guidance = "Large diagnostics require explicit disposable/high-capacity storage; keep bulk fixtures outside source, release and persistent storage.";
    private static readonly string[] UnsafeComponents = [".git", "projects", "release", "releases", "artifacts", "bin", "obj", "build"];

    internal static string FromEnvironment(IReadOnlyList<string> childDirectories, [CallerFilePath] string sourceFile = "")
    {
        var optIn = Environment.GetEnvironmentVariable("FLUFF_HISTORY_DIAGNOSTICS");
        var worktree = FindWorktree(sourceFile);
        var storage = new LocalStorage();
        return Admit(optIn, Environment.GetEnvironmentVariable("FLUFF_HISTORY_M222_ROOT"), worktree, storage, childDirectories);
    }

    internal static string Admit(string? optIn, string? requested, string worktree, Storage storage,
        IReadOnlyList<string>? childDirectories = null)
    {
        if (optIn != "1") throw Refuse("Set FLUFF_HISTORY_DIAGNOSTICS=1 to opt in.");
        if (string.IsNullOrWhiteSpace(requested)) throw Refuse("Set an explicit fully qualified diagnostic root.");
        try
        {
            var full = storage.FullPath(requested);
            CheckPath(full, storage.IsWindows);
            var source = storage.FullPath(worktree);
            CheckOverlap(full, source, storage.IsWindows);
            var resolved = storage.Resolve(full);
            var resolvedSource = storage.Resolve(source);
            CheckPath(resolved.Path, storage.IsWindows);
            // Check both spellings: neither an alias into the worktree nor a worktree
            // symlink pointing out to disposable storage is a valid fixture location.
            CheckOverlap(resolved.Path, resolvedSource.Path, storage.IsWindows);
            if (storage.IsWindows && resolved.Volume is null) throw Refuse("Cannot identify the destination volume.");
            if (storage.IsWindows && storage.PersistentVolume is { } persistent &&
                string.Equals(persistent, resolved.Volume, StringComparison.OrdinalIgnoreCase))
                throw Refuse("The root resolves onto the persistent P: volume.");
            if (storage.HasProjectAncestor(resolved.ExistingDirectory))
                throw Refuse("An existing ancestor contains a source-project marker.");
            var available = storage.AvailableBytes(resolved.ExistingDirectory);
            if (available < MinimumAvailableBytes)
                throw Refuse($"At least 16 GiB available to the caller is required; observed {available} bytes.");
            foreach (var child in childDirectories ?? [])
            {
                var childRoot = Admit(optIn, Path.Combine(resolved.Path, child), worktree, storage);
                if (!Within(childRoot, resolved.Path, storage.IsWindows))
                    throw Refuse("A fixture child directory redirects outside the admitted root.");
            }
            return resolved.Path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            throw Refuse("Cannot safely resolve the root and establish available space: " + error.Message, error);
        }
    }

    private static void CheckPath(string path, bool windows)
    {
        var normalized = windows ? path.Replace('\\', '/') : path;
        if (normalized == "/" || (windows && normalized.Length == 3 && normalized[1..] == ":/"))
            throw Refuse("Select an owned subdirectory, not a filesystem root.");
        if (windows && normalized.StartsWith("P:/", StringComparison.OrdinalIgnoreCase))
            throw Refuse("Persistent P: storage is forbidden for large fixtures.");
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(component =>
            UnsafeComponents.Contains(component, windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)))
            throw Refuse("Project, release and build-output locations are forbidden for large fixtures.");
    }

    private static void CheckOverlap(string root, string source, bool windows)
    {
        if (Within(root, source, windows) || Within(source, root, windows))
            throw Refuse("The diagnostic root overlaps the active source repository/worktree.");
    }

    private static bool Within(string path, string parent, bool windows)
    {
        if (windows) { path = path.Replace('\\', '/'); parent = parent.Replace('\\', '/'); }
        path = path.TrimEnd('/'); parent = parent.TrimEnd('/');
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(parent, comparison) || path.StartsWith(parent + "/", comparison);
    }

    private static InvalidOperationException Refuse(string reason, Exception? inner = null) => new(reason + " " + Guidance, inner);

    private static string FindWorktree(string sourceFile)
    {
        for (var directory = Path.GetDirectoryName(sourceFile); directory is not null; directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "FluffHistory.sln"))) return directory;
        throw Refuse("Cannot identify the active source worktree.");
    }

    internal readonly record struct Resolution(string Path, string ExistingDirectory, string? Volume = null);

    // Narrow injection surface: policy tests never create/populate a large fixture.
    internal abstract class Storage
    {
        internal abstract bool IsWindows { get; }
        internal abstract string FullPath(string path);
        internal abstract Resolution Resolve(string fullPath);
        internal abstract long AvailableBytes(string existingDirectory);
        internal abstract string? PersistentVolume { get; }
        internal virtual bool HasProjectAncestor(string existingDirectory) => false;
    }

    internal sealed class LocalStorage : Storage
    {
        internal override bool IsWindows => OperatingSystem.IsWindows();
        internal override string FullPath(string path)
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("Use a fully qualified path.");
            var full = System.IO.Path.GetFullPath(path);
            // Device/UNC aliases are unnecessary for this local, large diagnostic.
            if (IsWindows && (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\'))
                throw new ArgumentException("Use an ordinary local drive path.");
            if (IsWindows && full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(part =>
                part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':')))
                throw new ArgumentException("Ambiguous Windows path components are forbidden.");
            return System.IO.Path.TrimEndingDirectorySeparator(full);
        }

        internal override Resolution Resolve(string fullPath)
        {
            var suffix = new Stack<string>();
            var existing = fullPath;
            while (true)
            {
                try
                {
                    if ((File.GetAttributes(existing) & FileAttributes.Directory) == 0)
                        throw new IOException("An existing path component is not a directory.");
                    break;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    suffix.Push(System.IO.Path.GetFileName(existing));
                    existing = System.IO.Path.GetDirectoryName(existing) ?? throw new IOException("No existing ancestor.", error);
                }
            }
            string resolved; string? volume = null;
            if (IsWindows)
            {
                using var handle = OpenDirectory(existing);
                resolved = FinalPath(handle, 0);
                if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new IOException("Unrecognized final path.");
                resolved = FullPath(resolved[4..]);
                volume = Volume(handle);
            }
            else resolved = ResolveUnixDirectory(existing);
            var ancestor = resolved;
            while (suffix.TryPop(out var component)) resolved = System.IO.Path.Combine(resolved, component);
            return new(resolved, ancestor, volume);
        }

        internal override string? PersistentVolume
        {
            get
            {
                if (!IsWindows) return null;
                try { return Resolve(@"P:\").Volume; }
                catch (IOException error) when (error.InnerException is FileNotFoundException or DirectoryNotFoundException) { return null; }
            }
        }

        internal override long AvailableBytes(string existingDirectory)
        {
            if (!IsWindows) return new DriveInfo(existingDirectory).AvailableFreeSpace;
            var directory = Path.EndsInDirectorySeparator(existingDirectory) ? existingDirectory : existingDirectory + "\\";
            if (!GetDiskFreeSpaceEx(directory, out var available, out _, out _) || available > long.MaxValue)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot observe caller-visible free space.");
            return (long)available;
        }

        internal override bool HasProjectAncestor(string existingDirectory)
        {
            for (var directory = new DirectoryInfo(existingDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
                    Directory.EnumerateFiles(directory.FullName, "*.sln*").Any() || Directory.EnumerateFiles(directory.FullName, "*.*proj").Any())
                    return true;
            return false;
        }

        private static string ResolveUnixDirectory(string path, int depth = 0)
        {
            if (depth > 40) throw new IOException("Too many directory links.");
            var current = System.IO.Path.GetPathRoot(path)!;
            foreach (var component in path[current.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var item = new DirectoryInfo(System.IO.Path.Combine(current, component));
                current = item.ResolveLinkTarget(true) is { } target ? ResolveUnixDirectory(target.FullName, depth + 1) : item.FullName;
            }
            if ((File.GetAttributes(current) & FileAttributes.Directory) == 0) throw new IOException("Link target is not a directory.");
            return current;
        }

        private static SafeFileHandle OpenDirectory(string path)
        {
            var handle = CreateFile(path, 0, 7, 0, 3, 0x02000000, 0); // Follow links; FILE_FLAG_BACKUP_SEMANTICS.
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError(); handle.Dispose();
            throw new Win32Exception(error, "Cannot resolve existing directory.");
        }

        private static string FinalPath(SafeFileHandle handle, uint flags)
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, flags);
            if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve final directory path.");
            return buffer.ToString();
        }

        private static string Volume(SafeFileHandle handle)
        {
            var path = FinalPath(handle, 1); // VOLUME_NAME_GUID, also catches drive/mount aliases of P:.
            var end = path.IndexOf('}');
            if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
                throw new IOException("Cannot identify the destination volume.");
            return path[..(end + 1)];
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
    }
}
