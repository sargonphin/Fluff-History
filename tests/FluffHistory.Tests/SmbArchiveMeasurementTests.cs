using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static FluffHistory.Tests.SmbNetworkMeasurementTests;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbArchiveMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory, Trait("Category", "M19Diagnostic")]
    [InlineData("baseline")] [InlineData("heavy")]
    public void MeasureDenseTransportRepresentations(string tier)
    {
        var save = new SmbNetworkMeasurementTests(output);
        var dense = Path.Combine(Root, tier + "-dense");
        Assert.True(File.Exists(Path.Combine(Root, tier + "-local-compaction.complete")));
        var directory = Path.Combine(Root, tier + "-transport-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(Remote, Path.GetFileName(directory));
        Directory.CreateDirectory(directory); Directory.CreateDirectory(remote);
        var files = Directory.GetFiles(Path.Combine(dense, "metadata"), "*.seg").OrderBy(Ordinal).ToArray();
        var rawBytes = files.Sum(f => new FileInfo(f).Length);
        var expected = files.ToDictionary(f => Path.GetFileName(f), Hash, StringComparer.Ordinal);
        var measurements = new List<object>();
        var rawUpload = Measure(() => { foreach (var file in files) Copy(file, Path.Combine(remote, Path.GetFileName(file))); });
        var rawInputs = files.Select(file => Fresh(Path.Combine(remote, Path.GetFileName(file)))).ToArray();
        var rawCounters = SmbServerCounters.Capture();
        var rawDownload = Measure(() => { for (var i = 0; i < files.Length; i++) Copy(rawInputs[i], Path.Combine(directory, Path.GetFileName(files[i]))); });
        var rawProtocol = SmbServerCounters.Difference(rawCounters, SmbServerCounters.Capture());
        foreach (var file in files) Assert.Equal(expected[Path.GetFileName(file)], Hash(Path.Combine(directory, Path.GetFileName(file))));
        measurements.Add(new { Representation = "raw-segments", Count = files.Length, Bytes = rawBytes, Upload = rawUpload, Download = rawDownload,
            DownloadSmb = rawProtocol, Ideal100MbitSeconds = rawBytes / 12_500_000.0, Ideal1GbitSeconds = rawBytes / 125_000_000.0 });

        var rawPack = Path.Combine(directory, "segments.pack");
        var packMetric = Measure(() => Pack(files, rawPack));
        Transfer("raw-pack", [rawPack], packMetric, paths => Unpack(paths[0], Path.Combine(directory, "unpack-raw")), "unpack-raw");
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal })
        {
            var zip = Path.Combine(directory, level + ".zip");
            var metric = Measure(() => Zip(files, zip, level));
            Transfer("zip-" + level, [zip], metric, paths => Extract(paths, Path.Combine(directory, "unpack-" + level)), "unpack-" + level);
            var pick = Path.GetFileName(files[0]); // First complete sealed segment, not a tiny active tail.
            long skipped = 0; long fullBytes = 0;
            var single = Measure(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.GetEntry(pick)!; using var stream = entry.Open();
                using var sink = Stream.Null; stream.CopyTo(sink, 1024 * 1024); fullBytes = entry.Length;
            });
            var frame = Measure(() =>
            {
                using var archive = ZipFile.OpenRead(zip); var entry = archive.GetEntry(pick)!; using var stream = entry.Open();
                var buffer = new byte[128 * 1024]; var remaining = Math.Max(0, entry.Length - 16384);
                while (remaining > 0) { var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); Assert.True(count > 0); remaining -= count; skipped += count; }
                stream.ReadExactly(buffer.AsSpan(0, 16384));
            });
            var frameInput = Fresh(Path.Combine(remote, Path.GetFileName(zip)));
            var frameCounters = SmbServerCounters.Capture();
            var remoteFrame = Measure(() =>
            {
                using var archive = ZipFile.OpenRead(frameInput);
                using var stream = archive.GetEntry(pick)!.Open(); var buffer = new byte[128 * 1024];
                var remaining = fullBytes - 16384;
                while (remaining > 0) { var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); Assert.True(count > 0); remaining -= count; }
                stream.ReadExactly(buffer.AsSpan(0, 16384));
            });
            var frameProtocol = SmbServerCounters.Difference(frameCounters, SmbServerCounters.Capture());
            var rawFrameInput = Fresh(Path.Combine(remote, pick));
            var rawFrameCounters = SmbServerCounters.Capture();
            var rawFrame = Measure(() =>
            {
                using var file = new FileStream(rawFrameInput, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                file.Position = file.Length - 16384; var buffer = new byte[16384]; file.ReadExactly(buffer);
            });
            var rawFrameProtocol = SmbServerCounters.Difference(rawFrameCounters, SmbServerCounters.Capture());
            ushort method;
            using (var file = File.OpenRead(zip)) { file.Position = 8; using var reader = new BinaryReader(file); method = reader.ReadUInt16(); }
            measurements.Add(new { Representation = "zip-random-" + level, Segment = pick, SingleSegmentExtraction = single,
                DecompressedSegmentBytes = fullBytes, LateFrame = frame, RemoteLateFrame = remoteFrame, RawRemoteLateFrame = rawFrame, ZipMethod = method,
                DecompressedBeforeFrameBytes = skipped, RequestedFrameBytes = 16384, RemoteCache = "Fresh server-side file names/inodes when SSH configured; server/OS caches uncontrolled." });
            measurements.Add(new { Representation = "zip-random-protocol-" + level, FrameSmb = frameProtocol, RawFrameSmb = rawFrameProtocol });
        }
        foreach (var size in new[] { 4, 8 })
        {
            var shards = new List<string>();
            var pack = Measure(() =>
            {
                foreach (var (batch, ordinal) in files.Chunk(size).Select((b, i) => (b, i)))
                { var path = Path.Combine(directory, $"shard-{size}-{ordinal}.zip"); Zip(batch, path, CompressionLevel.Fastest); shards.Add(path); }
            });
            Transfer("zip-fastest-shard-" + size, shards.ToArray(), pack,
                paths => Extract(paths, Path.Combine(directory, "unpack-shard-" + size)), "unpack-shard-" + size);
            // Immutable replacement model: rebuild only the shard containing one changed segment.
            var rebuild = Measure(() => Zip(files.Take(size).ToArray(), Path.Combine(directory, $"rebuilt-{size}.zip"), CompressionLevel.Fastest));
            long decompressed = 0;
            var randomInput = Fresh(Path.Combine(remote, Path.GetFileName(shards[^1])));
            var random = Measure(() =>
            {
                using var zip = ZipFile.OpenRead(randomInput);
                var entry = zip.Entries[0]; decompressed = entry.Length; using var input = entry.Open(); input.CopyTo(Stream.Null, 1024 * 1024);
            });
            measurements.Add(new { Representation = "shard-rebuild-" + size, SegmentsRepacked = Math.Min(files.Length, size), Rebuild = rebuild,
                RandomSegment = random, DecompressedBytes = decompressed, UnrelatedEntriesDecompressed = 0 });
        }
        save.Save(tier + "-archives", new { RawBytes = rawBytes, RawSegmentCount = files.Length, Measurements = measurements,
            Note = "Pack inputs are already local. Per-operation remote raw input download must be added; no free server-side pack service exists. Fresh server-side file copies isolate download/frame client names/inodes when SSH is configured; server/OS caches remain uncontrolled. Download/frame SMB counters are actual server protocol deltas, not wire bytes. Operation peaks are sampled every 20ms; lifetime peaks include earlier work." });
        DeleteOwnedChild(Root, directory); DeleteOwnedChild(Remote, remote);

        void Transfer(string name, string[] paths, Metric pack, Action<string[]> extract, string extractName)
        {
            var upload = Measure(() => { foreach (var path in paths) Copy(path, Path.Combine(remote, Path.GetFileName(path))); });
            var downloads = paths.Select(path => Path.Combine(directory, "download-" + Path.GetFileName(path))).ToArray();
            var inputs = paths.Select(path => Fresh(Path.Combine(remote, Path.GetFileName(path)))).ToArray();
            var counters = SmbServerCounters.Capture();
            var download = Measure(() => { for (var i = 0; i < paths.Length; i++) Copy(inputs[i], downloads[i]); });
            var protocol = SmbServerCounters.Difference(counters, SmbServerCounters.Capture());
            var unpack = Measure(() => extract(downloads));
            var result = Path.Combine(directory, extractName);
            foreach (var item in expected) Assert.Equal(item.Value, Hash(Path.Combine(result, item.Key!)));
            var bytes = paths.Sum(p => new FileInfo(p).Length);
            measurements.Add(new { Representation = name, Count = paths.Length, Bytes = bytes, Ratio = (double)bytes / rawBytes,
                Pack = pack, Upload = upload, Download = download, Unpack = unpack,
                DownloadSmb = protocol,
                PackDownloadUnpackMs = pack.ElapsedMs + download.ElapsedMs + unpack.ElapsedMs,
                PackUploadDownloadUnpackMs = pack.ElapsedMs + upload.ElapsedMs + download.ElapsedMs + unpack.ElapsedMs,
                RawInputPlusPipelineMs = rawDownload.ElapsedMs + pack.ElapsedMs + upload.ElapsedMs + download.ElapsedMs + unpack.ElapsedMs,
                Ideal100MbitSeconds = bytes / 12_500_000.0, Ideal1GbitSeconds = bytes / 125_000_000.0 });
        }
        string Fresh(string path) => SmbDiagnosticCopies.CreateFreshFile(Path.GetFileName(remote), Path.GetFileName(path)) ?? path;
    }

    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static long Ordinal(string path)
    {
        using var stream = File.OpenRead(path); var bytes = new byte[Format.SegmentHeaderBytes]; stream.ReadExactly(bytes);
        return MetadataCodec.DecodeSegment(bytes).Ordinal;
    }
    private static void Copy(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024);
        input.CopyTo(output, 1024 * 1024); output.Flush(true);
    }
    private static void Zip(string[] paths, string target, CompressionLevel level)
    {
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true))
            foreach (var path in paths)
            {
                using var input = File.OpenRead(path);
                using var entry = archive.CreateEntry(Path.GetFileName(path), level).Open(); input.CopyTo(entry, 1024 * 1024);
            }
        file.Flush(true);
    }
    private static void Extract(string[] paths, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var path in paths)
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                Assert.Matches("^[0-9a-f]{32}\\.seg$", entry.FullName);
                Assert.InRange(entry.Length, 160, 67_108_864);
                using var input = entry.Open();
                using var result = new FileStream(Path.Combine(target, entry.FullName), FileMode.CreateNew, FileAccess.Write);
                input.CopyTo(result, 1024 * 1024);
            }
        }
    }
    // Diagnostic-only, self-delimiting exact basename + length + bytes. Never repository state.
    private static void Pack(string[] paths, string target)
    {
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
        using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
        {
            writer.Write(paths.Length);
            foreach (var path in paths)
            { writer.Write(Path.GetFileName(path)); writer.Write(new FileInfo(path).Length); using var input = File.OpenRead(path); input.CopyTo(file, 1024 * 1024); }
        }
        file.Flush(true);
    }
    private static void Unpack(string path, string target)
    {
        Directory.CreateDirectory(target);
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file, Encoding.UTF8, true);
        var count = reader.ReadInt32(); Assert.InRange(count, 1, 10_000);
        var buffer = new byte[1024 * 1024];
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString(); Assert.Matches("^[0-9a-f]{32}\\.seg$", name);
            var remaining = reader.ReadInt64(); Assert.InRange(remaining, 160, 67_108_864);
            using var output = new FileStream(Path.Combine(target, name), FileMode.CreateNew, FileAccess.Write);
            while (remaining > 0) { var n = (int)Math.Min(buffer.Length, remaining); file.ReadExactly(buffer.AsSpan(0, n)); output.Write(buffer, 0, n); remaining -= n; }
        }
        Assert.Equal(file.Length, file.Position);
    }
}
