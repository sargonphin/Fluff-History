using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Nonsecret exact cloud coordinates. Never an email or an OAuth identity.</summary>
public sealed record OneDriveRecoveryReference(Guid RepositoryId, Guid GenerationId, string ItemId,
    string FileName, string ETag, string CloudSecretSha256);

/// <summary>Invocation-owned random cloud half. The repository master is never part of this object.</summary>
public sealed class OneDriveRecoverySecret : IDisposable
{
    public Guid RepositoryId { get; }
    public Guid GenerationId { get; }
    private byte[]? _key;
    internal ReadOnlySpan<byte> Key => _key ?? throw new ObjectDisposedException(nameof(OneDriveRecoverySecret));
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Key)).ToLowerInvariant();
    private OneDriveRecoverySecret(Guid repositoryId, Guid generationId, byte[] ownedKey)
    { RepositoryId = repositoryId; GenerationId = generationId; _key = ownedKey; }
    public static OneDriveRecoverySecret Create(Guid repositoryId)
    {
        if (repositoryId == Guid.Empty) throw new ArgumentException("Missing repository identity.", nameof(repositoryId));
        return new(repositoryId, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
    }
    public OneDriveRecoveryReference Reference(string itemId, string eTag) => new(RepositoryId, GenerationId, itemId,
        $"recovery-{RepositoryId:N}-{GenerationId:N}.json", eTag, Sha256);
    public byte[] EncodeCloudObject() => JsonSerializer.SerializeToUtf8Bytes(new CloudObject("Fluff-History", 1,
        "repository-recovery", RepositoryId, GenerationId, Convert.ToBase64String(Key)), OneDriveRecoverySidecar.Options);
    public static OneDriveRecoverySecret ParseCloudObject(ReadOnlySpan<byte> bytes, OneDriveRecoveryReference expected)
    {
        Format.Require(bytes.Length is > 0 and <= 16 * 1024, "cloud recovery object bound");
        byte[]? key = null;
        try
        {
            var value = JsonSerializer.Deserialize<CloudObject>(bytes, OneDriveRecoverySidecar.Options) ?? throw Format.Invalid("null cloud recovery object");
            Format.Require(value.Product == "Fluff-History" && value.Schema == 1 && value.Purpose == "repository-recovery" &&
                value.RepositoryId == expected.RepositoryId && value.GenerationId == expected.GenerationId &&
                value.RepositoryId != Guid.Empty && value.GenerationId != Guid.Empty, "cloud recovery object binding");
            key = Convert.FromBase64String(value.Key);
            Format.Require(key.Length == 32, "cloud recovery key length");
            Format.Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), Convert.FromHexString(expected.CloudSecretSha256)), "cloud recovery key hash");
            // Canonical roundtrip also rejects duplicate fields, whitespace, alternate spellings and unknown fields.
            var canonical = JsonSerializer.SerializeToUtf8Bytes(value, OneDriveRecoverySidecar.Options);
            try { Format.Require(bytes.SequenceEqual(canonical), "noncanonical cloud recovery object"); }
            finally { CryptographicOperations.ZeroMemory(canonical); }
            var result = new OneDriveRecoverySecret(value.RepositoryId, value.GenerationId, key); key = null; return result;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentNullException)
        { throw new InvalidDataException("Invalid OneDrive recovery object."); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }
    private sealed record CloudObject(string Product, int Schema, string Purpose, Guid RepositoryId, Guid GenerationId, string Key);
}

