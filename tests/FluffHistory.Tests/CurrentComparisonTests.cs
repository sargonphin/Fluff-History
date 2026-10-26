using System.Buffers.Binary;
using System.Security.Cryptography;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class CurrentComparisonTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(128)]
    [InlineData(16_384)]
    public void DistributedVersionsMatchFullReaderInPathOrderAcrossBoundedWindows(int window)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 513, rotation: 32_768);
        Assert.True(Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg").Length > 8);
        using var full = repo.OpenReader(); using var reader = repo.OpenComparisonReader(temp.Child("local"));
        Assert.True(full.CurrentIndex.Range(full.Captured.Root.Current).Select(e => e.Value.SegmentId).Distinct().Count() > 8);
        var expected = full.EnumerateCurrent().ToArray();
        var actual = reader.EnumerateCurrentComparison(new ComparisonReadBudget(window)).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.Equal(expected[i].Identity, actual[i].Identity);
            Assert.Equal(expected[i].Metadata.Length, actual[i].Length);
            Assert.Equal(expected[i].Metadata.LastWriteTimeUtc, actual[i].LastWriteTimeUtc);
            if (i > 0) Assert.True(PathKeys.Comparer.Compare(PathKeys.Current(actual[i - 1].Path), PathKeys.Current(actual[i].Path)) < 0);
        }
        var stats = reader.LastComparisonStatistics!;
        Assert.Equal((513 + window - 1) / window, stats.Windows);
        Assert.Equal(Math.Min(window, 513), stats.MaximumBufferedEntries);
        Assert.InRange(stats.Io.PeakHandles, 1, 8);
        Assert.Equal(513, stats.Io.SemanticReads);
        if (window == 16_384) Assert.True(stats.Io.SegmentOpens < 513 / 4);
        Assert.Null(reader.Workspace!.CreatedDirectory); Assert.False(Directory.Exists(temp.Child("local")));
    }

    [Fact]
    public void GroupedReadsBoundDecodeAndDoNotMutateRepositoryOrRequireWorkspace()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 1_000);
        var before = TemporaryDirectory.Snapshot(repo.DirectoryPath);
        var times = Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        using (var reader = repo.OpenComparisonReader(temp.Child("unused")))
        {
            Assert.Equal(1_000, reader.EnumerateCurrentComparison().Count());
            var stats = reader.LastComparisonStatistics!;
            Assert.Equal(1, stats.Io.SegmentOpens);
            // A small compressed segment can be mostly useful data. The contract is
            // bounded decode and zero workspace copies, not a raw-file fetch ratio.
            Assert.InRange(stats.Io.PeakDecodedBytes, 1, MetadataBlock.MaximumDecodedBytes);
            Assert.Equal(stats.Io.PhysicalBytesRead, stats.DestinationBytesRequested);
            Assert.Equal(stats.Io.PhysicalReads, stats.DestinationReadRequests);
            Assert.True(stats.Io.BlocksDecoded < 1_000); // Blocks amortize frame decoding.
            Assert.Equal(0, reader.Workspace!.DestinationBytesFetched);
            Assert.Equal(1_000, reader.EnumerateCurrentComparison().Count()); // rereads authority-bound ranges, no persistent cache
        }
        Assert.False(Directory.Exists(temp.Child("unused")));
        Assert.Equal(before, TemporaryDirectory.Snapshot(repo.DirectoryPath));
        foreach (var time in times) Assert.Equal(time.Value, File.GetLastWriteTimeUtc(time.Key));
    }

    [Fact]
    public void ScopeRootsFrozenCaseAndReactivatedVersionMatchFullQueries()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        var root = FoundationTests.Root; var other = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        using var writer = repo.OpenWriter();
        using (var before = repo.OpenReader())
            writer.Apply(new([FoundationTests.Prepare(writer, "Foo"), FoundationTests.Prepare(writer, "Foo/Bar"),
                FoundationTests.Prepare(writer, "Foo2/a"), FoundationTests.Prepare(writer, "é/𐐨.txt"),
                FoundationTests.Prepare(writer, "Foo/Bar", other)], sourceRoots: [new(root, "Source"), new(other, "Other")]), before.Authority);
        VersionIdentity reactivated;
        using (var before = repo.OpenReader())
        {
            reactivated = before.GetCurrent(new(root, "FOO/BAR"))!.Identity;
            writer.Apply(new(currentRemovals: [reactivated.Path]), before.Authority);
        }
        using (var before = repo.OpenReader()) writer.Apply(new(currentUpserts: [reactivated]), before.Authority);
        using var full = repo.OpenReader(); using var reader = repo.OpenComparisonReader();
        var actual = reader.EnumerateCurrentComparison().ToArray();
        Assert.Equal(full.EnumerateCurrent().Select(v => v.Identity), actual.Select(v => v.Identity));
        Assert.Equal(reactivated, Assert.Single(reader.EnumerateCurrentComparison(root, "fOo")).Identity);
        Assert.Equal(other, Assert.Single(reader.EnumerateCurrentComparison(other, "foo")).Path.SourceRootId);
        Assert.Equal(new HistoryPath(root, "É/𐐀.TXT"), Assert.Single(reader.EnumerateCurrentComparison(root, "é")).Path);
        Assert.Throws<ArgumentException>(() => reader.EnumerateCurrentComparison(folder: "Foo").ToArray());
    }

    [Fact]
    public void LongKeysRespectByteBudgetBeforeEntryLimit()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter())
        using (var before = repo.OpenReader())
        {
            var names = Enumerable.Range(0, 8).Select(i => string.Join('/', Enumerable.Repeat(new string('a', 190), 9)) + $"/file{i}");
            writer.Apply(new(names.Select(n => FoundationTests.Prepare(writer, n)), sourceRoots: [new(FoundationTests.Root, "Source")]), before.Authority);
        }
        using var reader = repo.OpenComparisonReader(); var budget = new ComparisonReadBudget(128, Format.MaximumKeyBytes);
        Assert.Equal(8, reader.EnumerateCurrentComparison(budget).Count());
        Assert.Equal(1, reader.LastComparisonStatistics!.MaximumBufferedEntries);
        Assert.Equal(8, reader.LastComparisonStatistics.Windows);
        Assert.InRange(reader.LastComparisonStatistics.MaximumBufferedKeyBytes, 1, budget.MaximumKeyBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeAndBetweenResultsReleasesHandlesWithoutWorkspace(bool openingToken)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); MetadataDiagnosticFixture.Populate(repo, 17);
        using var cts = new CancellationTokenSource();
        using var reader = repo.OpenComparisonReader(temp.Child("local"), openingToken ? cts.Token : default);
        var token = openingToken ? default : cts.Token;
        using (var items = reader.EnumerateCurrentComparison(new ComparisonReadBudget(2), cancellationToken: token).GetEnumerator())
        { Assert.True(items.MoveNext()); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => items.MoveNext()); }
        Assert.Throws<OperationCanceledException>(() => reader.EnumerateCurrentComparison(cancellationToken: cts.Token).ToArray());
        Assert.False(Directory.Exists(temp.Child("local")));
        AssertAllSegmentsExclusive(repo);
    }

    [Fact]
    public void AbandoningEnumerationOrDisposingReaderClosesComparisonHandles()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); MetadataDiagnosticFixture.Populate(repo, 17);
        using var reader = repo.OpenComparisonReader();
        using (var items = reader.EnumerateCurrentComparison().GetEnumerator()) Assert.True(items.MoveNext());
        AssertAllSegmentsExclusive(repo);
        using var pending = reader.EnumerateCurrentComparison().GetEnumerator(); Assert.True(pending.MoveNext());
        reader.Dispose(); AssertAllSegmentsExclusive(repo);
        Assert.Throws<ObjectDisposedException>(() => pending.MoveNext());
    }

    [Fact]
    public void CapturedAuthorityStaysOldAndUnpinnedRetiredGenerationExpires()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using var writer = repo.OpenWriter(); FoundationTests.Add(repo, writer);
        using var old = repo.OpenComparisonReader();
        FoundationTests.Add(repo, writer, "other");
        Assert.Single(old.EnumerateCurrentComparison());
        using (var fresh = repo.OpenComparisonReader()) Assert.Equal(2, fresh.EnumerateCurrentComparison().Count());
        writer.CompactMetadata(); Assert.True(writer.CleanupRetiredMetadata().DeletedSegments > 0);
        Assert.Throws<ReaderGenerationExpiredException>(() => old.EnumerateCurrentComparison().ToArray());
    }

    [Fact]
    public void RetirementBetweenWindowsNeverSilentlyContinuesWithFreshAuthority()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 513, rotation: 65_536);
        using var reader = repo.OpenComparisonReader();
        using var items = reader.EnumerateCurrentComparison(new ComparisonReadBudget(2)).GetEnumerator();
        Assert.True(items.MoveNext());
        using (var writer = repo.OpenWriter())
        { writer.CompactMetadata(); Assert.True(writer.CleanupRetiredMetadata().DeletedSegments > 0); }
        Assert.Throws<ReaderGenerationExpiredException>(() => { while (items.MoveNext()) { } });
    }

    [Fact]
    public void MissingCurrentGenerationSegmentIsCorruption()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        using var reader = repo.OpenComparisonReader();
        File.Delete(SegmentReader.SegmentPath(repo.DirectoryPath, reader.Captured.Address.SegmentId));
        Assert.Throws<InvalidDataException>(() => reader.EnumerateCurrentComparison().ToArray());
    }

    [Fact]
    public void HashCorruptionIsRejectedAndReleasesResources()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) { FoundationTests.Add(repo, writer); FoundationTests.Add(repo, writer, "unrelated"); }
        FrameAddress address;
        using (var full = repo.OpenReader()) address = full.CurrentIndex.Find(full.Captured.Root.Current, PathKeys.Current(new(FoundationTests.Root, "foo.txt")))!;
        using (var stream = new FileStream(SegmentReader.SegmentPath(repo.DirectoryPath, address.SegmentId), FileMode.Open, FileAccess.ReadWrite))
        { stream.Position = address.BlockOffset + MetadataBlock.HeaderBytes; var b = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(b ^ 1)); }
        using var reader = repo.OpenComparisonReader(temp.Child("local"));
        Assert.Throws<InvalidDataException>(() => reader.EnumerateCurrentComparison().ToArray());
        AssertAllSegmentsExclusive(repo); Assert.False(Directory.Exists(temp.Child("local")));
    }

    [Fact]
    public void ValidHashedVersionOfWrongPathIsRejected()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) { FoundationTests.Add(repo, writer, "a"); FoundationTests.Add(repo, writer, "b"); }
        using (var full = repo.OpenReader())
        {
            var other = full.CurrentIndex.Find(full.Captured.Root.Current, PathKeys.Current(new(FoundationTests.Root, "b")))!;
            RewriteCurrent(repo, full, PathKeys.Current(new(FoundationTests.Root, "a")), other);
        }
        using var reader = repo.OpenComparisonReader();
        Assert.Throws<InvalidDataException>(() => reader.EnumerateCurrentComparison().ToArray());
        using var normal = repo.OpenReader(); Assert.Throws<InvalidDataException>(() => normal.EnumerateCurrent().ToArray());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("sequence")]
    [InlineData("ordinal")]
    [InlineData("stored")]
    [InlineData("creation")]
    [InlineData("mtime")]
    [InlineData("length")]
    [InlineData("attributes")]
    [InlineData("utf8")]
    [InlineData("trailing")]
    [InlineData("previous")]
    public void RehashedMalformedRestoreAndComparisonFieldsAreStillRejected(string field)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        using (var full = repo.OpenReader())
        {
            var version = full.EnumerateCurrent().Single();
            // Null predecessor gives stable offsets; a new validly hashed address bypasses checksum failure.
            var body = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, null, Version: version));
            switch (field)
            {
                case "id": Array.Clear(body, 2, 16); break;
                case "sequence": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(18), 0); break;
                case "ordinal": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(26), 4096); break;
                case "stored": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(30), 0); break;
                case "creation": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 28), 0); break;
                case "mtime": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 20), long.MaxValue); break;
                case "length": BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 36), -1); break;
                case "attributes": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 12), -1); break;
                case "utf8": body[58] = 255; break;
                case "trailing": body = [.. body, 42]; break;
                case "previous": body[1] = 2; break;
            }
            using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, full.Captured.Root.Epoch, full.Captured);
            var pointer = append.Append(FrameKind.Semantic, body);
            // Do not call the appender's semantic readback validator on deliberately malformed test data.
            // Dispose closes/flushed FileStream; publish through a second test-only appender below.
            append.Dispose();
            RewriteCurrent(repo, full, PathKeys.Current(version.Identity.Path), pointer, pointer.Serial);
        }
        using var reader = repo.OpenComparisonReader();
        Assert.Throws<InvalidDataException>(() => reader.EnumerateCurrentComparison().ToArray());
        using var normal = repo.OpenReader(); Assert.Throws<InvalidDataException>(() => normal.EnumerateCurrent().ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(56)]
    [InlineData(64)]
    public void ComparisonFrameValidatorRejectsEveryHeaderBinding(int offset)
    {
        var header = new SegmentHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, 0);
        var frame = MetadataCodec.EncodeFrame(header, 160, 1, FrameKind.Semantic, [1, 2, 3]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Assert.Equal(new byte[] { 1, 2, 3 }, ComparisonFrameValidator.Validate(frame.Bytes, header, frame.Address, hash).ToArray());
        frame.Bytes[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => ComparisonFrameValidator.Validate(frame.Bytes, header, frame.Address, hash).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedReadsStillValidateClosedSegmentHeadersAndSeals(bool seal)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 257, rotation: 65_536);
        using var reader = repo.OpenComparisonReader();
        var address = reader.CurrentIndex.Range(reader.Captured.Root.Current).First().Value;
        Assert.True(address.SegmentOrdinal < reader.Captured.Address.SegmentOrdinal);
        using (var stream = new FileStream(SegmentReader.SegmentPath(repo.DirectoryPath, address.SegmentId), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        { stream.Position = seal ? stream.Length - 1 : 128; var b = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(b ^ 1)); }
        Assert.Throws<InvalidDataException>(() => reader.EnumerateCurrentComparison().ToArray());
    }

    [Fact]
    public void PathAllocationChangesPreserveWindowsDeviceAndComponentValidation()
    {
        foreach (var stem in new[] { "file", "con", "ConIn$", "CONOUT$", "nul", "lpt1", "COM¹", "aux", "PRN", "com0", "COMı", "cön", "é", "𐐨" })
        foreach (var suffix in new[] { "", ".txt", " .txt", "..txt", " ", ".", "/ok", "/..", "/bad?", "//leaf", "\u0001" })
        {
            var path = "folder/" + stem + suffix;
            var oldInvalid = path.Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || LegacyDevice(segment)) || path.Any(char.IsControl);
            if (oldInvalid) Assert.Throws<ArgumentException>(() => new HistoryPath(FoundationTests.Root, path));
            else Assert.NotNull(new HistoryPath(FoundationTests.Root, path));
        }
        static bool LegacyDevice(string segment)
        {
            var name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
        }
    }

    private static void RewriteCurrent(Repository repo, RepositoryReader reader, byte[] key, FrameAddress pointer, long? baselineSerial = null)
    {
        // A deliberately inconsistent current tree with otherwise correct frames/root binding.
        // For malformed semantic tests, account for the just-appended uncommitted frame's serial.
        var baseline = baselineSerial is null ? reader.Captured : reader.Captured with { Address = pointer };
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, baseline);
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
        var index = new OrderedIndex(IndexKind.Current, Read, append);
        var current = index.Update(reader.Captured.Root.Current, [(key, pointer)]);
        var root = reader.Captured.Root with { Generation = reader.Captured.Root.Generation + 1, PreviousGeneration = reader.Captured.Root.Generation,
            PreviousTransactionId = reader.Captured.Root.TransactionId, PreviousRootHash = reader.Captured.Address.Sha256, TransactionId = Guid.NewGuid(), Current = current };
        var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
        AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, address)));
        AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
    }

    private static void AssertAllSegmentsExclusive(Repository repo)
    {
        foreach (var path in Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg"))
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
}
