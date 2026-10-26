using System.ComponentModel;
using System.Runtime.InteropServices;
using FluffHistory.Core;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Storage;

/// <summary>A read-only handle for one exact surviving version. The consumer verifies its recorded checksum inline.</summary>
public sealed class VersionPayload : IDisposable
{
    public StoredVersion Version { get; }
    public Stream Content { get; }
    internal VersionPayload(StoredVersion version, Stream content) { Version = version; Content = content; }
    public void Dispose() => Content.Dispose();
}

public sealed partial class RepositoryReader
{
    public VersionPayload? OpenVersionPayload(VersionIdentity identity)
    {
        var version = GetVersion(identity); if (version is null) return null;
        RequirePayloadStillSurvives(identity);
        try
        {
            _ = RepositoryLayout.NormalizeDirectory(_repository.DirectoryPath);
            _repository.CheckBootstrap();
            var path = _repository.GetPayloadPath(identity.VersionId); RepositoryLayout.RequireFile(path);
            var file = PayloadInput.Open(path);
            try
            {
                var attributes = File.GetAttributes(file.SafeFileHandle);
                Format.Require(file.CanSeek && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) == 0,
                    "non-ordinary payload");
                Format.Require(file.Length == _repository.EstimatePayloadPhysicalBytes(version.Metadata.Length), "payload length");
                return new(version, _repository.Crypto is null ? file : new EncryptedPayload.Reader(_repository, identity.VersionId, file, version.Metadata.Length));
            }
            catch { file.Dispose(); throw; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RequirePayloadStillSurvives(identity); throw;
        }
    }
    private void RequirePayloadStillSurvives(VersionIdentity identity)
    {
        using var fresh = _repository.OpenReader();
        if (fresh.Authority == Authority) return;
        var key = PathKeys.Version(identity); var address = fresh.VersionIndex.Find(fresh.Captured.Root.Versions, key);
        if (address is null) throw new PayloadVersionExpiredException(identity);
        Format.Require(fresh.Version(address, identity.Path, key).Identity == identity, "payload identity changed at exact key");
    }
}

internal static class PayloadInput
{
    internal static FileStream Open(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            var native = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
                path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            handle = CreateFile(native, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | 0x08000000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("Cannot open immutable payload.", new Win32Exception(error)); }
        }
        else if (OperatingSystem.IsLinux())
        {
            var fd = OpenUnix(path, 0x80000 | 0x20000 | 0x800);
            if (fd < 0) throw new IOException("Cannot open immutable payload.", new Win32Exception(Marshal.GetLastPInvokeError()));
            handle = new((IntPtr)fd, ownsHandle: true);
        }
        else throw new PlatformNotSupportedException("Payload restore handles are implemented for Windows and Linux.");
        try { return new(handle, FileAccess.Read, 1, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(string path, int flags);
}