internal sealed record OneDriveRecoverySidecar(string Product, int Schema, string Provider, Guid RepositoryId,
    Guid GenerationId, string ItemId, string FileName, string ETag, string CloudSecretSha256, string ProtectedKey)
{
    internal const string Name = "onedrive-account-recovery.json";
    internal static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    internal byte[] Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal OneDriveRecoveryReference Reference => new(RepositoryId, GenerationId, ItemId, FileName, ETag, CloudSecretSha256);
    internal static bool Exists(string directory)
    {
        try { _ = File.GetAttributes(Path.Combine(directory, Name)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal static OneDriveRecoverySidecar Read(string directory, Guid repositoryId)
    {
        var bytes = AuthorityFiles.ReadSmall(Path.Combine(directory, Name));
        try
        {
            var value = JsonSerializer.Deserialize<OneDriveRecoverySidecar>(bytes, Options) ?? throw Format.Invalid("null OneDrive recovery sidecar");
            Format.Require(bytes.AsSpan().SequenceEqual(value.Bytes()) && value.Product == "Fluff-History" && value.Schema == 1 &&
                value.Provider == "onedrive-appfolder-v1" && value.RepositoryId == repositoryId && repositoryId != Guid.Empty && value.GenerationId != Guid.Empty,
                "OneDrive recovery sidecar identity/schema");
            ValidateReference(value.Reference);
            Format.Require(Convert.FromBase64String(value.ProtectedKey).Length == 128, "OneDrive recovery envelope bound");
            return value;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentNullException)
        { throw new InvalidDataException("Invalid OneDrive recovery sidecar."); }
    }
    internal static void ValidateReference(OneDriveRecoveryReference value)
    {
        Format.Require(value.RepositoryId != Guid.Empty && value.GenerationId != Guid.Empty && value.ItemId is { Length: > 0 and <= 512 } &&
            !value.ItemId.Any(c => c < 33 || c > 126 || c is '/' or '\\' or '?' or '#') &&
            value.FileName == $"recovery-{value.RepositoryId:N}-{value.GenerationId:N}.json" &&
            value.ETag is { Length: > 0 and <= 1024 } && !value.ETag.Any(char.IsControl) &&
            value.CloudSecretSha256 is { Length: 64 } && value.CloudSecretSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "OneDrive recovery coordinates");
    }
    internal static byte[] Context(RepositoryBootstrap bootstrap, OneDriveRecoveryReference reference)
    {
        ValidateReference(reference);
        var domain = Encoding.UTF8.GetBytes($"FluffHistory/v4/m20.3/onedrive-appfolder-recovery/v1/{reference.RepositoryId:N}/{reference.GenerationId:N}/{reference.ItemId}/");
        return SHA256.HashData(domain.Concat(bootstrap.Binding()).ToArray());
    }
}

public sealed class OneDriveRecoveryUncertainException()
    : IOException("OneDrive recovery sidecar installation could not be confirmed. Verify before retrying.");
internal enum OneDriveRecoveryStage { CandidateFlushed, BeforeInstall, AfterInstall, BeforeConfirmation, AfterRemoval }

public sealed partial class Repository
{
    internal Action<OneDriveRecoveryStage>? OneDriveRecoveryObserved { get; set; }
    public static OneDriveRecoveryReference? InspectOneDriveRecovery(string directory)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory); EncryptionMigrationControl.RefuseOrdinaryOpen(directory);
        var bootstrap = RepositoryBootstrap.Read(directory);
        if (!OneDriveRecoverySidecar.Exists(directory)) return null;
        Format.Require(bootstrap.Encrypted, "OneDrive recovery on plaintext repository");
        return OneDriveRecoverySidecar.Read(directory, bootstrap.RepositoryId).Reference;
    }
    /// <summary>Non-authoritative cleanup coordinates only; never opens mixed maintenance data.</summary>
    public static OneDriveRecoveryReference? InspectOneDriveRecoveryForEncryptionChange(string directory)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory);
        if (!EncryptionMigrationControl.Exists(directory)) return InspectOneDriveRecovery(directory);
        var control = EncryptionMigrationControl.Read(directory);
        return control.SourceEncrypted && OneDriveRecoverySidecar.Exists(directory)
            ? OneDriveRecoverySidecar.Read(directory, control.RepositoryId).Reference : null;
    }
    public static Repository OpenWithOneDriveRecovery(string directory, OneDriveRecoverySecret secret)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory); EncryptionMigrationControl.RefuseOrdinaryOpen(directory);
        var bootstrap = RepositoryBootstrap.Read(directory);
        Format.Require(bootstrap.Encrypted, "OneDrive recovery on plaintext repository");
        var sidecar = OneDriveRecoverySidecar.Read(directory, bootstrap.RepositoryId);
        Format.Require(secret.RepositoryId == sidecar.RepositoryId && secret.GenerationId == sidecar.GenerationId &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret.Key), Convert.FromHexString(sidecar.CloudSecretSha256)), "OneDrive recovery secret binding");
        var context = OneDriveRecoverySidecar.Context(bootstrap, sidecar.Reference);
        using var wrapper = new RepositoryCryptography(secret.Key.ToArray(), context);
        var packet = wrapper.Unprotect(Convert.FromBase64String(sidecar.ProtectedKey), "onedrive-appfolder-master-v1");
        try
        {
            Format.Require(packet.Length == 64 && CryptographicOperations.FixedTimeEquals(packet.AsSpan(0, 32), context), "OneDrive recovery packet binding");
            var crypto = new RepositoryCryptography(packet.AsSpan(32, 32).ToArray(), bootstrap.Binding());
            try
            {
                var repository = OpenCore(directory, bootstrap, crypto);
                try { _ = repository.ReadAuthority(); return repository; }
                catch { repository.Dispose(); throw; }
            }
            catch { crypto.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(packet); }
    }
    public void PublishOneDriveRecovery(OneDriveRecoverySecret secret, OneDriveRecoveryReference reference,
        AuthorityIdentity expectedAuthority, Guid? previousGeneration, CancellationToken token = default)
    {
        CheckUnlocked();
        if (!IsEncrypted) throw new InvalidOperationException("OneDrive recovery requires encryption.");
        using var writer = OpenWriter(); writer.PublishOneDriveRecovery(secret, reference, expectedAuthority, previousGeneration, token);
    }
    public void RemoveOneDriveRecovery(Guid expectedGeneration, CancellationToken token = default)
    {
        CheckUnlocked(); using var writer = OpenWriter(); writer.RemoveOneDriveRecovery(expectedGeneration, token);
    }
}

