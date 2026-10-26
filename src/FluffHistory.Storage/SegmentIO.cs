using FluffHistory.Core;

namespace FluffHistory.Storage;

internal sealed class IoStatistics
{
    internal long PagesRead, PagesWritten, SemanticReads, FramesWritten, BytesWritten, SegmentsCreated;
    // Logical frame bytes plus segment headers. Encoded ranges/decode work are separate.
    internal long BytesRead, SegmentOpens, HandleEvictions, PeakHandles;
    internal long PhysicalBytesRead, PhysicalReads, BlocksDecoded, DecodedBytes, PeakDecodedBytes, PhysicalBytesWritten, BlocksWritten;
    internal long DecodedCacheReadBytes;
}

internal sealed class SegmentReader(string directory, RepositoryIdentity repository, Guid epoch, FrameAddress frontier,
    MetadataWorkspace? workspace = null, IoStatistics? statistics = null, ComparisonReadStatistics? comparison = null,
    int readAheadBytes = -1, RepositoryCryptography? crypto = null) : IDisposable
{
    internal const int NetworkReadWindowBytes = 1024 * 1024;
    private readonly int _readAheadBytes = WindowSize(directory, workspace, comparison, readAheadBytes);
    private readonly Dictionary<Guid, Handle> _handles = [];
    private readonly Queue<Guid> _order = [];
    private sealed class Handle(Stream stream, SegmentHeader header, long length, MetadataBlockReader blocks)
    {
        internal readonly Stream Stream = stream;
        internal readonly SegmentHeader Header = header;
        internal readonly long Length = length;
        internal readonly MetadataBlockReader Blocks = blocks;
    }
    private static int WindowSize(string path, MetadataWorkspace? workspace, ComparisonReadStatistics? comparison, int requested)
    {
        if (requested < -1 || requested > NetworkReadWindowBytes) throw new ArgumentOutOfRangeException(nameof(requested));
        if (requested >= 0) return requested;
        // Point/control reads decode only their blocks. Grouped network projections
        // opt into encoded prefetch; workspaces localize requested ranges separately.
        return workspace is null && comparison is not null && WindowsFileSystem.IsNetwork(path) ? NetworkReadWindowBytes : 0;
    }
    internal IoStatistics Statistics { get; } = statistics ?? comparison?.Io ?? new();
    private void Requested(int bytes)
    {
        if (comparison is null) return;
        comparison.DestinationReadRequests++; comparison.DestinationBytesRequested += bytes;
    }
    internal void CheckAddress(FrameAddress p)
    {
        p.Validate();
        Format.Require(p.Serial <= frontier.Serial && p.SegmentOrdinal <= frontier.SegmentOrdinal &&
            (p.SegmentOrdinal != frontier.SegmentOrdinal || p.SegmentId == frontier.SegmentId && p.End <= frontier.End), "pointer beyond committed frontier");
    }
    internal byte[] Read(FrameAddress p)
    {
        CheckAddress(p);
        var handle = Open(p.SegmentId, p.SegmentOrdinal);
        Format.Require(p.End <= handle.Length, "truncated frame");
        var bytes = new byte[Format.FrameHeaderBytes + p.Length]; ReadFrame(handle, p, bytes);
        if (p.Kind == FrameKind.Page) Statistics.PagesRead++;
        if (p.Kind == FrameKind.Semantic) Statistics.SemanticReads++;
        return MetadataCodec.ValidateFrame(bytes, handle.Header, p);
    }
    internal ReadOnlySpan<byte> ReadComparison(FrameAddress p, byte[] buffer, System.Security.Cryptography.IncrementalHash hash)
    {
        CheckAddress(p);
        var handle = Open(p.SegmentId, p.SegmentOrdinal);
        Format.Require(p.Kind == FrameKind.Semantic && p.End <= handle.Length, "comparison frame type/extent");
        var bytes = buffer.AsSpan(0, Format.FrameHeaderBytes + p.Length);
        ReadFrame(handle, p, bytes); Statistics.SemanticReads++;
        comparison!.LogicalFrameBytes += bytes.Length;
        return ComparisonFrameValidator.Validate(bytes, handle.Header, p, hash);
    }
    private void ReadFrame(Handle handle, FrameAddress address, Span<byte> bytes)
    {
        handle.Blocks.Read(address, bytes); Statistics.BytesRead += bytes.Length;
    }
    internal SegmentHeader Header(Guid id, long ordinal) => Open(id, ordinal).Header;
    internal long CommittedLength(Guid id, long ordinal) => Open(id, ordinal).Length;
    private Handle Open(Guid id, long ordinal)
    {
        if (_handles.TryGetValue(id, out var found)) { Format.Require(found.Header.Ordinal == ordinal, "segment ordinal"); return found; }
        Stream stream;
        MetadataBlockReader? blocks = null;
        try
        {
            if (workspace is not null) stream = workspace.OpenSegment(id);
            else
            {
                var path = SegmentPath(directory, id); RepositoryLayout.RequireFile(path);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            }
        }
        catch (FileNotFoundException)
        {
            // An expired capture is explicit; current-generation absence remains corruption.
            if (AuthorityFiles.CurrentEpoch(directory, repository.RepositoryId, crypto) != epoch) throw new ReaderGenerationExpiredException();
            throw Format.Invalid("required segment missing");
        }
        try
        {
            // Committed frame extents are immutable. Cache this opened extent once;
            // ReadExactly still fails on truncation and CheckAddress guards the capture.
            var length = stream.Length;
            Format.Require(length >= Format.SegmentHeaderBytes && length <= MetadataBlock.MaximumPhysicalBytes, "physical segment length");
            Statistics.SegmentOpens++;
            var bytes = new byte[Format.SegmentHeaderBytes]; stream.ReadExactly(bytes); Requested(bytes.Length); Statistics.BytesRead += bytes.Length; var h = MetadataCodec.DecodeSegment(bytes);
            Statistics.PhysicalBytesRead += bytes.Length; Statistics.PhysicalReads++;
            Format.Require(h.RepositoryId == repository.RepositoryId && h.Epoch == epoch && h.SegmentId == id && h.Ordinal == ordinal, "segment binding");
            blocks = new MetadataBlockReader(stream, h, Statistics, Requested, _readAheadBytes, workspace, crypto);
            var logicalLength = ordinal < frontier.SegmentOrdinal ? blocks.ValidateSeal().LogicalLength : frontier.End;
            if (_handles.Count == 8) { var oldest = _order.Dequeue(); _handles[oldest].Blocks.Dispose(); _handles[oldest].Stream.Dispose(); _handles.Remove(oldest); Statistics.HandleEvictions++; }
            var handle = new Handle(stream, h, logicalLength, blocks); _handles.Add(id, handle); _order.Enqueue(id);
            Statistics.PeakHandles = Math.Max(Statistics.PeakHandles, _handles.Count);
            return handle;
        }
        catch { blocks?.Dispose(); stream.Dispose(); throw; }
    }
    internal static void ValidateSeal(Stream stream, SegmentHeader header, long? observedLength = null, RepositoryCryptography? crypto = null)
    {
        using var blocks = new MetadataBlockReader(stream, header, new(), crypto: crypto); _ = blocks.ValidateSeal();
    }
    internal static string SegmentPath(string directory, Guid id)
    {
        Format.Require(RepositoryLayout.IsExistingDirectory(Path.Combine(directory, "metadata")), "missing metadata directory");
        return Path.Combine(directory, "metadata", id.ToString("N") + ".seg");
    }
    public void Dispose() { foreach (var h in _handles.Values) { h.Blocks.Dispose(); h.Stream.Dispose(); } _handles.Clear(); _order.Clear(); }
}

