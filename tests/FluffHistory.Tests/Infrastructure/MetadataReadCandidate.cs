using System.Security.Cryptography;
using System.Buffers.Binary;
using FluffHistory.Core;
using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests.Infrastructure;

// Losing architecture candidates remain diagnostic-only. Exact production codecs validate every frame.
internal sealed class MetadataReadCandidate : IDisposable
{
    private readonly Repository _repository;
    private readonly Authority _authority;
    private readonly string _mode;
    private readonly string _local;
    private readonly SegmentReader _guard;
    private readonly Dictionary<Guid, Handle> _handles = [];
    private readonly Queue<Guid> _order = [];
    private readonly HashSet<Guid> _copied = [];
    internal long ReadRequests, RequestedBytes, SegmentOpens, LocalBytes;
    private sealed class Handle(FileStream stream, SegmentHeader header)
    {
        internal readonly FileStream Stream = stream;
        internal readonly SegmentHeader Header = header;
        internal readonly long Length = stream.Length;
        internal byte[]? Window;
        internal long WindowOffset;
        internal int WindowLength;
    }
    private readonly Func<FrameAddress, byte[]>? _readOverride;
    internal MetadataReadCandidate(Repository repository, Authority authority, string mode, string local,
        Func<FrameAddress, byte[]>? readOverride = null)
    {
        _repository = repository; _authority = authority; _mode = mode; _local = local; Directory.CreateDirectory(local);
        _guard = new(repository.DirectoryPath, repository.Identity, authority.Root.Epoch, authority.Address);
        _readOverride = readOverride;
    }

