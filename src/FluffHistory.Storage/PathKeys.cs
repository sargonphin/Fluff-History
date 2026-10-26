using System.Buffers.Binary;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal static class PathKeys
{
    internal static readonly IComparer<byte[]> Comparer = System.Collections.Generic.Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));
    internal static byte[] Source(Guid id) => id.ToByteArray(bigEndian: true);
    internal static byte[] Current(HistoryPath path)
    {
        var name = Format.Utf16.GetBytes(path.CanonicalPath);
        var key = new byte[18 + name.Length];
        path.SourceRootId.TryWriteBytes(key, bigEndian: true, out _);
        name.CopyTo(key, 16); return key;
    }
    internal static byte[] Version(VersionIdentity version)
    {
        Format.Require(version.Sequence > 0 && version.Ordinal >= 0 && version.Ordinal < MutationBatch.MaximumChanges, "version ordering");
        var path = Current(version.Path); var result = new byte[path.Length + 12]; path.CopyTo(result, 0);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(path.Length), version.Sequence);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(path.Length + 8), version.Ordinal); return result;
    }
    internal static HistoryPath DecodePath(ReadOnlySpan<byte> key)
    {
        Format.Require(key.Length >= 20 && key.Length <= Format.MaximumKeyBytes - 12 && (key.Length & 1) == 0 && key[^1] == 0 && key[^2] == 0, "logical key length/terminator");
        var name = Format.Utf16.GetString(key[16..^2]);
        var path = new HistoryPath(new Guid(key[..16], bigEndian: true), name);
        Format.Require(path.CanonicalPath == name, "noncanonical case key"); return path;
    }
    internal static VersionIdentity DecodeVersion(byte[] key, Guid id)
    {
        Format.Require(key.Length <= Format.MaximumKeyBytes && id != Guid.Empty, "version identity/key");
        Validate(key, IndexKind.Versions);
        return new(DecodePath(key.AsSpan(0, key.Length - 12)),
            BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(key.Length - 12)),
            BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(key.Length - 4)), id);
    }
    internal static void Validate(byte[] key, IndexKind kind)
    {
        if (kind == IndexKind.Sources) { Format.Require(key.Length == 16 && new Guid(key, true) != Guid.Empty, "source key"); return; }
        if (kind == IndexKind.Current) { _ = DecodePath(key); return; }
        Format.Require(kind == IndexKind.Versions && key.Length >= 32, "index key kind/length");
        _ = DecodePath(key.AsSpan(0, key.Length - 12));
        Format.Require(BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(key.Length - 12)) > 0 &&
            BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(key.Length - 4)) is >= 0 and < MutationBatch.MaximumChanges, "version key suffix");
    }
    internal static byte[] UpperBound(byte[] prefix)
        => ScopeUpperBound(prefix) ?? throw Format.Invalid("unbounded key prefix");

    // A root prefix consisting entirely of FF is the final range, whose end is unbounded.
    internal static byte[]? ScopeUpperBound(byte[] prefix)
    {
        var result = prefix.ToArray();
        for (var i = result.Length - 1; i >= 0; i--) if (result[i] != 255) { result[i]++; return result[..(i + 1)]; }
        return null;
    }
    internal static byte[] Scope(Guid root, string folder)
    {
        if (folder.Length == 0) return Source(root);
        var path = new HistoryPath(root, folder); var key = Current(path);
        var result = new byte[key.Length]; key.AsSpan(0, key.Length - 2).CopyTo(result);
        result[^2] = 0; result[^1] = (byte)'/'; return result;
    }
}
