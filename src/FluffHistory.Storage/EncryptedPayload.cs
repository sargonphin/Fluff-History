using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FluffHistory.Storage;

// A fresh salted object key per immutable payload instance, nonce zero for header,
// counter 1.. for ordered data/final records. No resume/rewrite under an existing key.
internal static class EncryptedPayload
{
    internal const int ChunkBytes = 256 * 1024, HeaderBytes = 96;
    internal static long PhysicalLength(long logical)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logical);
        if (logical > (uint.MaxValue - 2L) * ChunkBytes) throw new ArgumentOutOfRangeException(nameof(logical), "Payload exceeds the authenticated chunk-count bound.");
        checked { return HeaderBytes + 48 + logical + (logical / ChunkBytes + (logical % ChunkBytes == 0 ? 0 : 1)) * 32; }
    }
    private static byte[] Header(Repository repository, Guid id)
    {
        var h = new byte[80]; "FHPAY4\0\0"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 1); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), ChunkBytes);
        repository.Identity.RepositoryId.TryWriteBytes(h.AsSpan(16, 16), bigEndian: true, out _);
        id.TryWriteBytes(h.AsSpan(32, 16), bigEndian: true, out _); RandomNumberGenerator.Fill(h.AsSpan(48)); return h;
    }
    private static AesGcm Cipher(Repository repository, byte[] header)
    {
        var key = repository.Crypto!.Derive("payload", header.AsSpan(48, 32), header.AsSpan(0, 48));
        try { return new(key, 16); } finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static byte[] Nonce(ulong counter)
    { var nonce = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter); return nonce; }
    private static byte[] Aad(byte[] header, byte[] record) => header.Concat(record).ToArray();

    internal sealed class Writer : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _header, _buffer = new byte[ChunkBytes];
        private readonly AesGcm _aes;
        private int _buffered;
        private ulong _counter = 1;
        private long _length;
        private bool _complete;
        internal Writer(Repository repository, Guid id, Stream output)
        {
            _output = output; _header = Header(repository, id); _aes = Cipher(repository, _header);
            try
            {
                var tag = new byte[16]; _aes.Encrypt(Nonce(0), ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, _header); output.Write(_header); output.Write(tag);
            }
            catch { _aes.Dispose(); throw; }
        }
        private void Record(ReadOnlySpan<byte> plain, bool final)
        {
            Format.Require(_counter < uint.MaxValue, "payload chunk counter limit");
            var record = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(record, _counter);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), plain.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), final ? 1 : 0);
            var encrypted = new byte[plain.Length]; var tag = new byte[16];
            _aes.Encrypt(Nonce(_counter++), plain, encrypted, tag, Aad(_header, record));
            _output.Write(record); _output.Write(encrypted); _output.Write(tag);
        }
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (_complete) throw new InvalidOperationException("Encrypted payload is complete.");
            while (!bytes.IsEmpty)
            {
                var take = Math.Min(bytes.Length, ChunkBytes - _buffered); bytes[..take].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take; _length = checked(_length + take); bytes = bytes[take..];
                if (_buffered == ChunkBytes) { Record(_buffer, false); CryptographicOperations.ZeroMemory(_buffer); _buffered = 0; }
            }
        }
        internal void Complete(long expected)
        {
            Format.Require(!_complete && _length == expected, "encrypted payload logical length");
            if (_buffered != 0) Record(_buffer.AsSpan(0, _buffered), false);
            var final = new byte[16]; BinaryPrimitives.WriteInt64LittleEndian(final, _length);
            BinaryPrimitives.WriteUInt64LittleEndian(final.AsSpan(8), _counter - 1); Record(final, true);
            _complete = true; CryptographicOperations.ZeroMemory(_buffer);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_complete;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => _output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing) { if (disposing) { _aes.Dispose(); CryptographicOperations.ZeroMemory(_buffer); } base.Dispose(disposing); }
    }

    internal sealed class Reader : Stream
    {
        private readonly Stream _input;
        private readonly byte[] _header = new byte[80], _buffer = new byte[ChunkBytes];
        private readonly AesGcm _aes;
        private readonly long _expected;
        private long _decoded, _position;
        private int _offset, _available;
        private ulong _counter = 1;
        private bool _final;
        internal Reader(Repository repository, Guid id, Stream input, long expected, bool ownsInput = true)
        {
            _input = input; _expected = expected; OwnsInput = ownsInput;
            Format.Require(input.Length == PhysicalLength(expected), "encrypted payload physical length");
            input.ReadExactly(_header); var tag = new byte[16]; input.ReadExactly(tag);
            Format.Require(_header.AsSpan(0, 8).SequenceEqual("FHPAY4\0\0"u8) &&
                BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(8)) == 1 && BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(12)) == ChunkBytes &&
                new Guid(_header.AsSpan(16, 16), bigEndian: true) == repository.Identity.RepositoryId &&
                new Guid(_header.AsSpan(32, 16), bigEndian: true) == id, "encrypted payload header/identity");
            _aes = Cipher(repository, _header);
            try { _aes.Decrypt(Nonce(0), ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, _header); }
            catch (CryptographicException error) { _aes.Dispose(); throw new InvalidDataException("Encrypted payload header authentication failed.", error); }
        }
        private bool OwnsInput { get; }
        private void Load()
        {
            var record = new byte[16]; _input.ReadExactly(record);
            var count = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8)); var flags = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12));
            var final = _decoded == _expected;
            Format.Require(_counter < uint.MaxValue && BinaryPrimitives.ReadUInt64LittleEndian(record) == _counter && flags == (final ? 1 : 0) &&
                count == (final ? 16 : Math.Min(ChunkBytes, _expected - _decoded)), "encrypted payload chunk order/length");
            var encrypted = new byte[count]; var tag = new byte[16]; _input.ReadExactly(encrypted); _input.ReadExactly(tag);
            try { _aes.Decrypt(Nonce(_counter), encrypted, tag, _buffer.AsSpan(0, count), Aad(_header, record)); }
            catch (CryptographicException error) { CryptographicOperations.ZeroMemory(_buffer); throw new InvalidDataException("Encrypted payload chunk authentication failed.", error); }
            if (final)
            {
                Format.Require(BinaryPrimitives.ReadInt64LittleEndian(_buffer) == _expected &&
                    BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(8)) == _counter - 1 && _input.ReadByte() == -1, "encrypted payload final/trailing bytes");
                _final = true; CryptographicOperations.ZeroMemory(_buffer);
            }
            else { _decoded += count; _offset = 0; _available = count; }
            _counter++;
        }
        public override int Read(Span<byte> bytes)
        {
            if (bytes.IsEmpty || _final) return 0;
            if (_available == 0) { Load(); if (_final) return 0; }
            var take = Math.Min(bytes.Length, _available); _buffer.AsSpan(_offset, take).CopyTo(bytes);
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(_offset, take)); _offset += take; _available -= take; _position += take; return take;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _expected;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) { _aes.Dispose(); CryptographicOperations.ZeroMemory(_buffer); if (OwnsInput) _input.Dispose(); } base.Dispose(disposing); }
    }
}
