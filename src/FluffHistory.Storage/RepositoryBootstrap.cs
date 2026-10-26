using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed class RepositoryLockedException() : InvalidOperationException("Repository is encrypted and locked. Supply a recovery password or Windows user protector.");

internal sealed record PasswordKeySlot(string Kdf, int Iterations, string Salt, string WrappedKey);
internal sealed record RepositoryBootstrap(string Product, int FormatVersion, int BootstrapSchema, string EncryptionMode,
    Guid RepositoryId, DateTimeOffset CreatedUtc, PasswordKeySlot? Recovery, string? WindowsUserKey)
{
    internal const string EncryptedMode = "aes256-gcm-hkdf-sha256-v1";
    internal const int Iterations = 600_000;
    internal RepositoryIdentity Identity => new(RepositoryId, CreatedUtc);
    internal bool Encrypted => EncryptionMode == EncryptedMode;
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal byte[] Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal byte[] Binding() => SHA256.HashData((this with { Recovery = null, WindowsUserKey = null }).Bytes());
    internal static RepositoryBootstrap Plain(RepositoryIdentity identity) => new("Fluff-History", 4, 1, "none", identity.RepositoryId, identity.CreatedUtc, null, null);
    internal static RepositoryBootstrap Read(string directory)
        => Parse(AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")));
    internal static RepositoryBootstrap Parse(byte[] bytes)
    {
        try
        {
            var b = JsonSerializer.Deserialize<RepositoryBootstrap>(bytes, Options) ?? throw Format.Invalid("null bootstrap");
            if (b.FormatVersion != 4) throw new NotSupportedException($"Expected repository format 4; found {b.FormatVersion}. No development-format migration is supplied.");
            Format.Require(b.Product == "Fluff-History" && b.BootstrapSchema == 1 && b.EncryptionMode is "none" or EncryptedMode,
                "unsupported/missing Format-4 bootstrap encryption schema");
            _ = b.Identity;
            using var document = JsonDocument.Parse(bytes);
            RequireUnique(document.RootElement);
            foreach (var field in new[] { "product", "formatVersion", "bootstrapSchema", "encryptionMode", "repositoryId", "createdUtc", "recovery" })
                Format.Require(document.RootElement.TryGetProperty(field, out _), "missing bootstrap field");
            var canonical = b.Bytes();
            if (!document.RootElement.TryGetProperty("windowsUserKey", out _))
                canonical = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(canonical).Replace(",\"windowsUserKey\":null}", "}", StringComparison.Ordinal));
            Format.Require(canonical.AsSpan().SequenceEqual(bytes), "noncanonical bootstrap");
            Format.Require(b.Encrypted ? b.Recovery is not null : b.Recovery is null && b.WindowsUserKey is null, "bootstrap mode/key slots");
            if (b.Recovery is { } slot)
            {
                Format.Require(slot.Kdf == "pbkdf2-sha256" && slot.Iterations == Iterations, "unsupported recovery KDF parameters");
                _ = Decode(slot.Salt, 32); _ = Decode(slot.WrappedKey, 96);
            }
            // A damaged optional Windows blob must not prevent portable password recovery.
            Format.Require(b.WindowsUserKey is null || b.WindowsUserKey.Length <= 6000, "Windows slot bound");
            return b;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Format-4 bootstrap.", error); }
    }
    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) { Format.Require(names.Add(property.Name), "duplicate bootstrap field"); RequireUnique(property.Value); }
    }
    private static byte[] Decode(string value, int expected)
    {
        try { var bytes = Convert.FromBase64String(value); Format.Require(bytes.Length == expected, "key slot size"); return bytes; }
        catch (Exception error) when (error is FormatException or ArgumentNullException) { throw new InvalidDataException("Invalid recovery slot.", error); }
    }
    private static byte[] PasswordKey(ReadOnlySpan<char> password, byte[] salt)
    {
        if (password.Length is < 1 or > 1024) throw new ArgumentException("Recovery password must contain 1..1024 characters.", nameof(password));
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
    }
    internal static (RepositoryBootstrap Bootstrap, RepositoryCryptography Crypto) Create(RepositoryIdentity identity, ReadOnlySpan<char> password, bool windows)
    {
        var b = Plain(identity) with { EncryptionMode = EncryptedMode }; var salt = RandomNumberGenerator.GetBytes(32);
        var master = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var wrapper = new RepositoryCryptography(PasswordKey(password, salt), b.Binding());
            var slot = wrapper.Protect(master, "password-slot", salt);
            b = b with { Recovery = new("pbkdf2-sha256", Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(slot)),
                WindowsUserKey = windows ? Convert.ToBase64String(WindowsKeyProtector.Protect(master, b.Binding())) : null };
            return (b, new(master, b.Binding()));
        }
        catch { CryptographicOperations.ZeroMemory(master); throw; }
    }
    internal RepositoryCryptography Unlock(ReadOnlySpan<char> password)
    {
        if (!Encrypted) throw new InvalidOperationException("Repository is unencrypted.");
        var slot = Recovery!; var salt = Decode(slot.Salt, 32);
        using var wrapper = new RepositoryCryptography(PasswordKey(password, salt), Binding());
        return new(wrapper.Unprotect(Decode(slot.WrappedKey, 96), "password-slot", salt), Binding());
    }
    internal RepositoryBootstrap WithRecoveryPassword(RepositoryCryptography crypto, ReadOnlySpan<char> password)
    {
        if (!Encrypted) throw new InvalidOperationException("Repository is unencrypted.");
        var salt = RandomNumberGenerator.GetBytes(32);
        using var wrapper = new RepositoryCryptography(PasswordKey(password, salt), Binding());
        return this with { Recovery = new("pbkdf2-sha256", Iterations, Convert.ToBase64String(salt),
            Convert.ToBase64String(crypto.WrapMaster(wrapper, "password-slot", salt))) };
    }
    internal RepositoryCryptography UnlockWindows()
    {
        if (!Encrypted || WindowsUserKey is null) throw new RepositoryLockedException();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(WindowsUserKey); }
        catch (FormatException error) { throw new InvalidDataException("Invalid Windows convenience slot. Use password recovery.", error); }
        var master = WindowsKeyProtector.Unprotect(bytes, Binding());
        if (master.Length != 32) { CryptographicOperations.ZeroMemory(master); throw Format.Invalid("Windows master-key size"); }
        return new(master, Binding());
    }
}
