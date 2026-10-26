using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

internal enum CapacitySortStage { RecordWritten, RunWritten, MergeStarted }

internal sealed class CapacityOrder : IDisposable
{
    internal static readonly IComparer<RetentionVersion> PublicationOrder = Comparer<RetentionVersion>.Create((a, b) =>
    { var c = a.Identity.Sequence.CompareTo(b.Identity.Sequence); return c != 0 ? c : a.Identity.Ordinal.CompareTo(b.Identity.Ordinal); });
    internal static readonly IComparer<RetentionVersion> OldestOrder = Comparer<RetentionVersion>.Create((a, b) =>
    { var c = a.StoredUtc.CompareTo(b.StoredUtc); return c != 0 ? c : PublicationOrder.Compare(a, b); });
    private readonly CapacityScratch _scratch;
    private readonly CapacityScratch.Run _ordered;
    private readonly CancellationToken _token;
    internal string DirectoryPath => _scratch.DirectoryPath;
    private CapacityOrder(CapacityScratch scratch, CapacityScratch.Run ordered, CancellationToken token)
    { _scratch = scratch; _ordered = ordered; _token = token; }

    internal static CapacityOrder Derive(Repository repository, RepositoryReader reader, CapacityRetentionOptions options,
        CapacityRetentionResult result, CancellationToken token, Action<CapacitySortStage, string>? observe = null)
    {
        var scratch = new CapacityScratch(repository, options, result.Sort, token, observe);
        try
        {
            result.HistoryScans++;
            var publication = new Runs(scratch, PublicationOrder, options);
            foreach (var version in reader.EnumerateRetentionVersions(cancellationToken: token))
            {
                if (++result.Examined > options.MaximumVersions) throw new CapacityWorkLimitException("Capacity history work limit exceeded before removal.");
                if (version.IsCurrent) result.Protected++; else result.Eligible++;
                publication.Add(version); result.Sort.InputVersions++;
            }
            var identities = publication.Finish(); var oldest = new Runs(scratch, OldestOrder, options);
            // All survivors participate: publication identity must be unique even across different
            // StoredUtc values or a protected/ineligible row. No O(H) uniqueness set is needed.
            using (var input = scratch.Read(identities, PublicationOrder))
            {
                RetentionVersion? prior = null;
                while (input.MoveNext())
                {
                    var item = input.Current!;
                    Require(prior is null || PublicationOrder.Compare(prior, item) < 0, "ambiguous sequence/ordinal across versions");
                    prior = item; result.Sort.PublicationRecordsValidated++;
                    if (!item.IsCurrent) { oldest.Add(item); result.Sort.EligibleInput++; }
                }
            }
            scratch.Delete(identities); var ordered = oldest.Finish();
            // Validate the complete derived order before any deletion. Re-read checks remain active
            // during consumption; scratch never replaces captured repository truth or removal checks.
            using (var input = scratch.Read(ordered, OldestOrder))
            {
                RetentionVersion? prior = null;
                while (input.MoveNext())
                {
                    Require(prior is null || OldestOrder.Compare(prior, input.Current!) < 0, "ambiguous oldest order");
                    prior = input.Current; result.Sort.OrderedCandidates++;
                }
            }
            Require(result.Sort.OrderedCandidates == result.Eligible, "candidate sort count");
            return new(scratch, ordered, token);
        }
        catch { scratch.Dispose(); throw; }
        finally { result.Reads = reader.LastRetentionStatistics; }
    }
    internal IEnumerable<RetentionVersion> Candidates()
    {
        using var reader = _scratch.Read(_ordered, OldestOrder);
        while (reader.MoveNext()) { _token.ThrowIfCancellationRequested(); yield return reader.Current!; }
    }
    internal string CandidateFile => _ordered.Path;
    public void Dispose() => _scratch.Dispose();
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException("Invalid capacity ordering scratch: " + message); }

    private sealed class Runs(CapacityScratch scratch, IComparer<RetentionVersion> order, CapacityRetentionOptions options)
    {
        private const int FanIn = 8;
        private readonly List<RetentionVersion> _buffer = [];
        private readonly List<List<CapacityScratch.Run>> _levels = [];
        private long _bytes;
        internal void Add(RetentionVersion item)
        {
            var charge = RetentionOptions.Charge(item);
            if (_buffer.Count != 0 && (_buffer.Count == options.SortChunkEntries || charge > options.SortChunkBytes - _bytes)) Flush();
            _buffer.Add(item); _bytes += charge;
            scratch.Statistics.PeakChunkEntries = Math.Max(scratch.Statistics.PeakChunkEntries, _buffer.Count);
            scratch.Statistics.PeakChunkBytes = Math.Max(scratch.Statistics.PeakChunkBytes, _bytes);
        }
        private void Flush()
        {
            if (_buffer.Count == 0) return;
            _buffer.Sort(order); var run = scratch.Write(_buffer); _buffer.Clear(); _bytes = 0;
            for (int level = 0; ; level++)
            {
                if (_levels.Count == level) _levels.Add([]);
                var group = _levels[level]; group.Add(run);
                if (group.Count != FanIn) break;
                run = scratch.Merge(group, order); group.Clear();
            }
        }
        internal CapacityScratch.Run Finish()
        {
            Flush(); var runs = _levels.SelectMany(l => l).ToList();
            if (runs.Count == 0) return scratch.Write([]);
            while (runs.Count > 1)
            {
                var next = new List<CapacityScratch.Run>();
                foreach (var group in runs.Chunk(FanIn)) next.Add(group.Length == 1 ? group[0] : scratch.Merge(group, order));
                runs = next;
            }
            return runs[0];
        }
    }
}

