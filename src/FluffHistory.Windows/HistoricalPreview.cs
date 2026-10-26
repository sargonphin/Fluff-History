using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Windows;

internal interface IHistoricalFileLauncher
{
    bool Open(string path, nint owner); // False only for ERROR_NO_ASSOCIATION; other failures throw.
    void OpenWith(string path, nint owner);
}

public sealed class HistoricalPreview(Repository repository)
{
    public Task<PreviewLease> OpenAsync(VersionIdentity identity, nint ownerWindow = 0, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Historical open requires Windows Shell.");
        return StaOperation.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return Open(identity, new NativeHistoricalLauncher(), Path.Combine(Path.GetTempPath(), "Fluff-History", "Previews"), ownerWindow, cancellationToken);
        });
    }
    internal PreviewLease Open(VersionIdentity identity, IHistoricalFileLauncher launcher, string previewRoot,
        nint owner = 0, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var reader = repository.OpenReader();
        var version = reader.GetVersion(identity) ?? throw new FileNotFoundException("Exact historical preview version does not survive.");
        previewRoot = RestoreDestination.Validate(previewRoot, repository.DirectoryPath);
        RestoreDestination.CheckParents(previewRoot, create: true);
        var directory = Path.Combine(previewRoot, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Generated preview directory already exists.");
        if (repository.IsEncrypted) RestrictedDirectory.Create(directory); else Directory.CreateDirectory(directory);
        var name = version.Metadata.RelativePath.Split('/')[^1]; var ownership = Guid.NewGuid().ToString("N");
        var marker = Path.Combine(directory, ".fluff-preview-" + ownership + ".owner");
        if (string.Equals(Path.GetFileName(marker), name, StringComparison.OrdinalIgnoreCase)) marker += ".marker";
        using (var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { var bytes = System.Text.Encoding.ASCII.GetBytes(ownership); output.Write(bytes); }
        var target = Path.Combine(directory, name);
        var lease = new PreviewLease(directory, target, marker, ownership);
        try
        {
            var result = new RestoreEngine(repository).RestoreFile(identity, target, cancellationToken: token);
            if (result.Cancelled) throw new OperationCanceledException(token);
            if (result.Restored != 1) throw new IOException("Historical preview materialization failed: " + result.Problems.FirstOrDefault()?.Reason);
            token.ThrowIfCancellationRequested();
            if (!launcher.Open(target, owner)) launcher.OpenWith(target, owner);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
}

/// <summary>Caller retains the lease while the preview may be used. Cleanup is exact, best effort and never recursive.</summary>
public sealed class PreviewLease : IDisposable
{
    private readonly string _directory, _marker, _ownership;
    public string FilePath { get; }
    internal PreviewLease(string directory, string file, string marker, string ownership)
    { _directory = directory; FilePath = file; _marker = marker; _ownership = ownership; }
    public void Dispose()
    {
        try
        {
            RestoreDestination.CheckParents(_directory, create: false);
            if (RestoreDestination.Attributes(_marker) is not { } attributes ||
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || File.ReadAllText(_marker) != _ownership) return;
            // A preview application may add files. Leave the lease intact rather than sweep its output.
            if (Directory.EnumerateFileSystemEntries(_directory).Any(p => p != FilePath && p != _marker)) return;
            if (RestoreDestination.Attributes(FilePath) is { } fileAttributes)
            {
                if ((fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return;
                File.SetAttributes(FilePath, FileAttributes.Normal); File.Delete(FilePath);
            }
            File.Delete(_marker); Directory.Delete(_directory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class NativeHistoricalLauncher : IHistoricalFileLauncher
{
    public bool Open(string path, nint owner)
    {
        // NOASYNC completes DDE before the operation STA exits. NO_UI lets the caller handle a failed launch,
        // including the explicit normal Windows Open With fallback, without a second Shell error dialog.
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x100 | 0x400,
            Owner = owner, Verb = "open", File = path, Show = 1 };
        if (ShellExecuteEx(ref info)) return true;
        var error = Marshal.GetLastPInvokeError(); if (error == 1155) return false;
        throw new Win32Exception(error, "Windows could not open the historical preview.");
    }
    public void OpenWith(string path, nint owner)
    {
        var info = new OpenAsInfo { File = path, Flags = 4 }; // OAIF_EXEC: normal Windows chooser opens this one file.
        Marshal.ThrowExceptionForHR(SHOpenWithDialog(owner, in info));
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo { [MarshalAs(UnmanagedType.LPWStr)] public string File; [MarshalAs(UnmanagedType.LPWStr)] public string? Class; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public nint Owner;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show; public nint Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey; public uint HotKey; public nint IconOrMonitor, Process;
    }
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHOpenWithDialog(nint owner, in OpenAsInfo info);
}