    internal (long Count, string Digest) Current()
    {
        var guard = _guard;
        var index = new OrderedIndex(IndexKind.Current, Read);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        foreach (var window in index.Range(_authority.Root.Current).Chunk(16384))
        {
            var values = new CurrentFileComparison[window.Length];
            foreach (var request in window.Select((entry, position) => (entry, position))
                         .OrderBy(p => p.entry.Value.SegmentId).ThenBy(p => p.entry.Value.Offset))
            {
                var body = Read(request.entry.Value);
                values[request.position] = ComparisonVersionDecoder.Decode(body, request.entry.Key, request.entry.Value, guard, _authority.Root.Sequence);
            }
            foreach (var value in values) { digest.AppendData(Encode(value)); count++; }
        }
        return (count, Convert.ToHexString(digest.GetHashAndReset()));
    }
    internal (long Count, string Digest) Timeline()
    {
        var guard = _guard;
        var index = new OrderedIndex(IndexKind.Versions, Read);
        using var ticks = new TimelineTickCollector(1_048_576, 16384);
        foreach (var window in index.Range(_authority.Root.Versions).Chunk(16384))
        {
            foreach (var entry in window.OrderBy(e => e.Value.SegmentId).ThenBy(e => e.Value.Offset))
                ticks.Add(TimelineVersionDecoder.Decode(Read(entry.Value), entry.Key, entry.Value, guard, _authority.Root.Sequence).UtcTicks);
            ticks.EndWindow(_ => { });
        }
        ticks.Complete(_ => { }); var values = ticks.TakeResult();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); Span<byte> bytes = stackalloc byte[8];
        foreach (var value in values) { BinaryPrimitives.WriteInt64LittleEndian(bytes, value); digest.AppendData(bytes); }
        return (values.LongLength, Convert.ToHexString(digest.GetHashAndReset()));
    }
    internal static byte[] Encode(CurrentFileComparison value)
    {
        using var writer = new BufferWriter();
        writer.Guid(value.Identity.VersionId); writer.I64(value.Identity.Sequence); writer.I32(value.Identity.Ordinal);
        writer.Guid(value.Path.SourceRootId); writer.Text(value.Path.CanonicalPath); writer.I64(value.Length); writer.I64(value.LastWriteTimeUtc.Ticks);
        return writer.Finish();
    }
    private byte[] Read(FrameAddress address)
    {
        _guard.CheckAddress(address);
        if (_readOverride is not null) return _readOverride(address);
        var handle = Open(address); var size = checked(Format.FrameHeaderBytes + address.Length);
        Assert.True(address.End <= handle.Length);
        var bytes = new byte[size];
        var windowBytes = _mode switch { "coalesced-64KiB" => 64 * 1024, "coalesced-256KiB" => 256 * 1024, "coalesced-1MiB" => 1024 * 1024, _ => 0 };
        if (windowBytes != 0)
        {
            if (handle.Window is null || address.Offset < handle.WindowOffset || address.End > handle.WindowOffset + handle.WindowLength)
            {
                handle.Window ??= new byte[windowBytes];
                handle.WindowOffset = address.Offset;
                handle.WindowLength = (int)Math.Min(handle.Window.Length, handle.Length - address.Offset);
                if (size <= handle.WindowLength)
                { handle.Stream.Position = handle.WindowOffset; handle.Stream.ReadExactly(handle.Window.AsSpan(0, handle.WindowLength)); Requested(handle.WindowLength); }
                else handle.WindowLength = 0;
            }
            if (size <= handle.WindowLength && address.Offset >= handle.WindowOffset && address.End <= handle.WindowOffset + handle.WindowLength)
                handle.Window!.AsSpan((int)(address.Offset - handle.WindowOffset), size).CopyTo(bytes);
            else { handle.Stream.Position = address.Offset; handle.Stream.ReadExactly(bytes); Requested(bytes.Length); }
        }
        else
        {
            handle.Stream.Position = address.Offset; handle.Stream.ReadExactly(bytes);
            if (_mode != "touched-segment") Requested(bytes.Length);
        }
        return MetadataCodec.ValidateFrame(bytes, handle.Header, address);
    }
    private Handle Open(FrameAddress address)
    {
        if (_handles.TryGetValue(address.SegmentId, out var existing)) return existing;
        var source = Path.Combine(_repository.DirectoryPath, "metadata", address.SegmentId.ToString("N") + ".seg");
        if (_mode == "touched-segment")
        {
            var target = Path.Combine(_local, address.SegmentId.ToString("N") + ".seg");
            if (_copied.Add(address.SegmentId))
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var buffer = new byte[1024 * 1024]; int count;
                while ((count = input.Read(buffer)) != 0) { Requested(count); output.Write(buffer, 0, count); LocalBytes += count; }
                SegmentOpens++;
            }
            source = target;
        }
        var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
        try
        {
            var bytes = new byte[Format.SegmentHeaderBytes]; stream.ReadExactly(bytes);
            if (_mode != "touched-segment") { Requested(bytes.Length); SegmentOpens++; }
            var header = MetadataCodec.DecodeSegment(bytes);
            Assert.Equal(_repository.Identity.RepositoryId, header.RepositoryId); Assert.Equal(_authority.Root.Epoch, header.Epoch);
            Assert.Equal(address.SegmentId, header.SegmentId); Assert.Equal(address.SegmentOrdinal, header.Ordinal);
            if (address.SegmentOrdinal < _authority.Address.SegmentOrdinal)
            { SegmentReader.ValidateSeal(stream, header); if (_mode != "touched-segment") Requested(Format.SealBytes); }
            if (_handles.Count == 8) { var id = _order.Dequeue(); _handles[id].Stream.Dispose(); _handles.Remove(id); }
            var result = new Handle(stream, header); _handles.Add(address.SegmentId, result); _order.Enqueue(address.SegmentId); return result;
        }
        catch { stream.Dispose(); throw; }
    }
    private void Requested(int count) { ReadRequests++; RequestedBytes += count; }
    public void Dispose() { foreach (var handle in _handles.Values) handle.Stream.Dispose(); _handles.Clear(); _guard.Dispose(); }
}
