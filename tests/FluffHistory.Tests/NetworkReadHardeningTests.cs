using System.Security.Cryptography;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class NetworkReadHardeningTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EveryFrameIsValidatedEvenWhenCorruptBytesWereAlreadyReadAhead(bool projection)
    {
        using var f = new Frames(true, 128, 128);
        var statistics = new ComparisonReadStatistics();
        using var reader = f.Reader(statistics, SegmentReader.NetworkReadWindowBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Format.FrameHeaderBytes + Format.MaximumFrameBytes];
        byte[] Read(int n) => projection ? reader.ReadComparison(f.Addresses[n], buffer, hash).ToArray() : reader.Read(f.Addresses[n]);
        Assert.Equal(f.Bodies[0], Read(0));
        var requests = statistics.DestinationReadRequests;
        Assert.Throws<InvalidDataException>(() => Read(1));
        Assert.Equal(requests, statistics.DestinationReadRequests); // The second frame was buffered, not trusted.
    }

    [Fact]
    public void ReadAheadStopsAtCapturedFrontierAndRejectsAnAvailableFutureFrame()
    {
        using var f = new Frames(16, 16);
        var statistics = new ComparisonReadStatistics();
        using var reader = f.Reader(statistics, SegmentReader.NetworkReadWindowBytes, f.Addresses[0]);
        Assert.Equal(f.Bodies[0], reader.Read(f.Addresses[0]));
        Assert.Equal(Format.FrameHeaderBytes + 16, statistics.Io.DecodedBytes);
        Assert.Throws<InvalidDataException>(() => reader.Read(f.Addresses[1]));
    }

    [Fact]
    public void TruncatedRefillNeverBecomesAUsableWindowAfterTheFileIsRestored()
    {
        using var f = new Frames(128, 128, 128);
        var original = File.ReadAllBytes(f.Path);
        using var reader = f.Reader(new(), 256);
        Assert.Equal(f.Bodies[0], reader.Read(f.Addresses[0]));
        using (var changed = new FileStream(f.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        { changed.SetLength(f.Addresses[1].BlockOffset + MetadataBlock.HeaderBytes + 1); changed.Flush(true); }
        Assert.Throws<InvalidDataException>(() => reader.Read(f.Addresses[1]));
        File.WriteAllBytes(f.Path, original);
        Assert.Equal(f.Bodies[1], reader.Read(f.Addresses[1]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MaximumFrameLargerThanWindowUsesValidatedDirectFallback(bool projection)
    {
        using var f = new Frames(Format.MaximumFrameBytes);
        var statistics = new ComparisonReadStatistics();
        using var reader = f.Reader(statistics, SegmentReader.NetworkReadWindowBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Format.FrameHeaderBytes + Format.MaximumFrameBytes];
        var value = projection ? reader.ReadComparison(f.Addresses[0], buffer, hash).ToArray() : reader.Read(f.Addresses[0]);
        Assert.Equal(f.Bodies[0], value); Assert.Equal(5, statistics.Io.BlocksDecoded);
        Assert.Equal(Format.FrameHeaderBytes + Format.MaximumFrameBytes, statistics.Io.DecodedBytes);
    }

    [Fact]
    public void FailedWriterIdentityCheckCannotResumeAfterTheHeaderIsRestored()
    {
        using var temp = new TemporaryDirectory(); var repository = Repository.Create(temp.Child());
        var other = Repository.Create(temp.Child("other")); var path = System.IO.Path.Combine(repository.DirectoryPath, "repository.json");
        var original = File.ReadAllBytes(path); var before = TemporaryDirectory.Snapshot(repository.DirectoryPath);
        using (var writer = repository.OpenWriter())
        {
            File.WriteAllBytes(path, AuthorityFiles.HeaderBytes(other.Identity));
            Assert.Throws<InvalidDataException>(() => writer.OpenOwnedReader());
            File.WriteAllBytes(path, original);
            Assert.Throws<InvalidOperationException>(() => writer.OpenOwnedReader());
            Assert.Throws<InvalidOperationException>(() => FoundationTests.Prepare(writer, "cannot-resume.txt"));
        }
        Assert.Equal(before, TemporaryDirectory.Snapshot(repository.DirectoryPath));
        using var fresh = repository.OpenWriter(); using var reader = fresh.OpenOwnedReader(); Assert.Empty(reader.EnumerateCurrent());
    }

    // Frame-layer fixtures deliberately do not claim valid semantic bodies. This exercises
    // raw/projection frame integrity, maximum extents and IO failure without codec changes.
    private sealed class Frames : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly RepositoryIdentity _repository = new(Guid.NewGuid(), FoundationTests.Time);
        private readonly Guid _epoch = Guid.NewGuid();
        internal string Path { get; }
        internal FrameAddress[] Addresses { get; }
        internal byte[][] Bodies { get; }
        internal Frames(params int[] lengths) : this(false, lengths) { }
        internal Frames(bool damageSecond, params int[] lengths)
        {
            var directory = System.IO.Path.Combine(_temp.Child(), "metadata"); Directory.CreateDirectory(directory);
            var header = new SegmentHeader(_repository.RepositoryId, Guid.NewGuid(), _epoch, 1, Guid.Empty, 0);
            Path = System.IO.Path.Combine(directory, header.SegmentId.ToString("N") + ".seg");
            Addresses = new FrameAddress[lengths.Length]; Bodies = new byte[lengths.Length][];
            using var output = File.Create(Path); output.Write(MetadataCodec.EncodeSegment(header));
            long logical = Format.SegmentHeaderBytes, previous = 0;
            for (var i = 0; i < lengths.Length; i++)
            {
                Bodies[i] = Enumerable.Repeat((byte)(i + 1), lengths[i]).ToArray();
                var frame = MetadataCodec.EncodeFrame(header, logical, i + 1, FrameKind.Semantic, Bodies[i], output.Position);
                Addresses[i] = frame.Address;
                if (damageSecond && i == 1) frame.Bytes[^1] ^= 1;
                foreach (var chunk in frame.Bytes.Chunk(MetadataBlock.MaximumDecodedBytes))
                { var block = MetadataBlock.Write(output, header, logical, previous, chunk); previous = block.Physical; logical += chunk.Length; }
            }
        }
        internal SegmentReader Reader(ComparisonReadStatistics statistics, int window, FrameAddress? frontier = null) =>
            new(_temp.Child(), _repository, _epoch, frontier ?? Addresses[^1], comparison: statistics, readAheadBytes: window);
        public void Dispose() => _temp.Dispose();
    }
}
