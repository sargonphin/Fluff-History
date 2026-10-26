using System.IO.Hashing;
using System.Security.AccessControl;
using System.Security.Principal;
using FluffHistory.Engine;

namespace FluffHistory.Windows;

// Only the selected restore/preview output is allowed to leave encryption at rest.
internal static class RestrictedDirectory
{
    internal static void Create(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Plaintext staging collision.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

internal sealed class PlaintextStaging : IDisposable
{
    private string? _directory;
    private readonly List<string> _files = [];
    internal long BytesWritten { get; private set; }
    internal int FilesVerified { get; private set; }
    internal IReadOnlyList<PlannedRestoreFile> Materialize(DisplayedRestorePlan plan, CancellationToken token)
    {
        if (!plan.Repository.IsEncrypted) return plan.Files;
        var root = Path.Combine(Path.GetTempPath(), "Fluff-History", "Shell"); RestoreDestination.CheckParents(root, create: true);
        _directory = Path.Combine(root, "FluffHistory-Shell-" + Guid.NewGuid().ToString("N")); RestrictedDirectory.Create(_directory);
        var result = new List<PlannedRestoreFile>(); var buffer = new byte[128 * 1024];
        try
        {
            foreach (var file in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(_directory, file.Version.Identity.VersionId.ToString("N") + ".plain");
                using var payload = plan.Reader.OpenVersionPayload(file.Version.Identity) ?? throw new IOException("Selected version no longer survives.");
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                _files.Add(target); var hash = new XxHash64(); long total = 0;
                int count;
                while ((count = payload.Content.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested(); total = checked(total + count);
                    if (total > file.Version.Metadata.Length) throw new InvalidDataException("Staged plaintext length exceeded.");
                    hash.Append(buffer.AsSpan(0, count)); output.Write(buffer, 0, count); BytesWritten += count;
                }
                if (total != file.Version.Metadata.Length || Convert.ToHexString(hash.GetCurrentHash()) != file.Version.Checksum.Value)
                    throw new InvalidDataException("Staged plaintext length/checksum verification failed.");
                output.Flush(true); FilesVerified++; result.Add(file with { PayloadPath = target });
            }
            return result;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }
    public void Dispose()
    {
        foreach (var path in _files) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        if (_directory is not null) { try { Directory.Delete(_directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
