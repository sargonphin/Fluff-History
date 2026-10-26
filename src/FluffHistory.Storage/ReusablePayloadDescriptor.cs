using FluffHistory.Core;

namespace FluffHistory.Storage;

internal enum ReusablePayloadState : byte { Ready = 1, TransactionBound = 2 }

// Optional cache codec, deliberately independent of repository/semantic schemas.
internal sealed record ReusablePayloadDescriptor(Guid RepositoryId, HistoryPath Path, Guid PayloadId,
    Guid StagingOwner, long Length, DateTimeOffset LastWriteUtc, XxHash64Checksum Checksum,
    AuthorityIdentity Origin, string OriginRootHash, ReusablePayloadState State, Guid TransactionId)
{
    internal const int ReusablePayloadDescriptorVersion = 1;
    internal const int MaximumBytes = 9000;
    internal static byte[] Encode(ReusablePayloadDescriptor d)
    {
        Validate(d);
        using var w = new BufferWriter();
        w.Bytes("FHREUSE\0"u8); w.I32(ReusablePayloadDescriptorVersion);
        w.Guid(d.RepositoryId); w.Guid(d.Path.SourceRootId); w.Text(d.Path.CanonicalPath);
        w.Guid(d.PayloadId); w.Guid(d.StagingOwner); w.I64(d.Length); w.I64(d.LastWriteUtc.Ticks);
        w.Bytes(Convert.FromHexString(d.Checksum.Value));
        w.I64(d.Origin.Generation); w.I64(d.Origin.MutationSequence); w.Guid(d.Origin.TransactionId);
        w.Bytes(Convert.FromHexString(d.OriginRootHash)); w.Byte((byte)d.State); w.Guid(d.TransactionId);
        var body = w.Finish(); w.Bytes(Convert.FromHexString(Format.Hash(body))); return w.Finish();
    }
    internal static ReusablePayloadDescriptor Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            Format.Require(bytes.Length is >= 218 and <= MaximumBytes, "optional reuse descriptor size");
            var body = bytes[..^32];
            Format.Require(Format.Hash(body) == Convert.ToHexString(bytes[^32..]), "optional reuse descriptor digest");
            var r = new BufferReader(body);
            Format.Require(r.Take(8).SequenceEqual("FHREUSE\0"u8) && r.I32() == ReusablePayloadDescriptorVersion, "optional reuse descriptor schema");
            var repository = r.Guid(); var root = r.Guid(); var name = r.Text(4 * HistoryPath.MaximumPathLength);
            var path = new HistoryPath(root, name); Format.Require(path.CanonicalPath == name, "optional reuse canonical path");
            var payload = r.Guid(); var owner = r.Guid(); var length = r.I64(); var time = new DateTimeOffset(r.I64(), TimeSpan.Zero);
            var checksum = new XxHash64Checksum(Convert.ToHexString(r.Take(8)));
            var origin = new AuthorityIdentity(repository, r.I64(), r.I64(), r.Guid());
            var d = new ReusablePayloadDescriptor(repository, path, payload, owner, length, time, checksum, origin,
                Convert.ToHexString(r.Take(32)), (ReusablePayloadState)r.Byte(), r.Guid());
            r.End(); Validate(d); return d;
        }
        catch (ArgumentException error) { throw new InvalidDataException("Bad optional reusable payload descriptor.", error); }
    }
    private static void Validate(ReusablePayloadDescriptor d)
    {
        Format.Require(d.RepositoryId != Guid.Empty && d.PayloadId != Guid.Empty && d.StagingOwner != Guid.Empty && d.Length >= 0 &&
            d.LastWriteUtc != default && d.LastWriteUtc.Offset == TimeSpan.Zero && d.Origin.RepositoryId == d.RepositoryId &&
            d.Origin.Generation >= 0 && d.Origin.MutationSequence >= 0 && d.Origin.MutationSequence <= d.Origin.Generation &&
            d.Origin.TransactionId != Guid.Empty && Format.ValidHash(d.OriginRootHash) &&
            (d.State == ReusablePayloadState.Ready && d.TransactionId == Guid.Empty ||
             d.State == ReusablePayloadState.TransactionBound && d.TransactionId != Guid.Empty), "optional reuse descriptor fields");
    }
}
