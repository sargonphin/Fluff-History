using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class EncryptionTests
{
    internal const string Password = "synthetic-M20-unit-test-recovery";
    internal static StoredVersion Add(Repository repo, string name = "canary-private-path.txt", byte[]? bytes = null)
    {
        bytes ??= "canary-private-file-content"u8.ToArray();
        using var writer = repo.OpenWriter(); using var reader = repo.OpenReader(); using var draft = writer.CreatePayloadDraft();
        draft.Write(bytes); draft.CompleteCopy(bytes.Length);
        var p = draft.PrepareVersion(new(FoundationTests.Root, name, bytes.Length, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
            FoundationTests.Time, new(Convert.ToHexString(XxHash64.Hash(bytes))));
        using var prepared = writer.PrepareMutation(new([p], sourceRoots: [new(FoundationTests.Root, "canary-private-source-root")]), reader.Authority);
        draft.Install(); prepared.Commit(); using var fresh = repo.OpenReader(); return fresh.GetNewestVersion(new(FoundationTests.Root, name))!;
    }
    internal static void NoCanary(string directory)
    {
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == "writer.lock") { Assert.Equal(0, new FileInfo(path).Length); continue; }
            var bytes = ReadShared(path);
            foreach (var word in new[] { "canary-private", "CANARY-PRIVATE" })
            {
                Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(word)) < 0, path);
                Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(word)) < 0, path);
                Assert.True(bytes.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(word)) < 0, path);
            }
        }
    }
    private static byte[] ReadShared(string path)
    { using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var copy = new MemoryStream(); input.CopyTo(copy); return copy.ToArray(); }
    [Fact]
    public void ModesLockedDiscoveryIndependentRecoveryAndRawUnencryptedPayload()
    {
        using var temp = new TemporaryDirectory(); using var plain = Repository.Create(temp.Child("plain"));
        var raw = Add(plain); Assert.Equal("canary-private-file-content", File.ReadAllText(plain.GetPayloadPath(raw.Identity.VersionId)));
        using var repo = Repository.CreateEncrypted(temp.Child(), Password); var version = Add(repo);
        using var locked = Repository.Discover(repo.DirectoryPath)!; Assert.True(locked.IsEncrypted); Assert.True(locked.IsLocked);
        Assert.Equal(4, RepositoryIdentity.FormatVersion); Assert.Throws<RepositoryLockedException>(() => locked.OpenWriter());
        Assert.Throws<RepositoryLockedException>(() => locked.OpenReader()); Assert.Throws<RepositoryLockedException>(() => Repository.Open(repo.DirectoryPath));
        var before = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        Assert.Throws<RecoveryPasswordAuthenticationException>(() => Repository.OpenWithPassword(repo.DirectoryPath, "synthetic-wrong"));
        Assert.Equal(before, TemporaryDirectory.Snapshot(repo.DirectoryPath));
        using var recovered = Repository.OpenWithPassword(repo.DirectoryPath, Password); using var reader = recovered.OpenReader();
        using var payload = reader.OpenVersionPayload(version.Identity)!; using var restored = new MemoryStream(); payload.Content.CopyTo(restored);
        Assert.Equal("canary-private-file-content"u8.ToArray(), restored.ToArray()); NoCanary(repo.DirectoryPath);
        var result = new RestoreEngine(recovered).RestoreFile(version.Identity, temp.Child("restored"));
        Assert.Equal(1, result.Restored); Assert.Equal(restored.ToArray(), File.ReadAllBytes(temp.Child("restored")));
    }
    [Theory]
    [InlineData("missing-mode")] [InlineData("missing-schema")] [InlineData("mode")] [InlineData("kdf")]
    [InlineData("salt")] [InlineData("slot")] [InlineData("repository")] [InlineData("format3")]
    public void MaliciousBootstrapIsRejectedWithoutMutation(string damage)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password);
        var path = Path.Combine(repo.DirectoryPath, "repository.json"); var b = JsonNode.Parse(File.ReadAllText(path))!;
        switch (damage)
        {
            case "missing-mode": b.AsObject().Remove("encryptionMode"); break;
            case "missing-schema": b.AsObject().Remove("bootstrapSchema"); break;
            case "mode": b["encryptionMode"] = "none"; break;
            case "kdf": b["recovery"]!["iterations"] = int.MaxValue; break;
            case "salt": b["recovery"]!["salt"] = "bad"; break;
            case "slot": var slot = Convert.FromBase64String(b["recovery"]!["wrappedKey"]!.GetValue<string>()); slot[^1] ^= 1; b["recovery"]!["wrappedKey"] = Convert.ToBase64String(slot); break;
            case "repository": b["repositoryId"] = Guid.NewGuid(); break;
            case "format3": b["formatVersion"] = 3; break;
        }
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var changed = System.Text.Json.JsonSerializer.Deserialize<RepositoryBootstrap>(b.ToJsonString(), options)!;
        File.WriteAllBytes(path, changed.Bytes()); var before = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        Assert.ThrowsAny<Exception>(() => Repository.OpenWithPassword(repo.DirectoryPath, Password));
        Assert.Equal(before, TemporaryDirectory.Snapshot(repo.DirectoryPath));
    }
    [Fact]
    public void OptionalWindowsSlotCanBeMissingOrCorruptWithoutBreakingPasswordRecovery()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password, OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows()) { using var unlocked = Repository.OpenWithWindowsUser(repo.DirectoryPath); Assert.False(unlocked.IsLocked); }
        var path = Path.Combine(repo.DirectoryPath, "repository.json"); var b = RepositoryBootstrap.Read(repo.DirectoryPath);
        foreach (var value in new string?[] { "not-a-valid-dpapi-blob", null })
        {
            b = b with { WindowsUserKey = value }; File.WriteAllBytes(path, b.Bytes());
            using var recovered = Repository.OpenWithPassword(repo.DirectoryPath, Password); Assert.False(recovered.IsLocked);
            Assert.ThrowsAny<Exception>(() => Repository.OpenWithWindowsUser(repo.DirectoryPath));
        }
        File.WriteAllText(path, Encoding.UTF8.GetString(b.Bytes()).Replace(",\"windowsUserKey\":null}", "}", StringComparison.Ordinal));
        using var missing = Repository.OpenWithPassword(repo.DirectoryPath, Password); Assert.False(missing.IsLocked);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(262143)] [InlineData(262144)] [InlineData(262145)] [InlineData(900001)]
    public void StreamingPayloadBoundariesAndIdentity(long size)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password);
        var bytes = RandomNumberGenerator.GetBytes((int)size); var version = Add(repo, bytes: bytes);
        var physical = File.ReadAllBytes(repo.GetPayloadPath(version.Identity.VersionId));
        Assert.Equal(repo.EstimatePayloadPhysicalBytes(size), physical.Length);
        using var reader = repo.OpenReader(); using var payload = reader.OpenVersionPayload(version.Identity)!;
        using var copy = new MemoryStream(); payload.Content.CopyTo(copy); Assert.Equal(bytes, copy.ToArray());
        using var other = Repository.CreateEncrypted(temp.Child("other"), Password);
        Assert.Throws<InvalidDataException>(() => new EncryptedPayload.Reader(other, version.Identity.VersionId, new MemoryStream(physical), size));
        Assert.Throws<InvalidDataException>(() => new EncryptedPayload.Reader(repo, Guid.NewGuid(), new MemoryStream(physical), size));
    }
    [Theory]
    [InlineData("header")] [InlineData("salt")] [InlineData("tag")] [InlineData("first")] [InlineData("middle")] [InlineData("last")]
    [InlineData("truncated")] [InlineData("trailing")] [InlineData("order")] [InlineData("duplicate")] [InlineData("missing")]
    public void DamagedPayloadNeverRestores(string damage)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password);
        var version = Add(repo, bytes: RandomNumberGenerator.GetBytes(3 * EncryptedPayload.ChunkBytes + 7));
        var path = repo.GetPayloadPath(version.Identity.VersionId); var b = File.ReadAllBytes(path);
        var step = EncryptedPayload.ChunkBytes + 32;
        switch (damage)
        {
            case "header": b[8] ^= 1; break; case "salt": b[48] ^= 1; break; case "tag": b[80] ^= 1; break;
            case "first": b[120] ^= 1; break; case "middle": b[96 + step + 50] ^= 1; break; case "last": b[^1] ^= 1; break;
            case "truncated": b = b[..^1]; break; case "trailing": b = [..b, 0]; break;
            case "order": var chunk = b.AsSpan(96, step).ToArray(); b.AsSpan(96 + step, step).CopyTo(b.AsSpan(96)); chunk.CopyTo(b, 96 + step); break;
            case "duplicate": b.AsSpan(96, step).CopyTo(b.AsSpan(96 + step)); break;
            case "missing": b = [..b.AsSpan(0, 96), ..b.AsSpan(96 + step)]; break;
        }
        File.WriteAllBytes(path, b); var target = temp.Child("output");
        var result = new RestoreEngine(repo).RestoreFile(version.Identity, target); Assert.Equal(0, result.Restored); Assert.False(File.Exists(target));
    }
    [Fact]
    public void CompressedEncryptedBlocksAuthenticateFullIdentityAndRandomCrossBlockReads()
    {
        var identity = new RepositoryIdentity(Guid.NewGuid(), FoundationTests.Time);
        var (bootstrap, crypto) = RepositoryBootstrap.Create(identity, Password, false); using var secret = crypto;
        var segment = new SegmentHeader(identity.RepositoryId, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
        using var stream = new MemoryStream(); stream.Write(MetadataCodec.EncodeSegment(segment));
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("canary-private-metadata", 17000)));
        var first = MetadataBlock.Write(stream, segment, 160, 0, data.AsSpan(0, 262144), crypto: crypto);
        var second = MetadataBlock.Write(stream, segment, first.LogicalEnd, first.Physical, data.AsSpan(262144), crypto: crypto);
        Assert.Equal(3, first.Encoding); Assert.True(stream.Length < data.Length / 10);
        byte[] Read(long offset, int count) => stream.ToArray().AsSpan((int)offset, count).ToArray();
        Assert.Equal(data.AsSpan(262144).ToArray(), MetadataBlock.Decode(Read, segment, second, stream.Length, crypto));
        using var blocks = new MetadataBlockReader(stream, segment, new(), crypto: crypto);
        var address = new FrameAddress(segment.SegmentId, 1, first.LogicalEnd - 50, 104, FrameKind.Semantic, 1, new string('0', 64), first.Physical);
        var actual = new byte[200]; blocks.Read(address, actual); Assert.Equal(data.AsSpan(262144 - 50, 200).ToArray(), actual);
        Assert.Throws<InvalidDataException>(() => MetadataBlock.Decode(Read, segment with { Epoch = Guid.NewGuid() }, first, stream.Length, crypto));
        var encoded = stream.ToArray(); encoded[(int)first.Physical + 170] ^= 1;
        // Rehash the public corruption checksum: AEAD must still reject.
        SHA256.HashData(encoded.AsSpan((int)first.Physical, MetadataBlock.HeaderBytes + first.EncodedLength)).CopyTo(encoded, (int)first.End - 32);
        Assert.Throws<InvalidDataException>(() => MetadataBlock.Decode((o, n) => encoded.AsSpan((int)o, n).ToArray(), segment, first, stream.Length, crypto));
        Assert.True(bootstrap.Encrypted);
    }

    [Fact]
    public void WorkspaceMaintenanceAndCapacityScratchNeverPersistPlaintextAndTamperFails()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password);
        var old = Add(repo); var current = Add(repo, bytes: "canary-private-replacement"u8.ToArray());
        var work = temp.Child("workspace"); Directory.CreateDirectory(work);
        using (var reader = repo.OpenComparisonReader(work))
        {
            Assert.Equal(2, reader.EnumerateVersions(old.Identity.Path).Count()); NoCanary(work);
            Assert.Empty(Directory.GetFiles(work, "*.decoded", SearchOption.AllDirectories));
            // A fresh segment handle shares the validated RAM block, with no second disk representation.
            var hits = reader.Workspace!.DecodedCacheHits;
            using var segments = new SegmentReader(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, reader.Captured.Address, reader.Workspace, crypto: repo.Crypto);
            _ = segments.Read(reader.Captured.Address);
            Assert.True(reader.Workspace.DecodedCacheHits > hits);
        }
        using (var scratch = new HistoryScratch(work, new(), default, repo.Crypto))
        {
            var key = PathKeys.Version(old.Identity); var run = scratch.Write([new(key, old.Identity.VersionId, SemanticKind.VersionAdded, repo.ReadAuthority().Address with { Kind = FrameKind.Semantic })]);
            NoCanary(work);
            using var read = new HistoryScratch.Reader(run, SemanticHistoryFold.KeyOrder, default); Assert.True(read.MoveNext());
        }
        using (var scratch = new CapacityScratch(repo, new(WorkspaceRoot: work), new(), default))
        {
            var run = scratch.Write([new(old.Identity, old.StoredUtc, old.Metadata.Length, false)]); NoCanary(work);
            var b = File.ReadAllBytes(run.Path); b[^1] ^= 1; SHA256.HashData(b.AsSpan(36)).CopyTo(b, 4); File.WriteAllBytes(run.Path, b);
            using var read = scratch.Read(run, CapacityOrder.OldestOrder); Assert.Throws<InvalidDataException>(() => read.MoveNext());
        }
        using (var writer = repo.OpenWriter(work))
        {
            writer.Observe = stage =>
            {
                if (stage != PublicationStage.LocalPrepared) return;
                NoCanary(work); Assert.Empty(Directory.GetFiles(work, "*.decoded", SearchOption.AllDirectories));
                Assert.InRange(writer.PendingPublication!.Workspace.DecodedCachePeakBytes, 0, 32L * 1024 * 1024);
            };
            writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata();
            var authority = repo.ReadAuthority().Identity;
            Assert.Throws<InvalidOperationException>(() => writer.RemoveVersions([current.Identity], authority));
        }
        using (var writer = repo.OpenWriter(work)) { var result = writer.RemoveVersions([old.Identity], repo.ReadAuthority().Identity); Assert.Equal(1, result.Removed); }
        Assert.False(File.Exists(repo.GetPayloadPath(old.Identity.VersionId))); NoCanary(repo.DirectoryPath);
        using var recovered = Repository.OpenWithPassword(repo.DirectoryPath, Password); using var fresh = recovered.OpenReader(); Assert.Equal(current.Identity, fresh.GetCurrent(current.Identity.Path)!.Identity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EncryptedUnpublishedTornSuffixAndSparsePublicationKeepCommittedPrefix(bool afterReplace)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password); var first = Add(repo);
        var before = repo.ReadAuthority(); var segment = SegmentReader.SegmentPath(repo.DirectoryPath, before.Address.SegmentId);
        var prefix = File.ReadAllBytes(segment); Guid transaction;
        using (var writer = repo.OpenWriter())
        using (var prepared = writer.PrepareMutation(new(sourceRoots: [new(Guid.NewGuid(), "canary-private-second-root")]), before.Identity))
        {
            transaction = prepared.IntendedAuthority.TransactionId;
            writer.Observe = stage =>
            {
                if (stage == (afterReplace ? PublicationStage.AfterReplace : PublicationStage.BeforeReplace))
                { if (afterReplace) writer.LoseHandleForTest(); else throw new IOException("synthetic injected boundary"); }
            };
            Assert.ThrowsAny<IOException>(() => prepared.Commit());
        }
        Assert.Equal(prefix, File.ReadAllBytes(segment).AsSpan(0, prefix.Length).ToArray());
        using (var recovered = Repository.OpenWithPassword(repo.DirectoryPath, Password))
        using (var writer = recovered.OpenWriter()) Assert.Equal(afterReplace ? PublicationOutcome.Published : PublicationOutcome.Unpublished, writer.Reconcile(transaction));
        if (!afterReplace) using (var tail = new FileStream(segment, FileMode.Append, FileAccess.Write)) tail.Write("torn encrypted suffix"u8);
        Add(repo, "canary-private-after-retry");
        Assert.Equal(prefix, File.ReadAllBytes(segment).AsSpan(0, prefix.Length).ToArray());
        using var fresh = repo.OpenReader(); Assert.NotNull(fresh.GetVersion(first.Identity)); NoCanary(repo.DirectoryPath);
    }

    [Fact]
    public void FreshEnvelopeSaltEveryWriteAndPurposeBinding()
    {
        using var crypto = new RepositoryCryptography(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var salts = new HashSet<string>();
        for (var i = 0; i < 512; i++)
        {
            var bytes = crypto.Protect("synthetic plaintext"u8, "control", "object"u8);
            Assert.True(salts.Add(Convert.ToHexString(bytes.AsSpan(16, 32))));
            Assert.Throws<InvalidDataException>(() => crypto.Unprotect(bytes, "other", "object"u8));
            Assert.Throws<InvalidDataException>(() => crypto.Unprotect(bytes, "control", "wrong-object"u8));
            Assert.Equal("synthetic plaintext"u8.ToArray(), crypto.Unprotect(bytes, "control", "object"u8));
        }
    }

    [Fact]
    public void EmptyEncryptedPayloadIsChargedBeforeSourceRead()
    {
        using var f = new LiveCapacityFixture(true); f.File(content: "");
        var engine = new FileHistoryEngine(f.Repository, new(Capacity: new(RetentionPolicy.Forever)), f.Access, f.Probe, new AllocationProbe(1));
        f.Access.BeforeOpen = _ => Assert.True(f.Probe.Calls > 0);
        var result = f.Scan(engine); Assert.Equal(1, result.VersionsCreated); Assert.Equal(144, result.Capacity.MaximumPayloadAllocation);
        Assert.Equal(0, result.SourceBytesRead); Assert.Equal(1, result.ContentStreamsOpened);
    }

    [Theory]
    [InlineData("magic")] [InlineData("schema")] [InlineData("length")] [InlineData("salt")] [InlineData("tag")]
    [InlineData("ciphertext")] [InlineData("truncate")] [InlineData("trailing")]
    public void SmallControlsAuthenticateAndNeverGuessMode(string damage)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password);
        var path = Path.Combine(repo.DirectoryPath, "state.json"); var b = File.ReadAllBytes(path);
        switch (damage)
        {
            case "magic": b[0] ^= 1; break; case "schema": b[8] ^= 1; break; case "length": b[12] ^= 1; break;
            case "salt": b[16] ^= 1; break; case "tag": b[48] ^= 1; break; case "ciphertext": b[64] ^= 1; break;
            case "truncate": b = b[..^1]; break; case "trailing": b = [..b, 0]; break;
        }
        File.WriteAllBytes(path, b); var before = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        Assert.Throws<InvalidDataException>(() => Repository.OpenWithPassword(repo.DirectoryPath, Password)); Assert.Equal(before, TemporaryDirectory.Snapshot(repo.DirectoryPath));
    }

    [Fact]
    public void CompressionOnlyDevelopmentHeaderAndDowngradedBootstrapFailBeforeMutation()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.CreateEncrypted(temp.Child(), Password);
        var path = Path.Combine(repo.DirectoryPath, "repository.json"); var b = RepositoryBootstrap.Read(repo.DirectoryPath);
        File.WriteAllBytes(path, (b with { EncryptionMode = "none", Recovery = null, WindowsUserKey = null }).Bytes());
        var before = TemporaryDirectory.Snapshot(repo.DirectoryPath); Assert.ThrowsAny<Exception>(() => Repository.Open(repo.DirectoryPath)); Assert.Equal(before, TemporaryDirectory.Snapshot(repo.DirectoryPath));
        var json = JsonNode.Parse(File.ReadAllText(path))!; json.AsObject().Remove("bootstrapSchema"); json.AsObject().Remove("encryptionMode"); json.AsObject().Remove("recovery"); json.AsObject().Remove("windowsUserKey");
        File.WriteAllText(path, json.ToJsonString()); before = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        Assert.Throws<InvalidDataException>(() => Repository.Open(repo.DirectoryPath)); Assert.Equal(before, TemporaryDirectory.Snapshot(repo.DirectoryPath));
    }
}
