using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class CodecTests
{
    [Fact]
    public void LaterUnicodeMappingsDoNotChangeFrozenKeys()
    {
        Assert.NotEqual(new HistoryPath(FoundationTests.Root, "\u019b"), new HistoryPath(FoundationTests.Root, "\ua7dc"));
        using var stream = typeof(HistoryPath).Assembly.GetManifestResourceStream("FluffHistory.Core.OrdinalCaseMap.txt")!;
        Assert.Equal("2AEE4E3EDD477A245CF1F1636F32240D7EF2AC1C8B07EC3C897AC20B199D7E9C", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
    }
    [Theory]
    [InlineData("foo", "FOO", true)]
    [InlineData("é", "É", true)]
    [InlineData("σ", "ς", true)]
    [InlineData("𐐨", "𐐀", true)]
    [InlineData("ſ", "S", false)]
    [InlineData("ı", "I", false)]
    [InlineData("K", "K", false)]
    [InlineData("ß", "ẞ", false)]
    public void FrozenCaseSemanticsAreOrdinalAndCultureIndependent(string a, string b, bool equal)
    {
        var saved = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal(equal, new HistoryPath(FoundationTests.Root, a) == new HistoryPath(FoundationTests.Root, b)); Assert.Equal(equal, StringComparer.OrdinalIgnoreCase.Equals(a, b)); }
        finally { CultureInfo.CurrentCulture = saved; }
    }
    [Fact]
    public void FrozenMappingsAgreeWithPlatformOrdinalComparison()
    {
        using var stream = typeof(HistoryPath).Assembly.GetManifestResourceStream("FluffHistory.Core.OrdinalCaseMap.txt")!;
        using var reader = new StreamReader(stream); int count = 0;
        while (reader.ReadLine() is { } line)
        {
            var pair = line.Split(';'); var a = new Rune(int.Parse(pair[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            var b = new Rune(int.Parse(pair[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            Assert.True(StringComparer.OrdinalIgnoreCase.Equals(a, b), line); Assert.Equal(HistoryPath.Fold(a), HistoryPath.Fold(b)); count++;
        }
        Assert.Equal(1448, count);
    }
    [Theory]
    [InlineData("../bad")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a/CON")]
    [InlineData("a. ")]
    [InlineData("/root")]
    public void UnsafeLogicalNamesAreRejected(string path) => Assert.Throws<ArgumentException>(() => new HistoryPath(FoundationTests.Root, path));
    [Fact]
    public void MalformedEncodedKeysAreRejected()
    {
        var key = PathKeys.Current(new(FoundationTests.Root, "foo")); Assert.Equal(new HistoryPath(FoundationTests.Root, "FOO"), PathKeys.DecodePath(key));
        key[^1] = 1; Assert.Throws<InvalidDataException>(() => PathKeys.DecodePath(key));
        var bad = PathKeys.Current(new(FoundationTests.Root, "X")); bad[16] = 0xd8; bad[17] = 0; Assert.Throws<DecoderFallbackException>(() => PathKeys.DecodePath(bad));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(50)]
    [InlineData(159)]
    public void SegmentCorruptionIsBoundedAndDetected(int offset)
    {
        var header = new SegmentHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0); var bytes = MetadataCodec.EncodeSegment(header); bytes[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => MetadataCodec.DecodeSegment(bytes));
    }
    [Fact]
    public void FrameRejectsCorruptionTruncationWrongBindingAndHugeLength()
    {
        var h = new SegmentHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0); var f = MetadataCodec.EncodeFrame(h, 160, 1, FrameKind.Semantic, [1, 2, 3]);
        var bytes = f.Bytes.ToArray(); bytes[^1] ^= 1; Assert.Throws<InvalidDataException>(() => MetadataCodec.ValidateFrame(bytes, h, f.Address));
        Assert.Throws<InvalidDataException>(() => MetadataCodec.ValidateFrame(f.Bytes[..^1], h, f.Address));
        Assert.Throws<InvalidDataException>(() => MetadataCodec.ValidateFrame(f.Bytes, h with { RepositoryId = Guid.NewGuid() }, f.Address));
        bytes = f.Bytes[..96]; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), int.MaxValue); Assert.Throws<InvalidDataException>(() => MetadataCodec.ReadFrameHeader(bytes, h, 160));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidPagesRejectCycleTypeCountOrderAndPadding(int problem)
    {
        var key = PathKeys.Current(new(FoundationTests.Root, "a")); var p = new FrameAddress(Guid.NewGuid(), 1, 160, 1, FrameKind.Semantic, 1, new string('0', 64));
        var entries = new[] { new PageEntry(key, p), new PageEntry(PathKeys.Current(new(FoundationTests.Root, "b")), p) };
        if (problem == 0) entries[0] = new(key, p with { Serial = 2 });
        if (problem == 1) entries[0] = new(key, p with { Kind = FrameKind.Root });
        if (problem == 3) entries[1] = entries[0];
        var bytes = MetadataCodec.EncodePage(new(IndexKind.Current, 0, entries));
        if (problem == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), ushort.MaxValue);
        if (problem == 4) bytes[^1] = 1;
        Assert.Throws<InvalidDataException>(() => MetadataCodec.DecodePage(bytes, 2));
    }
    [Fact]
    public void CorruptUnvisitedVersionDoesNotBlockUnrelatedCurrentButRebuildDetectsIt()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); FrameAddress old;
        using (var w = repo.OpenWriter()) { FoundationTests.Add(repo, w); using (var r = repo.OpenReader()) old = r.VersionIndex.Find(r.Captured.Root.Versions, PathKeys.Version(r.GetCurrent(new(FoundationTests.Root, "foo.txt"))!.Identity))!; FoundationTests.Add(repo, w); FoundationTests.Add(repo, w, "other"); }
        Damage(temp.Child(), old);
        using (var r = repo.OpenReader()) Assert.NotNull(r.GetCurrent(new(FoundationTests.Root, "other")));
        using (var r = repo.OpenReader()) Assert.Throws<InvalidDataException>(() => r.EnumerateVersions(new(FoundationTests.Root, "foo.txt")).ToArray());
        using (var w = repo.OpenWriter()) Assert.Throws<InvalidDataException>(() => w.RebuildIndexes());
    }
    [Fact]
    public void RebuildRepairsOnlyExplicitlyAndIgnoresUncommittedTail()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); FrameAddress page;
        using (var w = repo.OpenWriter()) FoundationTests.Add(repo, w);
        using (var r = repo.OpenReader()) page = r.Captured.Root.Current!;
        Damage(temp.Child(), page);
        using (var r = repo.OpenReader()) Assert.Throws<InvalidDataException>(() => r.GetCurrent(new(FoundationTests.Root, "foo.txt")));
        using (var stream = new FileStream(SegmentReader.SegmentPath(temp.Child(), page.SegmentId), FileMode.Append)) stream.Write("uncommitted garbage"u8);
        using (var w = repo.OpenWriter()) w.RebuildIndexes();
        using var fresh = repo.OpenReader(); Assert.Single(fresh.EnumerateCurrent()); Assert.Single(fresh.EnumerateVersions(new(FoundationTests.Root, "foo.txt")));
    }
    [Fact]
    public void RequiredRootAndStateCorruptionNeverFallback()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var r = repo.OpenReader()) Damage(temp.Child(), r.Captured.Address);
        Assert.Throws<InvalidDataException>(() => Repository.Open(temp.Child()));
        File.WriteAllText(Path.Combine(temp.Child(), "state.json"), "{}"); Assert.Throws<InvalidDataException>(() => Repository.Open(temp.Child()));
    }
    [Fact]
    public void UnsupportedAndNoncanonicalHeadersFailWithoutMutation()
    {
        using var temp = new TemporaryDirectory(); Repository.Create(temp.Child()); var path = Path.Combine(temp.Child(), "repository.json"); var bytes = File.ReadAllText(path);
        File.WriteAllText(path, bytes.Replace("\"formatVersion\":4", "\"formatVersion\":9", StringComparison.Ordinal)); Assert.Throws<NotSupportedException>(() => Repository.Open(temp.Child()));
        File.WriteAllText(path, bytes + " "); Assert.Throws<InvalidDataException>(() => Repository.Open(temp.Child()));
    }
    [Fact]
    public void PointerCannotEscapeFrontierOrSegmentIdentity()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var r = repo.OpenReader(); var p = r.Captured.Address;
        Assert.Throws<InvalidDataException>(() => r.Read(p with { Offset = p.End }));
        Assert.Throws<InvalidDataException>(() => r.Read(p with { SegmentId = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => r.Read(p with { Serial = p.Serial + 1 }));
        Assert.Throws<InvalidDataException>(() => r.Read(p with { BlockOffset = p.BlockOffset + 1 }));
    }
    internal static void Damage(string directory, FrameAddress address)
    {
        if (address.Kind == FrameKind.Page)
        {
            // Damage a logical replaceable page inside a valid physical container.
            // Compressed-byte damage instead destroys the whole block's integrity.
            var repo = Repository.Open(directory); var prior = repo.ReadAuthority();
            using var reader = repo.OpenReader(); var body = reader.Read(address); body[0] = 255;
            using var append = new SegmentAppender(directory, repo.Identity, prior.Root.Epoch, prior);
            var bad = append.Append(FrameKind.Page, body);
            var root = prior.Root with { Generation = prior.Root.Generation + 1, PreviousGeneration = prior.Root.Generation,
                PreviousTransactionId = prior.Root.TransactionId, PreviousRootHash = prior.Address.Sha256, TransactionId = Guid.NewGuid(), Current = bad };
            var pointer = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.Dispose();
            AuthorityFiles.WriteNew(Path.Combine(directory, "damage.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, pointer)));
            AuthorityFiles.Replace(directory, "damage.tmp"); return;
        }
        using var stream = new FileStream(SegmentReader.SegmentPath(directory, address.SegmentId), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = address.BlockOffset + MetadataBlock.HeaderBytes; var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1)); stream.Flush(true);
    }
}
