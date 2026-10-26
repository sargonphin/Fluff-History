using System.Buffers.Binary;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class Format4Tests
{
    private static readonly VersionIdentity Identity = new(new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "a"), 1, 7,
        Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"));
    [Fact]
    public void ExactRemovalBytesUseCanonicalKeyAndNetworkGuidWithoutExtraFields()
    {
        var expected = Convert.FromHexString("05002000" + "00112233445566778899AABBCCDDEEFF" + "00410000" +
            "0000000000000001" + "00000007" + "102132435465768798A9BACBDCEDFE0F");
        var actual = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, null, RemovedVersion: Identity));
        Assert.Equal(expected, actual); Assert.Equal(Identity, MetadataCodec.DecodeSemantic(expected).RemovedVersion);
        Assert.Equal(52, actual.Length);
    }
    [Fact]
    public void MinimumMaximumKeysAndPredecessorRoundTrip()
    {
        foreach (var name in new[] { "a", new string('a', HistoryPath.MaximumPathLength) })
        {
            var id = Identity with { Path = new(Identity.Path.SourceRootId, name), Sequence = long.MaxValue, Ordinal = 4095 };
            var previous = new FrameAddress(Guid.NewGuid(), 1, 160, 0, FrameKind.Semantic, 1, new string('0', 64));
            var bytes = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, previous, RemovedVersion: id));
            Assert.Equal(20 + Format.AddressBytes + PathKeys.Version(id).Length, bytes.Length);
            var decoded = MetadataCodec.DecodeSemantic(bytes); Assert.Equal(previous, decoded.Previous); Assert.Equal(id, decoded.RemovedVersion);
        }
    }
    [Theory]
    [InlineData("odd")][InlineData("short")][InlineData("oversize")][InlineData("root")][InlineData("case")]
    [InlineData("terminator")][InlineData("sequence")][InlineData("ordinal")][InlineData("uuid")]
    [InlineData("surrogate")][InlineData("negative-sequence")][InlineData("negative-ordinal")]
    [InlineData("trailing")][InlineData("truncated")][InlineData("kind")][InlineData("flag")]
    public void MalformedRemovalBytesFail(string damage)
    {
        var bytes = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, null, RemovedVersion: Identity));
        switch (damage)
        {
            case "odd": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 33); break;
            case "short": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 30); break;
            case "oversize": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), ushort.MaxValue); break;
            case "root": bytes.AsSpan(4, 16).Clear(); break;
            case "case": bytes[21] = (byte)'a'; break;
            case "terminator": bytes[23] = 1; break;
            case "sequence": bytes.AsSpan(24, 8).Clear(); break;
            case "negative-sequence": bytes.AsSpan(24, 8).Fill(255); break;
            case "negative-ordinal": bytes.AsSpan(32, 4).Fill(255); break;
            case "surrogate": bytes[20] = 0xd8; bytes[21] = 0; break;
            case "ordinal": BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), 4096); break;
            case "uuid": bytes.AsSpan(36, 16).Clear(); break;
            case "trailing": bytes = [..bytes, 0]; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "kind": bytes[0] = 6; break;
            case "flag": bytes[1] = 2; break;
        }
        Assert.Throws<InvalidDataException>(() => MetadataCodec.DecodeSemantic(bytes));
    }
    [Theory]
    [InlineData(2)] [InlineData(3)]
    public void HeaderStateAndSegmentAre4AndOldHeaderFailsWithoutRelabeling(int oldFormat)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var headerPath = Path.Combine(repo.DirectoryPath, "repository.json"); var header = File.ReadAllText(headerPath);
        using (var parsed = JsonDocument.Parse(header)) Assert.Equal(4, parsed.RootElement.GetProperty("formatVersion").GetInt32());
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.DirectoryPath, "state.json")))) Assert.Equal(4, state.RootElement.GetProperty("formatVersion").GetInt32());
        var segment = File.ReadAllBytes(Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg").Single());
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(segment.AsSpan(8)));
        var oldHeader = header.Replace("\"formatVersion\":4", $"\"formatVersion\":{oldFormat}", StringComparison.Ordinal); File.WriteAllText(headerPath, oldHeader);
        var snapshot = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        Assert.Throws<NotSupportedException>(() => Repository.Open(repo.DirectoryPath)); Assert.Equal(snapshot, TemporaryDirectory.Snapshot(repo.DirectoryPath));
        BinaryPrimitives.WriteInt32LittleEndian(segment.AsSpan(8), oldFormat); System.Security.Cryptography.SHA256.HashData(segment.AsSpan(0, 128)).CopyTo(segment, 128);
        Assert.Throws<NotSupportedException>(() => MetadataCodec.DecodeSegment(segment[..160]));
    }
    [Fact]
    public void PreparedCandidatesClaim4AndOldStateIsRejectedWithoutAdoption()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader())
        using (var prepared = writer.PrepareMutation(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority))
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.DirectoryPath, prepared.CandidateName))))
        { Assert.Equal(4, state.RootElement.GetProperty("formatVersion").GetInt32()); Assert.Equal(prepared.Intended, AuthorityFiles.ReadPrepared(repo.DirectoryPath, repo.Identity, prepared.CandidateName, prepared.ControlName)); }
        var path = Path.Combine(repo.DirectoryPath, "state.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"formatVersion\":4", "\"formatVersion\":3", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Repository.Open(repo.DirectoryPath));
    }
    [Fact]
    public void RemovalFramesRejectChangedHashAddressAndForwardPredecessor()
    {
        var header = new SegmentHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
        var body = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, null, RemovedVersion: Identity));
        var encoded = MetadataCodec.EncodeFrame(header, 160, 1, FrameKind.Semantic, body);
        Assert.Equal(body, MetadataCodec.ValidateFrame(encoded.Bytes, header, encoded.Address));
        Assert.Throws<InvalidDataException>(() => MetadataCodec.ValidateFrame(encoded.Bytes, header, encoded.Address with { Offset = 161 }));
        encoded.Bytes[^1] ^= 1; Assert.Throws<InvalidDataException>(() => MetadataCodec.ValidateFrame(encoded.Bytes, header, encoded.Address));
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var reader = repo.OpenReader();
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, reader.Captured);
        var forward = reader.Captured.Address with { Kind = FrameKind.Semantic, Serial = reader.Captured.Address.Serial + 100 };
        append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, forward, RemovedVersion: Identity)));
        Assert.Throws<InvalidDataException>(() => append.FlushAndValidate());
    }
    [Fact]
    public void UnpublishedRemovalTailCannotCrossCapturedFrontierOrAuthorizeDeletion()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); StoredVersion old;
        using (var writer = repo.OpenWriter()) { old = VersionRemovalTests.Add(repo, writer); _ = VersionRemovalTests.Add(repo, writer); }
        using var reader = repo.OpenReader();
        using (var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, reader.Captured))
        {
            var negative = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionRemoved, reader.Captured.Root.Journal, RemovedVersion: old.Identity)));
            append.FlushAndValidate(); Assert.Throws<InvalidDataException>(() => reader.Semantic(negative));
        }
        using var cleanup = repo.OpenWriter(); Assert.Equal(0, cleanup.CleanupRetiredPayloads().Result.Inspected);
        Assert.True(File.Exists(repo.GetPayloadPath(old.Identity.VersionId)));
    }
}
