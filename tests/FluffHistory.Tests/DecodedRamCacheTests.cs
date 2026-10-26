using System.Security.Cryptography;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class DecodedRamCacheTests
{
    private static SegmentHeader Segment() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
    private static BlockHeader Header(long physical) => new(physical, physical, MetadataBlock.MaximumDecodedBytes, 100, 0, false, 1, new byte[32]);

    [Fact]
    public void LruIsByteBoundedValidatesBindingAndZerosEvictedDisposedBuffers()
    {
        var segment = Segment(); using var cache = new DecodedBlockCache(2L * MetadataBlock.MaximumDecodedBytes);
        var a = RandomNumberGenerator.GetBytes(MetadataBlock.MaximumDecodedBytes);
        var b = RandomNumberGenerator.GetBytes(a.Length); var c = RandomNumberGenerator.GetBytes(a.Length);
        cache.Store(segment, Header(160), a); cache.Store(segment, Header(200), b);
        Assert.True(cache.TryGet(segment, Header(160), out var hit)); Assert.Same(a, hit);
        cache.Store(segment, Header(300), c); // B is least recently used.
        Assert.All(b, value => Assert.Equal(0, value)); Assert.False(cache.TryGet(segment, Header(200), out _));
        Assert.True(cache.TryGet(segment, Header(160), out _));
        foreach (var changed in new[] { Header(160) with { Logical = 7 }, Header(160) with { DecodedLength = 8 }, Header(160) with { EncodedLength = 9 },
                     Header(160) with { Previous = 10 }, Header(160) with { Sealed = true }, Header(160) with { Encoding = 3 }, Header(160) with { DecodedHash = RandomNumberGenerator.GetBytes(32) } })
            Assert.Throws<InvalidDataException>(() => cache.TryGet(segment, changed, out _));
        foreach (var changed in new[] { segment with { RepositoryId = Guid.NewGuid() }, segment with { SegmentId = Guid.NewGuid() },
                     segment with { Epoch = Guid.NewGuid() }, segment with { Ordinal = 2 }, segment with { PreviousId = Guid.NewGuid() } })
            Assert.False(cache.TryGet(changed, Header(160), out _));
        Assert.Equal(2L * a.Length, cache.CurrentBytes); Assert.Equal(cache.CurrentBytes, cache.PeakBytes); Assert.Equal(1, cache.Evictions);
        cache.Dispose(); Assert.Equal(0, cache.CurrentBytes);
        Assert.All(a, value => Assert.Equal(0, value)); Assert.All(c, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(segment, Header(160), out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EvictionReconstructsFromPhysicalCacheAndNeverCreatesDecodedFile(bool encrypted)
    {
        using var temp = new TemporaryDirectory(); using var repo = encrypted
            ? Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password) : Repository.Create(temp.Child());
        EncryptionTests.Add(repo);
        var authority = repo.ReadAuthority();
        using var workspace = new MetadataWorkspace(repo, temp.Child("workspace")) { MaximumDecodedBytes = MetadataBlock.MaximumDecodedBytes };
        using (var reader = new SegmentReader(repo.DirectoryPath, repo.Identity, authority.Root.Epoch, authority.Address, workspace, crypto: repo.Crypto))
            _ = reader.Read(authority.Address);
        var cache = workspace.DecodedBlocks; var misses = cache.Misses;
        // A different validated block can evict the authority block; reconstruction is safe and ordinary.
        var padding = new byte[MetadataBlock.MaximumDecodedBytes]; padding[0] = 42;
        cache.Store(Segment(), Header(500), padding);
        using (var reader = new SegmentReader(repo.DirectoryPath, repo.Identity, authority.Root.Epoch, authority.Address, workspace, crypto: repo.Crypto))
            _ = reader.Read(authority.Address);
        Assert.True(cache.Misses > misses); Assert.True(cache.Evictions > 0);
        Assert.InRange(cache.PeakBytes, 1, MetadataBlock.MaximumDecodedBytes);
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.decoded", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(workspace.DirectoryPath, "*.cache", SearchOption.AllDirectories));
        if (encrypted) { EncryptionTests.NoCanary(workspace.DirectoryPath); EncryptionTests.NoCanary(repo.DirectoryPath); }
        Assert.Equal(authority.Identity, repo.ReadAuthority().Identity);
        workspace.Dispose(); Assert.All(padding, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => workspace.OpenSegment(authority.Address.SegmentId));
    }

    [Fact]
    public void FreshWorkspaceRejectsTamperedUnderlyingCiphertextAfterCacheDisposal()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password);
        var authority = repo.ReadAuthority(); var original = File.ReadAllBytes(SegmentReader.SegmentPath(repo.DirectoryPath, authority.Address.SegmentId));
        using (var workspace = new MetadataWorkspace(repo, temp.Child("workspace")))
        using (var reader = new SegmentReader(repo.DirectoryPath, repo.Identity, authority.Root.Epoch, authority.Address, workspace, crypto: repo.Crypto))
        {
            _ = reader.Read(authority.Address);
            // Mutate encrypted data and repair its public checksum: only full authentication detects this.
            var changed = original.ToArray(); var block = checked((int)authority.Address.BlockOffset);
            changed[block + MetadataBlock.HeaderBytes + 64] ^= 1;
            var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(block + 60));
            SHA256.HashData(changed.AsSpan(block, MetadataBlock.HeaderBytes + count)).CopyTo(changed, block + MetadataBlock.HeaderBytes + count + 16);
            File.WriteAllBytes(SegmentReader.SegmentPath(repo.DirectoryPath, authority.Address.SegmentId), changed);
            _ = reader.Read(authority.Address); // Validated cached data may satisfy this exact captured read.
        }
        using var fresh = new MetadataWorkspace(repo, temp.Child("fresh"));
        using var segments = new SegmentReader(repo.DirectoryPath, repo.Identity, authority.Root.Epoch, authority.Address, fresh, crypto: repo.Crypto);
        Assert.Throws<InvalidDataException>(() => segments.Read(authority.Address));
    }

    [Fact]
    public void CancellationAppliesToRamHitsAndDisposalClearsCache()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        var authority = repo.ReadAuthority(); using var token = new CancellationTokenSource();
        using var workspace = new MetadataWorkspace(repo, temp.Child("workspace"), token.Token);
        using var reader = new SegmentReader(repo.DirectoryPath, repo.Identity, authority.Root.Epoch, authority.Address, workspace);
        _ = reader.Read(authority.Address); var cache = workspace.DecodedBlocks; Assert.True(cache.CurrentBytes > 0);
        token.Cancel(); Assert.Throws<OperationCanceledException>(() => reader.Read(authority.Address));
        workspace.Dispose(); Assert.Equal(0, cache.CurrentBytes);
    }
}
