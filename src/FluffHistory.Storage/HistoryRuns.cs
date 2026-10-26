using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal sealed class HistoryFoldStatistics
{
    internal long Records, Additions, Removals, Survivors, ScratchBytesWritten, PeakScratchBytes, RunsCreated, Merges;
    internal int PeakBufferedEntries, PeakBufferedKeyBytes, PeakMergeReaders;
}

internal sealed record HistoryEvent(byte[] Key, Guid Id, SemanticKind Kind, FrameAddress Address);

/// <summary>Checked, disposable external runs. No roots, repository header or authority live here.</summary>
internal sealed class HistoryScratch(string directory, MaintenanceLimits limits, CancellationToken token, RepositoryCryptography? crypto = null) : IDisposable
{
    private readonly Dictionary<string, long> _owned = [];
    private long _live;
    internal HistoryFoldStatistics Statistics { get; } = new();
    internal sealed record Run(string Path, long Count, RepositoryCryptography? Crypto = null);
    internal void Charge(string path, int bytes)
    {
        if (bytes > limits.MaximumScratchBytes - _live || bytes > limits.MaximumScratchBytesWritten - Statistics.ScratchBytesWritten)
            throw new IOException("History-fold scratch space/write budget exceeded.");
        _owned[path] += bytes; _live += bytes; Statistics.ScratchBytesWritten += bytes;
        Statistics.PeakScratchBytes = Math.Max(Statistics.PeakScratchBytes, _live);
    }
    internal Run Write(IEnumerable<HistoryEvent> events)
    {
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".run"); long count = 0;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            _owned.Add(path, 0); Statistics.RunsCreated++;
            foreach (var item in events)
            {
                token.ThrowIfCancellationRequested();
                using var writer = new BufferWriter(); writer.U16((ushort)item.Key.Length); writer.Bytes(item.Key);
                writer.Guid(item.Id); writer.Byte((byte)item.Kind); writer.RequiredAddress(item.Address);
                var plain = writer.Finish();
                var body = crypto?.Protect(plain, "history-scratch", System.Text.Encoding.UTF8.GetBytes(path + ":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture))) ?? plain;
                if (crypto is not null) CryptographicOperations.ZeroMemory(plain);
                Charge(path, checked(36 + body.Length));
                using var header = new BufferWriter(); header.I32(body.Length);
                stream.Write(header.Finish()); stream.Write(SHA256.HashData(body)); stream.Write(body); count++;
            }
            stream.Flush();
        }
        return new(path, count, crypto);
    }
    internal void Delete(Run run)
    {
        File.Delete(run.Path); _live -= _owned[run.Path]; _owned.Remove(run.Path);
    }
    internal Run Merge(IReadOnlyList<Run> runs, IComparer<HistoryEvent> order)
    {
        Statistics.Merges++; Statistics.PeakMergeReaders = Math.Max(Statistics.PeakMergeReaders, runs.Count);
        var readers = new List<Reader>();
        try
        {
            foreach (var run in runs) readers.Add(new(run, order, token));
            var expected = runs.Aggregate(0L, (total, run) => checked(total + run.Count));
            var result = Write(Merged()); Format.Require(result.Count == expected, "scratch merge lost records");
            foreach (var reader in readers) reader.Dispose(); readers.Clear();
            foreach (var run in runs) Delete(run);
            return result;
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
        IEnumerable<HistoryEvent> Merged()
        {
            var queue = new PriorityQueue<int, HistoryEvent>(order);
            for (var i = 0; i < readers.Count; i++) if (readers[i].MoveNext()) queue.Enqueue(i, readers[i].Current!);
            while (queue.TryDequeue(out var index, out var value))
            {
                token.ThrowIfCancellationRequested(); yield return value;
                if (readers[index].MoveNext()) queue.Enqueue(index, readers[index].Current!);
            }
        }
    }
    internal sealed class Reader(Run run, IComparer<HistoryEvent> order, CancellationToken token) : IDisposable
    {
        private readonly FileStream _stream = new(run.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        private long _read;
        internal HistoryEvent? Current { get; private set; }
        internal bool MoveNext()
        {
            token.ThrowIfCancellationRequested();
            if (_read == run.Count) { Format.Require(_stream.Position == _stream.Length, "scratch trailing records"); return false; }
            var header = new byte[36]; _stream.ReadExactly(header);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            var extra = run.Crypto is null ? 0 : RepositoryCryptography.Overhead;
            Format.Require(length >= 131 + extra && length <= Format.MaximumKeyBytes + 99 + extra, "scratch record length");
            var body = new byte[length]; _stream.ReadExactly(body);
            Format.Require(SHA256.HashData(body).AsSpan().SequenceEqual(header.AsSpan(4)), "scratch checksum");
            if (run.Crypto is not null) body = run.Crypto.Unprotect(body, "history-scratch", System.Text.Encoding.UTF8.GetBytes(run.Path + ":" + _read.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var reader = new BufferReader(body); var keyLength = reader.U16();
            Format.Require(keyLength is >= 32 and <= Format.MaximumKeyBytes && (keyLength & 1) == 0, "scratch key length");
            var key = reader.Take(keyLength).ToArray(); var id = reader.Guid(); _ = PathKeys.DecodeVersion(key, id);
            var kind = (SemanticKind)reader.Byte();
            Format.Require(kind is SemanticKind.VersionAdded or SemanticKind.VersionRemoved, "scratch event type");
            var address = reader.RequiredAddress(); MetadataCodec.RequireKind(address, FrameKind.Semantic); reader.End();
            var next = new HistoryEvent(key, id, kind, address);
            Format.Require(Current is null || order.Compare(Current, next) <= 0, "scratch order");
            Current = next; _read++; return true;
        }
        public void Dispose() => _stream.Dispose();
    }
    public void Dispose()
    {
        foreach (var path in _owned.Keys)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        _owned.Clear();
    }
}

internal sealed class HistoryRuns(HistoryScratch scratch, IComparer<HistoryEvent> order, int maximumEntries = 4096)
{
    private const int MaximumKeyBytes = 4 * 1024 * 1024, FanIn = 8;
    private readonly List<HistoryEvent> _buffer = [];
    private readonly List<List<HistoryScratch.Run>> _levels = [];
    private int _keyBytes;
    internal void Add(HistoryEvent item)
    {
        if (_buffer.Count != 0 && (_buffer.Count == maximumEntries || item.Key.Length > MaximumKeyBytes - _keyBytes)) Flush();
        _buffer.Add(item); _keyBytes += item.Key.Length;
        scratch.Statistics.PeakBufferedEntries = Math.Max(scratch.Statistics.PeakBufferedEntries, _buffer.Count);
        scratch.Statistics.PeakBufferedKeyBytes = Math.Max(scratch.Statistics.PeakBufferedKeyBytes, _keyBytes);
    }
    private void Flush()
    {
        if (_buffer.Count == 0) return;
        _buffer.Sort(order); var run = scratch.Write(_buffer); _buffer.Clear(); _keyBytes = 0;
        for (var level = 0; ; level++)
        {
            if (_levels.Count == level) _levels.Add([]);
            var group = _levels[level]; group.Add(run);
            if (group.Count < FanIn) break;
            run = scratch.Merge(group, order); group.Clear();
        }
    }
    internal HistoryScratch.Run Finish()
    {
        Flush(); var runs = _levels.SelectMany(level => level).ToList();
        if (runs.Count == 0) return scratch.Write([]);
        while (runs.Count > 1)
        {
            var next = new List<HistoryScratch.Run>();
            for (var i = 0; i < runs.Count; i += FanIn)
            {
                var group = runs.Skip(i).Take(FanIn).ToArray(); next.Add(group.Length == 1 ? group[0] : scratch.Merge(group, order));
            }
            runs = next;
        }
        return runs[0];
    }
}
