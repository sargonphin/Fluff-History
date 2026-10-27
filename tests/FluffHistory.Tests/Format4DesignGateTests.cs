using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Trait("Category", "MetadataDiagnostic")]
public sealed class Format4DesignGateTests(ITestOutputHelper output)
{
    internal static string Root => Environment.GetEnvironmentVariable("FLUFF_HISTORY_FORMAT_DIAGNOSTIC_ROOT")
        ?? throw new InvalidOperationException("Set FLUFF_HISTORY_FORMAT_DIAGNOSTIC_ROOT to owned disposable storage.");

    [Format3CompressionDiagnosticFact]
    public void MeasureBaselineCandidates()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "v3-baseline");
        if (!File.Exists(Path.Combine(Root, "v3-baseline.complete")))
        {
            Assert.False(Directory.Exists(path), "Inspect incomplete fixture before rebuilding.");
            var repo = Repository.Create(path);
            SmbDiagnosticFixture.Populate(repo, 30_000, 250_000);
            var dirtyBytes = Inventory(path);
            using (var writer = repo.OpenWriter(Path.Combine(Root, "work")))
            { writer.CompactMetadata(SmbNetworkMeasurementTests.Limits); writer.CleanupRetiredMetadata(); }
            File.WriteAllText(Path.Combine(Root, "v3-baseline.complete"), dirtyBytes.ToString());
        }
        var repository = Repository.Open(path); var authority = repository.ReadAuthority();
        var results = new List<object>(); string? currentDigest = null, timelineDigest = null;
        foreach (var block in new[] { 0, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024, 64 * 1024 * 1024 })
        {
            using var candidate = new Blocks(path, block);
            var pack = Metric(() => candidate.Build());
            using var traversal = new MetadataReadCandidate(repository, authority, "compressed-prototype", Root, candidate.Read);
            (long Count, string Digest) current = default, timeline = default;
            candidate.Reset(); var currentMetric = Metric(() => current = traversal.Current()); var currentIo = candidate.Counters;
            candidate.Reset(); var timelineMetric = Metric(() => timeline = traversal.Timeline()); var timelineIo = candidate.Counters;
            currentDigest ??= current.Digest; timelineDigest ??= timeline.Digest;
            Assert.Equal(currentDigest, current.Digest); Assert.Equal(timelineDigest, timeline.Digest);
            Assert.Equal(30_000, current.Count); Assert.Equal(250_000, timeline.Count);
            using var reader = repository.OpenReader();
            var late = reader.VersionIndex.Range(authority.Root.Versions).Select(e => e.Value).MaxBy(a => a.Offset)!;
            candidate.Reset(); var randomMetric = Metric(() => candidate.Read(late)); var randomIo = candidate.Counters;
            var cross = reader.VersionIndex.Range(authority.Root.Versions).Select(e => e.Value)
                .FirstOrDefault(a => block > 0 && (a.Offset / block != (a.End - 1) / block));
            candidate.Reset(); var crossMetric = Metric(() => { if (cross is not null) candidate.Read(cross); });
            results.Add(new { BlockBytes = block, LogicalBytes = Inventory(path), candidate.PhysicalBytes, Pack = pack,
                Current = new { Metric = currentMetric, current.Count, current.Digest, Io = currentIo },
                Timeline = new { Metric = timelineMetric, timeline.Count, timeline.Digest, Io = timelineIo },
                Random = new { Metric = randomMetric, Io = randomIo },
                CrossBlock = new { Found = cross is not null, Metric = crossMetric, Io = candidate.Counters },
                Sparse = new { RewrittenCommittedBytes = 0, MaxNewDecodedBlock = block,
                    Model = "append immutable partial chunks at publication boundaries; physical locator in address, no directory rewrite" } });
            output.WriteLine($"block {block}: {candidate.PhysicalBytes} bytes; current {currentMetric}; timeline {timelineMetric}");
        }
        var result = new { Environment = "native Windows VM, uncontrolled OS cache; application fetch/decode counters, not SMB",
            DirtyV3Bytes = long.Parse(File.ReadAllText(Path.Combine(Root, "v3-baseline.complete"))), Results = results };
        File.WriteAllText(Path.Combine(Root, "phase-a.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static long Inventory(string path) => Directory.GetFiles(Path.Combine(path, "metadata"), "*.seg").Sum(p => new FileInfo(p).Length);

    [Format3CompressionDiagnosticFact]
    public void MeasureSparseAndLogicalReadAhead()
    {
        var source = Path.Combine(Root, "v3-baseline"); Assert.True(File.Exists(Path.Combine(Root, "v3-baseline.complete")));
        var repository = Repository.Open(source); var authority = repository.ReadAuthority(); var results = new List<object>();
        foreach (var size in new[] { 256 * 1024, 1024 * 1024, 4 * 1024 * 1024 })
        {
            using var blocks = new Blocks(source, size); blocks.Build(); blocks.ReadAhead = 1024 * 1024;
            using var traversal = new MetadataReadCandidate(repository, authority, "compressed-window", Root, blocks.Read);
            (long Count, string Digest) current = default, timeline = default;
            blocks.Reset(); var cm = Metric(() => current = traversal.Current()); var ci = blocks.Counters;
            blocks.Reset(); var tm = Metric(() => timeline = traversal.Timeline());
            results.Add(new { BlockBytes = size, LogicalWindow = blocks.ReadAhead, Current = new { Metric = cm, current.Count, current.Digest, Io = ci },
                Timeline = new { Metric = tm, timeline.Count, timeline.Digest, Io = blocks.Counters } });
        }
        using (var raw = new MetadataReadCandidate(repository, authority, "coalesced-1MiB", Root))
        {
            var cm = Metric(() => raw.Current()); var tm = Metric(() => raw.Timeline());
            results.Add(new { Mode = "v3-1MiB-logical-window", Current = cm, Timeline = tm, raw.ReadRequests, raw.RequestedBytes });
        }
        var copy = Path.Combine(Root, "v3-sparse"); Assert.False(Directory.Exists(copy)); SmbNetworkMeasurementTests.CopyTree(source, copy);
        var sparse = Repository.Open(copy);
        using (var writer = sparse.OpenWriter(Path.Combine(Root, "sparse-work")))
        {
            var version = FoundationTests.Prepare(writer, "one-new-file.txt");
            using var reader = sparse.OpenReader();
            using var prepared = writer.PrepareMutation(new([version]), reader.Authority);
            foreach (var size in new[] { 256 * 1024, 1024 * 1024, 4 * 1024 * 1024, 64 * 1024 * 1024 })
            {
                long physical = 0, decoded = 0, chunks = 0;
                var metric = Metric(() =>
                {
                    foreach (var transfer in prepared.Transfers)
                    {
                        using var input = File.OpenRead(transfer.TemporaryPath); var buffer = new byte[size];
                        while (input.Position < input.Length)
                        {
                            var count = (int)Math.Min(buffer.Length, input.Length - input.Position); input.ReadExactly(buffer.AsSpan(0, count));
                            using var outputBytes = new MemoryStream();
                            using (var deflate = new DeflateStream(outputBytes, CompressionLevel.Fastest, true)) deflate.Write(buffer, 0, count);
                            physical += Math.Min(count, outputBytes.Length) + 208; decoded += count; chunks++;
                        }
                    }
                });
                results.Add(new { Mode = "one-file-immutable-append", BlockBytes = size, AppendedLogical = decoded, AppendedPhysical = physical,
                    Chunks = chunks, RewrittenCommittedBytes = 0, Codec = metric });
            }
        }
        File.WriteAllText(Path.Combine(Root, "phase-a-append-window.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static object Metric(Action action)
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime; var allocation = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
        action(); return new { ElapsedMs = watch.Elapsed.TotalMilliseconds, CpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes() - allocation, ManagedLiveSample = GC.GetTotalMemory(false),
            WorkingSet = Environment.WorkingSet };
    }

    // Test-only seekable model. Physical positions are recorded while encoding; the
    // selected production address carries its locator instead of retaining this dictionary.
    private sealed class Blocks(string source, int blockBytes) : IDisposable
    {
        private sealed record Block(long Logical, long Physical, int Decoded, int Encoded, byte[] Hash);
        private readonly Dictionary<Guid, (SegmentHeader Header, FileStream Stream, Block[] Blocks)> _segments = [];
        private readonly Dictionary<(Guid, int), byte[]> _cache = [];
        private readonly Dictionary<Guid, (long Start, byte[] Bytes)> _encodedWindows = [];
        private readonly Queue<(Guid, int)> _order = [];
        private long _fetched, _decoded, _reads;
        internal int ReadAhead;
        internal long PhysicalBytes;
        internal object Counters => new { FetchedBytes = _fetched, DecodedBytes = _decoded, ReadRequests = _reads, RetainedDecodedBytes = _cache.Values.Sum(a => (long)a.Length) };
        internal void Reset() { _cache.Clear(); _order.Clear(); _encodedWindows.Clear(); _fetched = _decoded = _reads = 0; }
        internal void Build()
        {
            foreach (var path in Directory.GetFiles(Path.Combine(source, "metadata"), "*.seg"))
            {
                using var input = File.OpenRead(path); var h = new byte[160]; input.ReadExactly(h); var header = MetadataCodec.DecodeSegment(h);
                var target = Path.Combine(Root, $"candidate-{blockBytes}-{header.SegmentId:N}.bin");
                var stream = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1);
                var blocks = new List<Block>(); input.Position = 0;
                var buffer = new byte[blockBytes == 0 ? 1024 * 1024 : blockBytes];
                while (input.Position < input.Length)
                {
                    var logical = input.Position; var count = (int)Math.Min(buffer.Length, input.Length - logical); input.ReadExactly(buffer.AsSpan(0, count));
                    var physical = stream.Position;
                    if (blockBytes == 0) stream.Write(buffer, 0, count);
                    else
                    {
                        // 160-byte validated header + 48-byte trailer accounting.
                        stream.Write(new byte[160]);
                        using (var deflate = new DeflateStream(stream, CompressionLevel.Fastest, true)) deflate.Write(buffer, 0, count);
                        stream.Write(new byte[48]);
                    }
                    blocks.Add(new(logical, physical, count, checked((int)(stream.Position - physical)), SHA256.HashData(buffer.AsSpan(0, count))));
                }
                stream.Flush(); PhysicalBytes += stream.Length; _segments.Add(header.SegmentId, (header, stream, blocks.ToArray()));
            }
        }
        internal byte[] Read(FrameAddress address)
        {
            var (header, stream, blocks) = _segments[address.SegmentId]; var bytes = new byte[96 + address.Length];
            var offset = address.Offset; var written = 0;
            while (written < bytes.Length)
            {
                var index = checked((int)(offset / (blockBytes == 0 ? 1024 * 1024 : blockBytes))); var b = blocks[index];
                var take = (int)Math.Min(bytes.Length - written, b.Logical + b.Decoded - offset);
                if (blockBytes == 0)
                { stream.Position = offset; stream.ReadExactly(bytes.AsSpan(written, take)); _fetched += take; _reads++; }
                else
                {
                    var decoded = Decode(address.SegmentId, index);
                    decoded.AsSpan(checked((int)(offset - b.Logical)), take).CopyTo(bytes.AsSpan(written));
                }
                offset += take; written += take;
            }
            return MetadataCodec.ValidateFrame(bytes, header, address);
        }
        private byte[] Decode(Guid id, int index)
        {
            var (_, stream, blocks) = _segments[id]; var b = blocks[index]; var key = (id, index);
            if (_cache.TryGetValue(key, out var existing)) return existing;
            var decoded = new byte[b.Decoded]; byte[] encoded;
            if (ReadAhead > 0)
            {
                if (!_encodedWindows.TryGetValue(id, out var window) || b.Physical < window.Start || b.Physical + b.Encoded > window.Start + window.Bytes.Length)
                {
                    var last = index;
                    while (last + 1 < blocks.Length && blocks[last + 1].Logical < b.Logical + ReadAhead) last++;
                    var length = checked((int)(blocks[last].Physical + blocks[last].Encoded - b.Physical));
                    window = (b.Physical, new byte[length]); stream.Position = b.Physical; stream.ReadExactly(window.Bytes);
                    _encodedWindows[id] = window; _fetched += length; _reads++;
                }
                encoded = window.Bytes.AsSpan(checked((int)(b.Physical - window.Start)) + 160, b.Encoded - 208).ToArray();
            }
            else
            { stream.Position = b.Physical + 160; encoded = new byte[b.Encoded - 208]; stream.ReadExactly(encoded); _fetched += b.Encoded; _reads++; }
            using var input = new MemoryStream(encoded); using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            deflate.ReadExactly(decoded); Assert.Equal(-1, deflate.ReadByte()); Assert.Equal(b.Hash, SHA256.HashData(decoded));
            _decoded += decoded.Length;
            if (_cache.Count == 8) _cache.Remove(_order.Dequeue()); _cache.Add(key, decoded); _order.Enqueue(key); return decoded;
        }
        public void Dispose() { foreach (var value in _segments.Values) value.Stream.Dispose(); }
    }
}

public sealed class Format3CompressionDiagnosticFactAttribute : FactAttribute
{
    public Format3CompressionDiagnosticFactAttribute(int format = FluffHistory.Core.RepositoryIdentity.FormatVersion)
    {
        if (format != 3)
            Skip = "Historical v3 compression gate: reproduce at commit 5392e8d; use Format4ProductionMeasurementTests for current production.";
        else if (Environment.GetEnvironmentVariable("FLUFF_HISTORY_DIAGNOSTICS") != "1")
            Skip = "Opt-in historical compression diagnostic.";
    }
}