public sealed partial class RepositoryWriter
{
    internal void PublishOneDriveRecovery(OneDriveRecoverySecret secret, OneDriveRecoveryReference reference,
        AuthorityIdentity expectedAuthority, Guid? previousGeneration, CancellationToken token)
    {
        Check(); RequireNoCandidates(); RequireIdle(); token.ThrowIfCancellationRequested();
        var authority = _repository.ReadAuthority();
        Format.Require(authority.Identity == expectedAuthority, "OneDrive recovery authority predecessor changed");
        var bootstrap = RepositoryBootstrap.Read(_repository.DirectoryPath);
        var context = OneDriveRecoverySidecar.Context(bootstrap, reference);
        Format.Require(reference.RepositoryId == bootstrap.RepositoryId && secret.RepositoryId == reference.RepositoryId &&
            secret.GenerationId == reference.GenerationId && secret.Sha256 == reference.CloudSecretSha256, "OneDrive recovery publication binding");
        byte[]? prior = null;
        if (OneDriveRecoverySidecar.Exists(_repository.DirectoryPath))
        {
            var old = OneDriveRecoverySidecar.Read(_repository.DirectoryPath, bootstrap.RepositoryId);
            Format.Require(previousGeneration == old.GenerationId, "OneDrive recovery generation predecessor changed"); prior = old.Bytes();
        }
        else Format.Require(previousGeneration is null, "OneDrive recovery predecessor missing");
        using var wrapper = new RepositoryCryptography(secret.Key.ToArray(), context);
        var envelope = _repository.Crypto!.ProtectRecoveryPacket(wrapper, "onedrive-appfolder-master-v1", context);
        var sidecar = new OneDriveRecoverySidecar("Fluff-History", 1, "onedrive-appfolder-v1", reference.RepositoryId,
            reference.GenerationId, reference.ItemId, reference.FileName, reference.ETag, reference.CloudSecretSha256, Convert.ToBase64String(envelope));
        var bytes = sidecar.Bytes(); Format.Require(bytes.Length <= AuthorityFiles.MaximumControlBytes, "OneDrive recovery sidecar size");
        string? candidate = null; bool attempted = false;
        try
        {
            var name = $"onedrive-recovery-{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(Path.Combine(_repository.DirectoryPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { candidate = name; stream.Write(bytes); stream.Flush(true); }
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, candidate)).AsSpan().SequenceEqual(bytes), "OneDrive recovery candidate readback");
            _repository.OneDriveRecoveryObserved?.Invoke(OneDriveRecoveryStage.CandidateFlushed);
            token.ThrowIfCancellationRequested(); Check();
            Format.Require(bootstrap == RepositoryBootstrap.Read(_repository.DirectoryPath) && _repository.ReadAuthority().Identity == expectedAuthority, "OneDrive recovery predecessor changed");
            Format.Require(prior is null ? !OneDriveRecoverySidecar.Exists(_repository.DirectoryPath) :
                AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, OneDriveRecoverySidecar.Name)).AsSpan().SequenceEqual(prior), "OneDrive recovery sidecar predecessor changed");
            _repository.OneDriveRecoveryObserved?.Invoke(OneDriveRecoveryStage.BeforeInstall); token.ThrowIfCancellationRequested();
            attempted = true;
            try
            {
                if (prior is null) File.Move(Path.Combine(_repository.DirectoryPath, candidate), Path.Combine(_repository.DirectoryPath, OneDriveRecoverySidecar.Name));
                else AuthorityFiles.Replace(_repository.DirectoryPath, candidate, OneDriveRecoverySidecar.Name);
                _repository.OneDriveRecoveryObserved?.Invoke(OneDriveRecoveryStage.AfterInstall);
            }
            catch (Exception) { /* Exact installed bytes and authenticated authority decide success below. */ }
            _repository.OneDriveRecoveryObserved?.Invoke(OneDriveRecoveryStage.BeforeConfirmation);
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, OneDriveRecoverySidecar.Name)).AsSpan().SequenceEqual(bytes), "OneDrive recovery installed readback");
            using var recovered = Repository.OpenWithOneDriveRecovery(_repository.DirectoryPath, secret);
            Format.Require(recovered.ReadAuthority().Identity == expectedAuthority, "OneDrive recovery installed authority");
        }
        catch
        {
            _aborted = true;
            if (attempted) throw new OneDriveRecoveryUncertainException();
            throw;
        }
        finally
        {
            if (candidate is not null)
                try { File.Delete(Path.Combine(_repository.DirectoryPath, candidate)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }
    internal void RemoveOneDriveRecovery(Guid generation, CancellationToken token)
    {
        Check(); RequireNoCandidates(); RequireIdle(); token.ThrowIfCancellationRequested();
        _ = _repository.ReadAuthority();
        if (!OneDriveRecoverySidecar.Exists(_repository.DirectoryPath)) return;
        var sidecar = OneDriveRecoverySidecar.Read(_repository.DirectoryPath, _repository.Identity.RepositoryId);
        Format.Require(sidecar.GenerationId == generation, "OneDrive recovery removal generation changed");
        try { File.Delete(Path.Combine(_repository.DirectoryPath, OneDriveRecoverySidecar.Name)); }
        catch { if (OneDriveRecoverySidecar.Exists(_repository.DirectoryPath)) throw; }
        Format.Require(!OneDriveRecoverySidecar.Exists(_repository.DirectoryPath), "OneDrive recovery removal readback");
        _repository.OneDriveRecoveryObserved?.Invoke(OneDriveRecoveryStage.AfterRemoval);
    }
    internal void RetireOneDriveRecoveryForConversion(EncryptionMigrationControl control, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateConversionControl(control);
        Format.Require(control.SourceEncrypted && !control.TargetEncrypted && control.RepositoryId == _repository.Identity.RepositoryId,
            "conversion OneDrive recovery retirement identity");
        var sidecar = OneDriveRecoverySidecar.Read(_repository.DirectoryPath, _repository.Identity.RepositoryId);
        RemoveOneDriveRecovery(sidecar.GenerationId, token);
    }
}
