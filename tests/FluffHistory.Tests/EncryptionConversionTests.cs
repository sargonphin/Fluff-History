using System.Security.Cryptography;
using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class EncryptionConversionTests
{
    internal static EncryptionConversionOptions Options(string workspace) => new(workspace,
        new(MaximumOutputBytes: 32 * 1024 * 1024, MaximumScratchBytes: 16 * 1024 * 1024))
        { AllocationUnit = 4096, CapacityProbe = _ => new(100_000_000_000, 90_000_000_000) };

    [Fact]
    public void RoundTripPreservesExactHistoryAndAllRepresentativePayloads()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child();
        Dictionary<Guid, byte[]> expected = [];
        string digest; Guid identity;
        using (var source = Repository.Create(path))
        {
            identity = source.Identity.RepositoryId;
            foreach (var size in new[] { 0, 1, 262143, 262144, 262145, 524288, 524289 })
            {
                var content = new byte[size]; RandomNumberGenerator.Fill(content);
                var version = EncryptionTests.Add(source, "Canary-\u00e9\u6587/" + size + ".bin", content); expected.Add(version.Identity.VersionId, content);
            }
            var first = EncryptionTests.Add(source, "versions.txt", "first"u8.ToArray()); expected.Add(first.Identity.VersionId, "first"u8.ToArray());
            var second = EncryptionTests.Add(source, "versions.txt", "second"u8.ToArray()); expected.Add(second.Identity.VersionId, "second"u8.ToArray());
            var deleted = EncryptionTests.Add(source, "deleted.txt", "old noncurrent history"u8.ToArray()); expected.Add(deleted.Identity.VersionId, "old noncurrent history"u8.ToArray());
            var collision = EncryptionTests.Add(source, "Collision", "historical exact file"u8.ToArray()); expected.Add(collision.Identity.VersionId, "historical exact file"u8.ToArray());
            using (var writer = source.OpenWriter())
            using (var captured = source.OpenReader())
                writer.Apply(new(currentRemovals: [deleted.Identity.Path, collision.Identity.Path]), captured.Authority);
            var child = EncryptionTests.Add(source, "Collision/child.txt", "inferred folder child"u8.ToArray()); expected.Add(child.Identity.VersionId, "inferred folder child"u8.ToArray());
            var root = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            using (var writer = source.OpenWriter())
            using (var captured = source.OpenReader())
            using (var draft = writer.CreatePayloadDraft())
            {
                var bytes = "second root"u8.ToArray(); draft.Write(bytes); draft.CompleteCopy(bytes.Length);
                var prepared = draft.PrepareVersion(new(root, "Other.txt", bytes.Length, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
                    FoundationTests.Time, new(Convert.ToHexString(XxHash64.Hash(bytes))));
                using var transaction = writer.PrepareMutation(new([prepared], sourceRoots: [new(root, "other-root")]), captured.Authority);
                draft.Install(); transaction.Commit(); expected.Add(prepared.VersionId, bytes);
            }
            var magic = EncryptionTests.Add(source, "RawMagic.txt", "FHPAY4\0\0raw source still has no encrypted overhead"u8.ToArray());
            expected.Add(magic.Identity.VersionId, "FHPAY4\0\0raw source still has no encrypted overhead"u8.ToArray());
            using var reader = source.OpenReader(); digest = Repository.SemanticDigest(reader, default);
        }
        var encrypted = Repository.ConvertEncryption(path, true, EncryptionTests.Password, Options(temp.Root));
        Assert.Equal(expected.Count, encrypted.Versions); Assert.Equal(digest, encrypted.SemanticDigest);
        using (var repository = Repository.OpenWithPassword(path, EncryptionTests.Password))
        {
            Verify(repository);
            using (var writer = repository.OpenWriter()) Assert.Equal(0, writer.CleanupRetiredMetadata().DeferredSegments);
            EncryptionTests.NoCanary(path);
        }
        Assert.Throws<RecoveryPasswordAuthenticationException>(() => Repository.OpenWithPassword(path, "wrong"));
        Assert.Null(Repository.InspectEncryptionMigration(path));
        var plain = Repository.ConvertEncryption(path, false, EncryptionTests.Password, Options(temp.Root));
        Assert.Equal(digest, plain.SemanticDigest); Assert.Equal(encrypted.TargetPhysicalBytes, plain.SourcePhysicalBytes);
        using (var repository = Repository.Open(path))
        {
            Verify(repository); Assert.Equal(identity, repository.Identity.RepositoryId);
            foreach (var entry in expected) Assert.Equal(entry.Value, File.ReadAllBytes(repository.GetPayloadPath(entry.Key)));
            Assert.Null(RepositoryBootstrap.Read(path).WindowsUserKey); Assert.Null(RepositoryBootstrap.Read(path).Recovery);
        }
        Assert.False(File.Exists(Path.Combine(path, EncryptionMigrationControl.FileName)));

        void Verify(Repository repository)
        {
            using var reader = repository.OpenReader(); Assert.Equal(digest, Repository.SemanticDigest(reader, default));
            foreach (var item in reader.VersionIndex.Range(reader.Captured.Root.Versions, null, null))
            {
                var version = reader.Version(item.Value, versionKey: item.Key);
                using var payload = reader.OpenVersionPayload(version.Identity)!; using var memory = new MemoryStream(); payload.Content.CopyTo(memory);
                Assert.Equal(expected[version.Identity.VersionId], memory.ToArray());
                var restored = Path.Combine(temp.Root, "restored", Guid.NewGuid().ToString("N"));
                var result = new RestoreEngine(repository).RestoreFile(version.Identity, restored); Assert.Equal(1, result.Restored);
                Assert.Equal(SHA256.HashData(expected[version.Identity.VersionId]), SHA256.HashData(File.ReadAllBytes(restored)));
                Assert.Equal(repository.EstimatePayloadPhysicalBytes(version.Metadata.Length), new FileInfo(repository.GetPayloadPath(version.Identity.VersionId)).Length);
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InsufficientCapacityOrCancelledStartNeverMutatesRepository(bool cancelled)
    {
        using var temp = new TemporaryDirectory(); using (var repository = Repository.Create(temp.Child())) EncryptionTests.Add(repository);
        var before = TemporaryDirectory.Snapshot(temp.Child()); using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var options = Options(temp.Root) with { CapacityProbe = _ => new(100_000_000, 0) };
        if (cancelled) Assert.Throws<OperationCanceledException>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, options, token: cancellation.Token));
        else Assert.Throws<EncryptionMigrationSpaceException>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, options));
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
    }

    [Theory]
    [InlineData("MarkerCandidateFlushed", true)] [InlineData("MarkerInstalled", true)] [InlineData("BeforePayload", true)]
    [InlineData("PayloadWritten", true)] [InlineData("PayloadFlushed", true)] [InlineData("BeforePayloadReplace", true)]
    [InlineData("PayloadsCompleted", true)] [InlineData("BeforeMetadata", true)] [InlineData("MetadataFlushed", true)]
    [InlineData("TargetStatePrepared", true)] [InlineData("BeforeBootstrapPivot", true)] [InlineData("BeforeStatePivot", true)]
    [InlineData("FinalVerified", true)] [InlineData("BeforeMarkerRemoval", true)]
    [InlineData("MarkerCandidateFlushed", false)] [InlineData("MarkerInstalled", false)] [InlineData("BeforePayload", false)]
    [InlineData("PayloadWritten", false)] [InlineData("PayloadFlushed", false)] [InlineData("BeforePayloadReplace", false)]
    [InlineData("PayloadsCompleted", false)] [InlineData("BeforeMetadata", false)] [InlineData("MetadataFlushed", false)]
    [InlineData("TargetStatePrepared", false)] [InlineData("BeforeBootstrapPivot", false)] [InlineData("BeforeStatePivot", false)]
    [InlineData("FinalVerified", false)] [InlineData("BeforeMarkerRemoval", false)]
    public void BoundaryFaultLeavesExactSourceOrResumableTarget(string stageText, bool encrypted)
    {
        using var temp = new TemporaryDirectory(); var stage = Enum.Parse<EncryptionConversionStage>(stageText);
        string digest;
        using (var repository = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows()))
        {
            EncryptionTests.Add(repository); EncryptionTests.Add(repository, "other.txt", "other"u8.ToArray());
            using var reader = repository.OpenReader(); digest = Repository.SemanticDigest(reader, default);
        }
        var options = Options(temp.Root) with { Observed = actual => { if (actual == stage) throw new IOException("Injected conversion boundary."); } };
        Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, options));
        if (Repository.InspectEncryptionMigration(temp.Child()) is null)
        { using var source = encrypted ? Repository.Open(temp.Child()) : Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password); using var reader = source.OpenReader(); Assert.Equal(digest, Repository.SemanticDigest(reader, default)); }
        else
        {
            Assert.Throws<EncryptionMigrationRequiredException>(() => Repository.Discover(temp.Child()));
            Assert.Throws<EncryptionMigrationRequiredException>(() => Repository.Open(temp.Child()));
        }
        var result = Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, Options(temp.Root));
        Assert.Equal(digest, result.SemanticDigest); Assert.Equal(2, result.Versions);
        using var target = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
        using var after = target.OpenReader(); Assert.Equal(digest, Repository.SemanticDigest(after, default));
    }
}
