using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FluffHistory.Storage;

// Per-unlocked-repository key ownership. No process-wide secret registry.
internal sealed class RepositoryCryptography(byte[] key, byte[] binding) : IDisposable
{
    internal const int Overhead = 64;
    private bool _disposed;
    internal byte[] WrapMaster(RepositoryCryptography wrapper, string purpose, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return wrapper.Protect(key, purpose, context);
    }
    internal byte[] ProtectRecoveryPacket(RepositoryCryptography wrapper, string purpose, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Format.Require(context.Length == 32, "recovery packet context");
        var packet = new byte[64]; context.CopyTo(packet); key.CopyTo(packet, 32);
        try { return wrapper.Protect(packet, purpose); }
        finally { CryptographicOperations.ZeroMemory(packet); }
    }
    internal string ProtectCurrentWindowsUser()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToBase64String(WindowsKeyProtector.Protect(key, binding));
    }
    internal byte[] Derive(string purpose, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var label = Encoding.UTF8.GetBytes("FluffHistory/v4/m20/" + purpose);
        var info = new byte[label.Length + binding.Length + context.Length];
        label.CopyTo(info, 0); binding.CopyTo(info, label.Length); context.CopyTo(info.AsSpan(label.Length + binding.Length));
        var result = new byte[32]; HKDF.DeriveKey(HashAlgorithmName.SHA256, key, result, salt, info); return result;
    }
    internal byte[] Protect(ReadOnlySpan<byte> plain, string purpose, ReadOnlySpan<byte> context = default)
    {
        var result = new byte[checked(plain.Length + Overhead)]; "FHENC4\0\0"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), plain.Length);
        RandomNumberGenerator.Fill(result.AsSpan(16, 32)); var derived = Derive(purpose, result.AsSpan(16, 32), context);
        try
        {
            using var aes = new AesGcm(derived, 16);
            aes.Encrypt(new byte[12], plain, result.AsSpan(64), result.AsSpan(48, 16), result.AsSpan(0, 48)); return result;
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
    internal byte[] Unprotect(ReadOnlySpan<byte> bytes, string purpose, ReadOnlySpan<byte> context = default)
    {
        Format.Require(bytes.Length >= Overhead && bytes[..8].SequenceEqual("FHENC4\0\0"u8) &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) == bytes.Length - Overhead, "encrypted envelope schema/length");
        var derived = Derive(purpose, bytes.Slice(16, 32), context); var plain = new byte[bytes.Length - Overhead];
        try
        {
            using var aes = new AesGcm(derived, 16);
            aes.Decrypt(new byte[12], bytes[64..], bytes.Slice(48, 16), plain, bytes[..48]); return plain;
        }
        catch (CryptographicException error) { CryptographicOperations.ZeroMemory(plain); throw new InvalidDataException("Encrypted object authentication failed.", error); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
    internal string Bucket(ReadOnlySpan<byte> path)
    {
        var derived = Derive("reuse-bucket", binding, default);
        try { return Convert.ToHexString(HMACSHA256.HashData(derived, path)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(key); GC.SuppressFinalize(this); }
    ~RepositoryCryptography() => CryptographicOperations.ZeroMemory(key);
}
