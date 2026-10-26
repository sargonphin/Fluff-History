using FluffHistory.Core;

namespace FluffHistory.Storage;

/// <summary>Disposable physical range cache and append extents, never repository authority.</summary>
internal sealed class MetadataWorkspace : IDisposable
{
    private static readonly AsyncLocal<Action<Stream, Stream, long>?> CopyObserver = new();
    internal static Action<Stream, Stream, long>? CopyObserved { get => CopyObserver.Value; set => CopyObserver.Value = value; }
    private const int CachePageBytes = 64 * 1024;
    private readonly Repository _repository;
    private readonly string _root;
    private readonly CancellationToken _token;
    private readonly bool _streamMetadataReads;
    private readonly Dictionary<Guid, long> _originalLengths = [];
    private readonly Dictionary<Guid, Entry> _entries = [];
    private DecodedBlockCache? _decoded;
    private bool _disposed;
    private long _maximumDecodedBytes = 32L * 1024 * 1024;
    internal long MaximumDecodedBytes
    {
        get => _maximumDecodedBytes;
        set
        {
            if (_decoded is not null) throw new InvalidOperationException("Set decoded RAM budget before reading metadata.");
            ArgumentOutOfRangeException.ThrowIfLessThan(value, MetadataBlock.MaximumDecodedBytes); _maximumDecodedBytes = value;
        }
    }
    internal DecodedBlockCache DecodedBlocks
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); return _decoded ??= new(MaximumDecodedBytes); }
    }
    internal Action<SegmentHeader, BlockHeader>? DecodedObserved { get; set; }
    private string? _directory;
    private sealed record Entry(string Source, string Cache, long Length)
    { internal readonly Dictionary<long, long> Pages = []; }
    internal long DestinationSegmentFetches { get; private set; }
    internal long DestinationRangeFetches { get; private set; }
    internal long DestinationBytesFetched { get; private set; }
    internal long DecodedCacheBytes => _decoded?.CurrentBytes ?? 0;
    internal long DecodedCachePeakBytes => _decoded?.PeakBytes ?? 0;
    internal long DecodedCacheHits => _decoded?.Hits ?? 0;
    internal long DecodedCacheMisses => _decoded?.Misses ?? 0;
    internal long DecodedCacheEvictions => _decoded?.Evictions ?? 0;
    internal long MaximumMaterializedBytes { get; set; } = long.MaxValue;
    internal string? CreatedDirectory => _directory;
    internal IReadOnlyDictionary<Guid, long> OriginalLengths => _originalLengths;
    internal CancellationToken CancellationToken => _token;
    internal RepositoryCryptography? Crypto => _repository.Crypto;
    internal MetadataWorkspace(Repository repository, string? root, CancellationToken token = default, bool streamMetadataReads = false)
    {
        _repository = repository; _root = RepositoryLayout.NormalizeDirectory(root ?? Path.GetTempPath()); _token = token; _streamMetadataReads = streamMetadataReads;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (_root.Equals(repository.DirectoryPath, comparison) || _root.StartsWith(repository.DirectoryPath + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The local workspace root must be outside the repository destination.", nameof(root));
    }
    internal string DirectoryPath
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_directory is not null) return _directory;
            _token.ThrowIfCancellationRequested(); Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, $"FluffHistory-{_repository.Identity.RepositoryId:N}-{Guid.NewGuid():N}");
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Local workspace collision.");
            Directory.CreateDirectory(path); _directory = path; Directory.CreateDirectory(Path.Combine(path, "metadata")); return path;
        }
    }
    internal Stream OpenSegment(Guid id, FileAccess access = FileAccess.Read)
    {
        _token.ThrowIfCancellationRequested(); var target = SegmentReader.SegmentPath(DirectoryPath, id);
        if (!_entries.TryGetValue(id, out var entry))
        {
            if (File.Exists(target)) return new FileStream(target, FileMode.Open, access, FileShare.ReadWrite);
            var source = SegmentReader.SegmentPath(_repository.DirectoryPath, id); RepositoryLayout.RequireFile(source);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = input.Length;
            Format.Require(length >= Format.SegmentHeaderBytes && length <= MetadataBlock.MaximumPhysicalBytes, "physical segment length");
            var cache = Path.Combine(DirectoryPath, "metadata", id.ToString("N") + ".cache");
            if (!_streamMetadataReads) { using var created = File.Create(cache); }
            entry = new(source, cache, length); _entries.Add(id, entry); _originalLengths.Add(id, length); DestinationSegmentFetches++;
        }
        if (access == FileAccess.ReadWrite && !File.Exists(target))
        {
            using var read = new RangeStream(this, entry, null, false);
            var header = new byte[Format.SegmentHeaderBytes]; read.ReadExactly(header);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite); output.Write(header);
        }
        FileStream? suffix = File.Exists(target) ? new FileStream(target, FileMode.Open, access, FileShare.ReadWrite) : null;
        try { return new RangeStream(this, entry, suffix, access == FileAccess.ReadWrite); }
        catch { suffix?.Dispose(); throw; }
    }
    internal SegmentAppender CreateAppender(Authority baseline, Guid epoch, long rotation)
    { _ = DirectoryPath; return new(DirectoryPath, _repository.Identity, epoch, baseline, rotation, this, Crypto); }

    private sealed class RangeStream : Stream
    {
        private readonly MetadataWorkspace _owner;
        private readonly Entry _entry;
        private readonly FileStream _source;
        private readonly FileStream? _cache;
        private readonly FileStream? _suffix;
        private readonly bool _writable;
        private long _position;
        internal RangeStream(MetadataWorkspace owner, Entry entry, FileStream? suffix, bool writable)
        {
            _owner = owner; _entry = entry; _suffix = suffix; _writable = writable;
            _source = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            try { if (!owner._streamMetadataReads) _cache = new FileStream(entry.Cache, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1); }
            catch { _source.Dispose(); throw; }
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => _writable;
        public override long Length => _entry.Length + (_suffix is null ? 0 : _suffix.Length - Format.SegmentHeaderBytes);
        public override long Position { get => _position; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
        public override int Read(Span<byte> buffer)
        {
            _owner._token.ThrowIfCancellationRequested(); var total = 0;
            while (!buffer.IsEmpty && _position < Length)
            {
                int count;
                if (_position < _entry.Length)
                {
                    if (_cache is null)
                    {
                        count = (int)Math.Min(buffer.Length, _entry.Length - _position);
                        _source.Position = _position; _source.ReadExactly(buffer[..count]);
                        _owner.DestinationBytesFetched += count; _owner.DestinationRangeFetches++;
                        _position += count; total += count; buffer = buffer[count..]; continue;
                    }
                    var page = _position / CachePageBytes; var start = page * CachePageBytes;
                    var bytes = (int)Math.Min(CachePageBytes, _entry.Length - start);
                    if (!_entry.Pages.TryGetValue(page, out var cached))
                    {
                        if (bytes > _owner.MaximumMaterializedBytes - _owner.DestinationBytesFetched)
                            throw new IOException("Maintenance materialized-metadata budget exceeded.");
                        var data = new byte[bytes]; _source.Position = start; _source.ReadExactly(data);
                        cached = _cache.Length; _cache.Position = cached; _cache.Write(data); _cache.Flush();
                        _entry.Pages.Add(page, cached); _owner.DestinationBytesFetched += bytes; _owner.DestinationRangeFetches++;
                        CopyObserved?.Invoke(_source, _cache, bytes);
                    }
                    var within = (int)(_position - start); count = Math.Min(buffer.Length, bytes - within);
                    _cache.Position = cached + within; _cache.ReadExactly(buffer[..count]);
                }
                else
                {
                    count = (int)Math.Min(buffer.Length, Length - _position);
                    _suffix!.Position = Format.SegmentHeaderBytes + _position - _entry.Length; _suffix.ReadExactly(buffer[..count]);
                }
                _position += count; total += count; buffer = buffer[count..];
            }
            return total;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _owner._token.ThrowIfCancellationRequested();
            if (!_writable || _suffix is null || _position < _entry.Length || _position > Length)
                throw new IOException("A metadata workspace may only append new physical bytes.");
            _suffix.Position = Format.SegmentHeaderBytes + _position - _entry.Length; _suffix.Write(buffer); _position += buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => _suffix?.Flush();
        public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin switch
        { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        public override void SetLength(long value) => throw new NotSupportedException("Committed metadata is immutable.");
        protected override void Dispose(bool disposing)
        { if (disposing) { _source.Dispose(); _cache?.Dispose(); _suffix?.Dispose(); } base.Dispose(disposing); }
    }
    internal static void Copy(Stream source, Stream destination, long length, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested(); var count = (int)Math.Min(length, buffer.Length);
            source.ReadExactly(buffer.AsSpan(0, count)); destination.Write(buffer, 0, count); length -= count;
            CopyObserved?.Invoke(source, destination, count);
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _decoded?.Dispose(); if (_directory is null) return;
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
