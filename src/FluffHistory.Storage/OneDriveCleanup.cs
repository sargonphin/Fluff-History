using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluffHistory.Storage;

/// <summary>Exact, user-protected cleanup coordinates. Never history or an unlock credential.</summary>
public sealed class OneDriveCleanupTicket
{
    internal OneDriveCleanupTicket(OneDriveCleanupRecord record, byte[] bytes)
    { Record = record; Bytes = bytes; }
    internal OneDriveCleanupRecord Record { get; }
    internal byte[] Bytes { get; }
    public Guid OperationId => Record.OperationId;
    public OneDriveRecoveryReference Reference => Record.Reference;
}

internal sealed record OneDriveCleanupRecord(string Product, int Schema, Guid OperationId,
    OneDriveRecoveryReference Reference, string SourceBootstrapSha256, string TargetBootstrapSha256)
{
    internal const int MaximumRecords = 8;
    internal const string Prefix = "onedrive-cleanup-";
    internal string Name => Prefix + OperationId.ToString("N") + ".dat";
    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FluffHistory/onedrive-exact-cleanup/v1");
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, OneDriveRecoverySidecar.Options);
    internal static IEnumerable<string> Paths(string directory)
    {
        // Local bounded bookkeeping only. This never lists OneDrive content.
        var paths = Directory.EnumerateFiles(directory, Prefix + "*.dat").Take(MaximumRecords + 1).ToArray();
        Format.Require(paths.Length <= MaximumRecords, "OneDrive cleanup record limit; explicit cleanup is required");
        return paths.Order(StringComparer.Ordinal);
    }
    internal static OneDriveCleanupTicket Read(string path, Guid repositoryId)
    {
        var bytes = AuthorityFiles.ReadSmall(path);
        var clear = WindowsKeyProtector.Unprotect(bytes, Entropy);
        try
        {
            var value = JsonSerializer.Deserialize<OneDriveCleanupRecord>(clear, OneDriveRecoverySidecar.Options)
                ?? throw Format.Invalid("null OneDrive cleanup record");
            Format.Require(value.Encode().AsSpan().SequenceEqual(clear) && value.Product == "Fluff-History" &&
                value.Schema == 1 && value.OperationId != Guid.Empty && value.Reference.RepositoryId == repositoryId &&
                Path.GetFileName(path) == value.Name, "OneDrive cleanup record binding");
            OneDriveRecoverySidecar.ValidateReference(value.Reference);
            Format.Require(value.SourceBootstrapSha256.Length == 64 && value.TargetBootstrapSha256.Length == 64 &&
                Convert.FromHexString(value.SourceBootstrapSha256).Length == 32 &&
                Convert.FromHexString(value.TargetBootstrapSha256).Length == 32, "OneDrive cleanup bootstrap binding");
            return new(value, bytes);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Invalid OneDrive cleanup record.", error); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    internal void Install(string directory)
    {
        var path = Path.Combine(directory, Name);
        if (Exists(path))
        {
            Format.Require(Read(path, Reference.RepositoryId).Record == this, "OneDrive cleanup predecessor changed");
            return;
        }
        Format.Require(Paths(directory).Count() < MaximumRecords, "OneDrive cleanup record limit; explicit cleanup is required");
        var clear = Encode();
        byte[] protectedBytes;
        try { protectedBytes = WindowsKeyProtector.Protect(clear, Entropy); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var candidate = "onedrive-cleanup-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            AuthorityFiles.WriteNew(Path.Combine(directory, candidate), protectedBytes);
            Format.Require(Read(Path.Combine(directory, candidate), Reference.RepositoryId, this).Record == this,
                "OneDrive cleanup candidate readback");
            try { File.Move(Path.Combine(directory, candidate), path); }
            catch { if (!Exists(path) || Read(path, Reference.RepositoryId).Record != this) throw; }
            Format.Require(Read(path, Reference.RepositoryId).Record == this, "OneDrive cleanup installed readback");
        }
        finally
        {
            try { File.Delete(Path.Combine(directory, candidate)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    // Authenticate the candidate through its final logical name before publication.
    private static OneDriveCleanupTicket Read(string candidate, Guid repositoryId, OneDriveCleanupRecord expected)
    {
        var bytes = AuthorityFiles.ReadSmall(candidate);
        var clear = WindowsKeyProtector.Unprotect(bytes, Entropy);
        try
        {
            Format.Require(expected.Reference.RepositoryId == repositoryId && clear.AsSpan().SequenceEqual(expected.Encode()),
                "OneDrive cleanup candidate identity");
            return new(expected, bytes);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}

public sealed partial class Repository
{
    /// <summary>Passive local status only. No DPAPI, sign-in or cloud traffic.</summary>
    public static bool HasPendingOneDriveCleanup(string directory)
        => OneDriveCleanupRecord.Paths(RepositoryLayout.NormalizeDirectory(directory)).Any();
}

public sealed partial class RepositoryWriter
{
    internal void AdmitOneDriveCleanupForConversion()
    {
        Check();
        Format.Require(OneDriveCleanupRecord.Paths(_repository.DirectoryPath).Count() < OneDriveCleanupRecord.MaximumRecords,
            "OneDrive cleanup record limit; explicit cleanup is required before decryption");
    }
    /// <summary>Requires verified ordinary repository access after conversion. Moving it within
    /// the same Windows profile is supported; missing/foreign/corrupt records never authorize guessing.</summary>
    public IReadOnlyList<OneDriveCleanupTicket> ReadPendingOneDriveCleanup()
    {
        Check(); RequireNoCandidates(); RequireIdle();
        Format.Require(!EncryptionMigrationControl.Exists(_repository.DirectoryPath), "OneDrive cleanup requires completed conversion");
        _ = _repository.ReadAuthority();
        OneDriveRecoveryReference? active = null;
        if (OneDriveRecoverySidecar.Exists(_repository.DirectoryPath))
        {
            Format.Require(_repository.IsEncrypted, "unexpected recovery on plaintext cleanup target");
            active = OneDriveRecoverySidecar.Read(_repository.DirectoryPath, _repository.Identity.RepositoryId).Reference;
        }
        // Re-encryption does not inherit or consume cleanup. A later explicit retry can remove an
        // older retired generation without requiring another decryption (or deadlocking the cap).
        var bootstrap = RepositoryBootstrap.Plain(_repository.Identity).Bytes();
        return OneDriveCleanupRecord.Paths(_repository.DirectoryPath).Select(path =>
        {
            var ticket = OneDriveCleanupRecord.Read(path, _repository.Identity.RepositoryId);
            Format.Require(ticket.Record.TargetBootstrapSha256 == Convert.ToHexString(SHA256.HashData(bootstrap)),
                "OneDrive cleanup target changed");
            Format.Require(active is null || active.GenerationId != ticket.Reference.GenerationId && active.ItemId != ticket.Reference.ItemId,
                "OneDrive cleanup cannot retire active recovery");
            return ticket;
        }).ToArray();
    }
    /// <summary>Call only after verified exact conditional deletion (including its already-absent result). The writer remains
    /// owned across cloud work; acknowledgement cannot remove a changed local record.</summary>
    public void AcknowledgeOneDriveCleanup(OneDriveCleanupTicket ticket)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        var current = ReadPendingOneDriveCleanup().SingleOrDefault(value => value.OperationId == ticket.OperationId);
        Format.Require(current is not null && current.Record == ticket.Record && current.Bytes.AsSpan().SequenceEqual(ticket.Bytes),
            "OneDrive cleanup acknowledgement predecessor changed");
        var path = Path.Combine(_repository.DirectoryPath, ticket.Record.Name);
        try { File.Delete(path); }
        catch { if (OneDriveCleanupRecord.Exists(path)) throw; }
        Format.Require(!OneDriveCleanupRecord.Exists(path), "OneDrive cleanup acknowledgement readback");
    }
}
