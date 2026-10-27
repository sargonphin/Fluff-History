using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FluffHistory.Tests;

// Phase-A prototype only: no production repository behavior is changed by this gate.
public sealed class EncryptionGateTests
{
    [Fact]
    public void ChunkCountersAndEncryptedDerivedCacheAreBounded()
    {
        var master = RandomNumberGenerator.GetBytes(32); var salt = RandomNumberGenerator.GetBytes(32);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, "FluffHistory/v4/m20/payload"u8.ToArray());
        using var aes = new AesGcm(key, 16);
        var plain = new byte[256 * 1024]; RandomNumberGenerator.Fill(plain);
        var cipher = new byte[plain.Length]; var restored = new byte[plain.Length]; var tag = new byte[16];
        var nonce = new byte[12];
        for (ulong counter = 0; counter < 5; counter++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
            aes.Encrypt(nonce, plain, cipher, tag, salt);
            aes.Decrypt(nonce, cipher, tag, restored, salt); Assert.Equal(plain, restored);
            nonce[^1] ^= 32;
            Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(nonce, cipher, tag, restored, salt));
        }
        // Decoded workspace blocks can retain bounded random access using independently sealed records.
        var cacheKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, RandomNumberGenerator.GetBytes(32), "FluffHistory/v4/m20/cache"u8.ToArray());
        var cache = Seal(cacheKey, plain, salt); Assert.Equal(plain, Open(cacheKey, cache, salt));
        cache[80] ^= 1; Assert.Throws<AuthenticationTagMismatchException>(() => Open(cacheKey, cache, salt));
        foreach (var secret in new[] { master, key, cacheKey, plain, restored }) CryptographicOperations.ZeroMemory(secret);
    }

    [Fact]
    public void PortableRecoveryAndCompressedBlockFailClosed()
    {
        var master = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);
        var clock = Stopwatch.StartNew();
        var wrapper = Rfc2898DeriveBytes.Pbkdf2("synthetic-gate-password", salt, 600_000, HashAlgorithmName.SHA256, 32);
        clock.Stop();
        var aad = "FluffHistory/v4/m20/password/synthetic-repository"u8.ToArray();
        var slot = Seal(wrapper, master, aad);
        Assert.Equal(master, Open(wrapper, slot, aad));
        var wrong = Rfc2898DeriveBytes.Pbkdf2("synthetic-wrong-password", salt, 600_000, HashAlgorithmName.SHA256, 32);
        Assert.Throws<AuthenticationTagMismatchException>(() => Open(wrong, slot, aad));
        slot[^1] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => Open(wrapper, slot, aad));
        var canary = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("private-canary/path/source/content", 6000)));
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Fastest, true)) deflate.Write(canary);
        var objectSalt = RandomNumberGenerator.GetBytes(32);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, objectSalt, "FluffHistory/v4/m20/metadata"u8.ToArray());
        var block = Seal(key, packed.ToArray(), aad);
        Assert.False(block.AsSpan().IndexOf("private-canary"u8) >= 0);
        using var input = new MemoryStream(Open(key, block, aad));
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream(); inflater.CopyTo(decoded); Assert.Equal(canary, decoded.ToArray());
        // Optional portable probe: only a clearly synthetic gate fixture is exported.
        var fixture = Environment.GetEnvironmentVariable("FLUFF_HISTORY_ENCRYPTION_GATE");
        if (fixture is not null)
        {
            slot[^1] ^= 1;
            File.WriteAllBytes(fixture, salt.Concat(slot).ToArray());
            File.WriteAllText(fixture + ".expected", Convert.ToHexString(SHA256.HashData(master)));
            File.WriteAllText(fixture + ".timing", $"PBKDF2-HMAC-SHA256 600000: {clock.Elapsed.TotalMilliseconds:F2} ms");
        }
        foreach (var bytes in new[] { master, wrapper, wrong, key }) CryptographicOperations.ZeroMemory(bytes);
    }

    private static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var result = new byte[plain.Length + 16];
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(new byte[12], plain, result.AsSpan(16), result.AsSpan(0, 16), aad); return result;
    }
    private static byte[] Open(byte[] key, byte[] encrypted, byte[] aad)
    {
        var plain = new byte[encrypted.Length - 16]; using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(new byte[12], encrypted.AsSpan(16), encrypted.AsSpan(0, 16), plain, aad); return plain;
    }
}