internal sealed class SegmentAppender : IDisposable
{
    private readonly string _directory;
    private readonly RepositoryIdentity _repository;
    private readonly Guid _epoch;
    private readonly long _rotation;
    private Stream? _stream;
    private readonly MetadataWorkspace? _workspace;
    private readonly RepositoryCryptography? _crypto;
    private readonly byte[] _block = new byte[MetadataBlock.MaximumDecodedBytes];
    private int _blockLength;
    private long _logicalPosition, _blockLogical, _previousBlockOffset;
    private SegmentHeader? _header;
    private long _serial;
    private readonly long _baselineSerial;
    private readonly Dictionary<FrameAddress, byte[]> _newFrames = [];
    private SegmentReader? _readback;
    private long _pendingBytes;
    internal IoStatistics Statistics { get; } = new();
    internal long MaximumOutputBytes { get; set; } = long.MaxValue;
    internal Func<long, Guid>? SegmentIdentity { get; set; }
    internal Action? SegmentCreated { get; set; }
    internal SegmentAppender(string directory, RepositoryIdentity repository, Guid epoch, Authority? baseline, long rotation = Format.MaximumSegmentBytes,
        MetadataWorkspace? workspace = null, RepositoryCryptography? crypto = null)
    {
        _directory = directory; _repository = repository; _epoch = epoch; _rotation = rotation; _workspace = workspace; _crypto = crypto;
        _serial = baseline?.Address.Serial ?? 0; _baselineSerial = _serial;
        if (baseline is not null && baseline.Root.Epoch == epoch)
        {
            using var reader = new SegmentReader(directory, repository, epoch, baseline.Address, workspace, crypto: _crypto);
            _header = reader.Header(baseline.Address.SegmentId, baseline.Address.SegmentOrdinal);
            var stream = Open(_header.SegmentId, FileAccess.ReadWrite);
            using var blocks = new MetadataBlockReader(stream, _header, new(), crypto: _crypto);
            var last = blocks.Last(baseline.Address);
            _logicalPosition = baseline.Address.End; _previousBlockOffset = last.Physical;
            if (stream.Length == last.End && last.LogicalEnd == baseline.Address.End) { _stream = stream; _stream.Position = _stream.Length; }
            else stream.Dispose(); // Never truncate or adopt an abandoned tail.
        }
    }
    internal FrameAddress Append(FrameKind kind, byte[] body)
    {
        if (body.Length > Format.MaximumFrameBytes) throw Format.Invalid("frame body limit");
        Limit(Format.FrameHeaderBytes + body.Length + Format.SegmentHeaderBytes + Format.SealBytes);
        if (_stream is null || _logicalPosition + Format.FrameHeaderBytes + body.Length + Format.SealBytes > _rotation)
            Rotate();
        var frame = MetadataCodec.EncodeFrame(_header!, _logicalPosition, checked(++_serial), kind, body, _stream!.Position);
        WriteLogical(frame.Bytes); _newFrames.Add(frame.Address, body);
        _pendingBytes += frame.Bytes.Length;
        if (_newFrames.Count >= 4096 || _pendingBytes >= 8388608) FlushAndValidate();
        Statistics.FramesWritten++; Statistics.BytesWritten += frame.Bytes.Length;
        if (kind == FrameKind.Page) Statistics.PagesWritten++;
        return frame.Address;
    }
    private void Limit(long additional)
    {
        if (additional > MaximumOutputBytes - Statistics.BytesWritten) throw new IOException("Maintenance output budget exceeded.");
    }
    private Stream Open(Guid id, FileAccess access) => _workspace?.OpenSegment(id, access) ??
        new FileStream(SegmentReader.SegmentPath(_directory, id), FileMode.Open, access, FileShare.ReadWrite, 1);
    private static void FlushDurable(Stream stream) { if (stream is FileStream file) file.Flush(true); else stream.Flush(); }
    private void WriteLogical(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_blockLength == 0) _blockLogical = _logicalPosition;
            var count = Math.Min(bytes.Length, _block.Length - _blockLength);
            bytes[..count].CopyTo(_block.AsSpan(_blockLength)); _blockLength += count; _logicalPosition += count; bytes = bytes[count..];
            if (_blockLength == _block.Length) FlushBlock();
        }
    }
    private void FlushBlock()
    {
        if (_blockLength == 0) return;
        var h = MetadataBlock.Write(_stream!, _header!, _blockLogical, _previousBlockOffset, _block.AsSpan(0, _blockLength),
            outputBudget: MaximumOutputBytes - Statistics.PhysicalBytesWritten, crypto: _crypto);
        Statistics.BlocksWritten++; Statistics.PhysicalBytesWritten += h.End - h.Physical;
        _previousBlockOffset = h.Physical; _blockLength = 0;
    }
    private void Rotate()
    {
        // An abandoned active tail is never parsed as journal data or truncated. Seal its
        // physical extent before linking a new segment. Already sealed segments are untouched.
        if (_stream is null && _header is not null)
        {
            var old = Open(_header.SegmentId, FileAccess.ReadWrite);
            try { SegmentReader.ValidateSeal(old, _header, crypto: _crypto); old.Dispose(); }
            catch (InvalidDataException)
            {
                old.Position = old.Length; _stream = old;
            }
            catch { old.Dispose(); throw; }
        }
        if (_stream is not null)
        {
            FlushBlock();
            using var w = new BufferWriter(); w.I64(_logicalPosition); w.I64(_serial);
            var seal = MetadataCodec.EncodeFrame(_header!, _logicalPosition, checked(++_serial), FrameKind.Seal, w.Finish(), _stream.Position);
            var h = MetadataBlock.Write(_stream, _header!, _logicalPosition, _previousBlockOffset, seal.Bytes, true,
                MaximumOutputBytes - Statistics.PhysicalBytesWritten, _crypto);
            Statistics.PhysicalBytesWritten += h.End - h.Physical; Statistics.BlocksWritten++;
            Statistics.BytesWritten += seal.Bytes.Length; FlushDurable(_stream); _stream.Dispose(); _stream = null;
        }
        var previous = _header;
        if (Format.SegmentHeaderBytes > MaximumOutputBytes - Statistics.PhysicalBytesWritten)
            throw new IOException("Maintenance physical-output budget exceeded.");
        var ordinal = checked((previous?.Ordinal ?? 0) + 1);
        _header = new(_repository.RepositoryId, SegmentIdentity?.Invoke(ordinal) ?? Guid.NewGuid(), _epoch, ordinal, previous?.SegmentId ?? Guid.Empty, previous?.Ordinal ?? 0);
        _stream = new FileStream(SegmentReader.SegmentPath(_directory, _header.SegmentId), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        _stream.Write(MetadataCodec.EncodeSegment(_header)); Statistics.BytesWritten += Format.SegmentHeaderBytes; Statistics.SegmentsCreated++;
        Statistics.PhysicalBytesWritten += Format.SegmentHeaderBytes;
        _logicalPosition = Format.SegmentHeaderBytes; _previousBlockOffset = 0; _blockLength = 0;
        SegmentCreated?.Invoke();
    }
    internal byte[]? ReadNew(FrameAddress address)
    {
        if (address.Serial <= _baselineSerial) return null;
        if (_newFrames.TryGetValue(address, out var body)) return body;
        // An absent body was already flushed/validated before its cache was cleared.
        // Reading it must not publish a partial block of unrelated pending frames.
        if (_readback is not null) return _readback.Read(address);
        using var reader = new SegmentReader(_directory, _repository, _epoch, address, _workspace, crypto: _crypto);
        return reader.Read(address);
    }
    internal void FlushAndValidate()
    {
        if (_stream is null) return;
        FlushBlock(); FlushDurable(_stream);
        if (_newFrames.Count == 0) return;
        _readback?.Dispose(); _readback = null;
        var reader = new SegmentReader(_directory, _repository, _epoch, _newFrames.Keys.Last(), _workspace, crypto: _crypto);
        try
        {
            foreach (var pair in _newFrames)
            {
                Format.Require(reader.Read(pair.Key).AsSpan().SequenceEqual(pair.Value), "appended metadata readback");
                switch (pair.Key.Kind)
                {
                    case FrameKind.Page: _ = MetadataCodec.DecodePage(pair.Value, pair.Key.Serial); break;
                    case FrameKind.Semantic:
                        var semantic = MetadataCodec.DecodeSemantic(pair.Value);
                        Format.Require(semantic.Previous is null || semantic.Previous.Serial < pair.Key.Serial, "semantic predecessor order");
                        Format.Require(semantic.VersionAddress is null || semantic.VersionAddress.Serial < pair.Key.Serial, "version reference order");
                        break;
                    case FrameKind.Root: _ = MetadataCodec.DecodeRoot(pair.Value); break;
                    case FrameKind.Retired:
                        var retired = MetadataCodec.DecodeRetired(pair.Value);
                        Format.Require(retired.Previous is null || retired.Previous.Serial < pair.Key.Serial, "retired predecessor order");
                        break;
                    default: throw Format.Invalid("unexpected appended frame type");
                }
            }
            _readback = reader; // Bounded immutable readback reuse, never authority.
        }
        catch { reader.Dispose(); throw; }
        _newFrames.Clear(); _pendingBytes = 0;
    }
    public void Dispose()
    {
        try { if (_stream is not null) FlushBlock(); }
        finally { _readback?.Dispose(); _readback = null; _stream?.Dispose(); _stream = null; }
    }
}