/// <summary>Operation-owned local checked runs; no repository header, state, payload path or durable session.</summary>
internal sealed class CapacityScratch : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly CapacityRetentionOptions _options;
    private readonly CancellationToken _token;
    private readonly Action<CapacitySortStage, string>? _observe;
    private readonly Dictionary<string, long> _owned = [];
    private readonly RepositoryCryptography? _crypto;
    private readonly string _directory;
    private bool _created;
    private long _live;
    private int _readers, _writers;
    internal string DirectoryPath => _directory;
    internal CapacitySortStatistics Statistics { get; }
    internal sealed record Run(string Path, long Count);
    internal CapacityScratch(Repository repository, CapacityRetentionOptions options, CapacitySortStatistics statistics, CancellationToken token,
        Action<CapacitySortStage, string>? observe = null)
    {
        _options = options; _token = token; Statistics = statistics; _observe = observe; _crypto = repository.Crypto;
        var root = PhysicalPaths.Normalize(options.WorkspaceRoot ?? Path.GetTempPath());
        _directory = Path.Combine(root, $"FluffHistory-capacity-{repository.Identity.RepositoryId:N}-{Guid.NewGuid():N}");
        if (PhysicalPaths.Overlaps(PhysicalPaths.OverlapKey(_directory), PhysicalPaths.OverlapKey(repository.DirectoryPath)))
            throw new ArgumentException("Capacity scratch must be outside the repository.", nameof(options));
        if (OperatingSystem.IsWindows() && (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network))
            throw new ArgumentException("Capacity ordering scratch must use a local filesystem.", nameof(options));
        CheckParents(_directory);
    }
    private static void CheckParents(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
            CapacityOrder.Require((attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) == 0 && (attributes & FileAttributes.Directory) != 0, "unsafe workspace directory");
        }
    }
    private void Charge(string path, int bytes)
    {
        if (bytes > _options.MaximumScratchBytes - _live || bytes > _options.MaximumScratchBytesWritten - Statistics.ScratchBytesWritten)
            throw new CapacityWorkLimitException("Capacity sorting scratch space/write limit exceeded before removal.");
        _owned[path] += bytes; _live += bytes; Statistics.ScratchBytesWritten += bytes; Statistics.PeakScratchBytes = Math.Max(Statistics.PeakScratchBytes, _live);
    }
    internal void Opened(bool write)
    {
        if (write) _writers++; else _readers++;
        Statistics.PeakOpenFiles = Math.Max(Statistics.PeakOpenFiles, _readers + _writers);
        Statistics.PeakReadFiles = Math.Max(Statistics.PeakReadFiles, _readers);
    }
    internal void Closed(bool write) { if (write) _writers--; else _readers--; }
    internal Run Write(IEnumerable<RetentionVersion> items)
    {
        _token.ThrowIfCancellationRequested(); CheckParents(_directory);
        if (!_created)
        {
            if (Directory.Exists(_directory) || File.Exists(_directory)) throw new IOException("Capacity scratch collision.");
            Directory.CreateDirectory(_directory); _created = true;
        }
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".run"); long count = 0;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            _owned.Add(path, 0); Opened(true); Statistics.RunsCreated++;
            try
            {
                Span<byte> header = stackalloc byte[36];
                foreach (var item in items)
                {
                    _token.ThrowIfCancellationRequested(); var plain = Encode(item);
                    var body = _crypto?.Protect(plain, "capacity-scratch", Encoding.UTF8.GetBytes(path + ":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture))) ?? plain;
                    if (_crypto is not null) CryptographicOperations.ZeroMemory(plain);
                    Charge(path, 36 + body.Length);
                    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length); SHA256.HashData(body, header[4..]);
                    stream.Write(header); stream.Write(body); count++; _observe?.Invoke(CapacitySortStage.RecordWritten, path);
                }
                stream.Flush();
            }
            finally { Closed(true); }
        }
        _observe?.Invoke(CapacitySortStage.RunWritten, path); return new(path, count);
    }
    internal void Delete(Run run) { CheckParents(_directory); File.Delete(run.Path); _live -= _owned[run.Path]; _owned.Remove(run.Path); }
    internal Reader Read(Run run, IComparer<RetentionVersion> order) => new(this, run, order, _token);
    internal Run Merge(IReadOnlyList<Run> runs, IComparer<RetentionVersion> order)
    {
        Statistics.Merges++; Statistics.PeakMergeFanIn = Math.Max(Statistics.PeakMergeFanIn, runs.Count);
        _observe?.Invoke(CapacitySortStage.MergeStarted, runs[0].Path);
        var readers = new List<Reader>();
        try
        {
            foreach (var run in runs) readers.Add(Read(run, order));
            var expected = runs.Sum(r => r.Count); var output = Write(Values()); CapacityOrder.Require(output.Count == expected, "merge count");
            foreach (var input in readers) input.Dispose(); readers.Clear();
            foreach (var run in runs) Delete(run);
            return output;
        }
        finally { foreach (var input in readers) input.Dispose(); }
        IEnumerable<RetentionVersion> Values()
        {
            var queue = new PriorityQueue<int, RetentionVersion>(order);
            for (var i = 0; i < readers.Count; i++) if (readers[i].MoveNext()) queue.Enqueue(i, readers[i].Current!);
            while (queue.TryDequeue(out var index, out var value))
            { _token.ThrowIfCancellationRequested(); yield return value; if (readers[index].MoveNext()) queue.Enqueue(index, readers[index].Current!); }
        }
    }
    private static byte[] Encode(RetentionVersion item)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(item.Identity.Path.SourceRootId.ToByteArray(true)); writer.Write(item.Identity.VersionId.ToByteArray(true));
        writer.Write(item.Identity.Sequence); writer.Write(item.Identity.Ordinal); writer.Write(item.StoredUtc.Ticks); writer.Write(item.Length); writer.Write(item.IsCurrent ? (byte)1 : (byte)0);
        var name = Utf8.GetBytes(item.Identity.Path.CanonicalPath); writer.Write(name.Length); writer.Write(name); return stream.ToArray();
    }
    internal sealed class Reader : IDisposable
    {
        private readonly CapacityScratch _scratch;
        private readonly Run _run;
        private readonly IComparer<RetentionVersion> _order;
        private readonly CancellationToken _token;
        private readonly FileStream _stream;
        private long _read;
        private bool _disposed;
        internal RetentionVersion? Current { get; private set; }
        internal Reader(CapacityScratch scratch, Run run, IComparer<RetentionVersion> order, CancellationToken token)
        {
            _scratch = scratch; _run = run; _order = order; _token = token; CheckParents(scratch._directory);
            CapacityOrder.Require(scratch._owned.ContainsKey(run.Path), "unowned scratch run");
            var attributes = File.GetAttributes(run.Path);
            CapacityOrder.Require((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device | FileAttributes.Encrypted)) == 0, "unsafe scratch file");
            _stream = new(run.Path, FileMode.Open, FileAccess.Read, FileShare.Read); scratch.Opened(false);
        }
        internal bool MoveNext()
        {
            _token.ThrowIfCancellationRequested();
            if (_read == _run.Count) { CapacityOrder.Require(_stream.Position == _stream.Length, "trailing scratch records"); return false; }
            Span<byte> header = stackalloc byte[36]; _stream.ReadExactly(header); var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var extra = _scratch._crypto is null ? 0 : RepositoryCryptography.Overhead;
            CapacityOrder.Require(length >= 66 + extra && length <= 8257 + extra, "scratch record length"); var body = new byte[length]; _stream.ReadExactly(body);
            Span<byte> digest = stackalloc byte[32]; SHA256.HashData(body, digest); CapacityOrder.Require(digest.SequenceEqual(header[4..]), "scratch checksum");
            if (_scratch._crypto is not null) body = _scratch._crypto.Unprotect(body, "capacity-scratch", Encoding.UTF8.GetBytes(_run.Path + ":" + _read.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using var stream = new MemoryStream(body, false); using var reader = new BinaryReader(stream, Utf8);
            var root = new Guid(reader.ReadBytes(16), true); var id = new Guid(reader.ReadBytes(16), true);
            var sequence = reader.ReadInt64(); var ordinal = reader.ReadInt32(); var ticks = reader.ReadInt64(); var size = reader.ReadInt64(); var current = reader.ReadByte();
            var pathBytes = reader.ReadInt32(); CapacityOrder.Require(pathBytes is >= 1 and <= 8192 && pathBytes == stream.Length - stream.Position, "scratch path length");
            var name = Utf8.GetString(reader.ReadBytes(pathBytes)); var path = new HistoryPath(root, name);
            CapacityOrder.Require(name == path.CanonicalPath && id != Guid.Empty && sequence > 0 && ordinal is >= 0 and < MutationBatch.MaximumChanges &&
                ticks > 0 && ticks <= DateTimeOffset.MaxValue.Ticks && size >= 0 && current <= 1, "scratch fields");
            var next = new RetentionVersion(new(path, sequence, ordinal, id), new(ticks, TimeSpan.Zero), size, current == 1);
            CapacityOrder.Require(Current is null || _order.Compare(Current, next) <= 0, "scratch order"); Current = next; _read++; return true;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _stream.Dispose(); _scratch.Closed(false); }
    }
    public void Dispose()
    {
        try { CheckParents(_directory); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        foreach (var path in _owned.Keys) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        _owned.Clear();
        if (_created) { try { Directory.Delete(_directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
