using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluffHistory.Storage;

public sealed record RecoveryCredentialCoordinate(string Resource, string Name)
{
    public static RecoveryCredentialCoordinate ForRepository(Guid repositoryId) =>
        new("FluffHistory-Recovery-v1-" + repositoryId.ToString("N"), "FluffHistory");
}

/// <summary>Prerelease compatibility only. No creation, secret reading or decryption capability.</summary>
public interface ILegacyRecoveryCredentialCleanup
{
    bool Exists(RecoveryCredentialCoordinate coordinate);
    void Remove(RecoveryCredentialCoordinate coordinate);
}

// Historical schema is kept solely for bounded exact retirement, never recovery.
internal sealed record ExperimentalRecoverySidecar(string Product, int Schema, Guid RepositoryId,
    string Resource, string Name, string ProtectedKey)
{
    internal const string FileName = "microsoft-account-recovery.json";
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal byte[] Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal static bool Exists(string directory)
    {
        try { _ = File.GetAttributes(Path.Combine(directory, FileName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal static void Validate(string directory, Guid repositoryId)
    {
        var bytes = AuthorityFiles.ReadSmall(Path.Combine(directory, FileName));
        try
        {
            var value = JsonSerializer.Deserialize<ExperimentalRecoverySidecar>(bytes, Options) ?? throw Format.Invalid("null legacy recovery sidecar");
            var coordinate = RecoveryCredentialCoordinate.ForRepository(repositoryId);
            Format.Require(value.Bytes().AsSpan().SequenceEqual(bytes) && value.Product == "Fluff-History" && value.Schema == 1 &&
                value.RepositoryId == repositoryId && value.Resource == coordinate.Resource && value.Name == coordinate.Name &&
                Convert.FromBase64String(value.ProtectedKey).Length is > 0 and <= 4096, "legacy recovery sidecar identity/schema");
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentNullException)
        { throw new InvalidDataException("Invalid legacy recovery sidecar."); }
    }
}

public sealed partial class Repository
{
    public void RemoveLegacyMicrosoftRecovery(ILegacyRecoveryCredentialCleanup cleanup, CancellationToken token = default)
    {
        CheckUnlocked(); token.ThrowIfCancellationRequested(); using var writer = OpenWriter();
        writer.RetireLegacyMicrosoftRecovery(cleanup, token);
    }
}

public sealed partial class RepositoryWriter
{
    internal void RetireLegacyMicrosoftRecoveryForConversion(EncryptionMigrationControl control,
        ILegacyRecoveryCredentialCleanup cleanup, CancellationToken token)
    {
        ValidateConversionControl(control);
        Format.Require(control.SourceEncrypted && !control.TargetEncrypted && control.RepositoryId == _repository.Identity.RepositoryId,
            "conversion legacy recovery retirement identity");
        RetireLegacyMicrosoftRecovery(cleanup, token);
    }
    internal void RetireLegacyMicrosoftRecovery(ILegacyRecoveryCredentialCleanup cleanup, CancellationToken token)
    {
        Check(); token.ThrowIfCancellationRequested(); _ = _repository.ReadAuthority();
        if (ExperimentalRecoverySidecar.Exists(_repository.DirectoryPath))
        {
            ExperimentalRecoverySidecar.Validate(_repository.DirectoryPath, _repository.Identity.RepositoryId);
            try { File.Delete(Path.Combine(_repository.DirectoryPath, ExperimentalRecoverySidecar.FileName)); }
            catch { if (ExperimentalRecoverySidecar.Exists(_repository.DirectoryPath)) throw; }
            Format.Require(!ExperimentalRecoverySidecar.Exists(_repository.DirectoryPath), "legacy recovery removal readback");
        }
        var coordinate = RecoveryCredentialCoordinate.ForRepository(_repository.Identity.RepositoryId);
        try { cleanup.Remove(coordinate); }
        catch { if (cleanup.Exists(coordinate)) throw; }
    }
}
