using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluffHistory.Core;

namespace FluffHistory.Storage;

internal static class AuthorityFiles
{
    internal const int MaximumControlBytes = 8192;
    private sealed record State(int FormatVersion, Guid RepositoryId, Guid Epoch, long Generation, long MutationSequence,
        Guid TransactionId, FrameAddress Root, string Sha256);
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static byte[] HeaderBytes(RepositoryIdentity identity) => RepositoryBootstrap.Plain(identity).Bytes();
    internal static RepositoryIdentity ReadHeader(string directory)
    {
        return RepositoryBootstrap.Read(directory).Identity;
    }
    internal static byte[] StateBytes(Authority authority, RepositoryCryptography? crypto = null)
    {
        var r = authority.Root;
        var state = new State(RepositoryIdentity.FormatVersion, authority.Repository.RepositoryId, r.Epoch, r.Generation, r.Sequence, r.TransactionId, authority.Address, "");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state with { Sha256 = Format.Hash(JsonSerializer.SerializeToUtf8Bytes(state, Options)) }, Options);
        return crypto?.Protect(bytes, "state") ?? bytes;
    }
    internal static Authority Read(string directory, RepositoryIdentity repository, string name = "state.json", RepositoryCryptography? crypto = null)
        => ReadBytes(directory, repository, ReadSmall(Path.Combine(directory, name)), crypto);
    internal static Authority ReadBytes(string directory, RepositoryIdentity repository, byte[] bytes, RepositoryCryptography? crypto = null)
    {
        var s = Decode<State>(crypto?.Unprotect(bytes, "state") ?? bytes);
        Format.Require(s.FormatVersion == RepositoryIdentity.FormatVersion && s.RepositoryId == repository.RepositoryId && s.Root is not null && s.Epoch != Guid.Empty, "state identity");
        Format.Require(s.Sha256 == Format.Hash(JsonSerializer.SerializeToUtf8Bytes(s with { Sha256 = "" }, Options)), "state checksum");
        s.Root!.Validate(); MetadataCodec.RequireKind(s.Root, FrameKind.Root);
        using var reader = new SegmentReader(directory, repository, s.Epoch, s.Root, crypto: crypto);
        var root = MetadataCodec.DecodeRoot(reader.Read(s.Root));
        Format.Require(root.Generation == s.Generation && root.Sequence == s.MutationSequence && root.TransactionId == s.TransactionId && root.Epoch == s.Epoch, "state/root agreement");
        foreach (var p in new[] { root.Journal, root.Current, root.Versions, root.Sources, root.Retired })
            if (p is not null) reader.CheckAddress(p);
        return new(repository, root, s.Root);
    }
    // Authenticate a retained completed-state identity without requiring its retired metadata files.
    internal static AuthorityIdentity RetainedIdentity(byte[] bytes, RepositoryIdentity repository, RepositoryCryptography? crypto)
    {
        var state = Decode<State>(crypto?.Unprotect(bytes, "state") ?? bytes);
        Format.Require(state.RepositoryId == repository.RepositoryId && state.FormatVersion == RepositoryIdentity.FormatVersion &&
            state.Sha256 == Format.Hash(JsonSerializer.SerializeToUtf8Bytes(state with { Sha256 = "" }, Options)), "retained state identity/checksum");
        return new(state.RepositoryId, state.Generation, state.MutationSequence, state.TransactionId);
    }
    // A temporary candidate may precede final metadata installation. Its small companion
    // holds only the root body; the unchanged state/frame hashes bind that body exactly.
    internal static Authority ReadPrepared(string directory, RepositoryIdentity repository, string candidate, string control, RepositoryCryptography? crypto = null)
    {
        var state = Decode<State>(ReadControl(Path.Combine(directory, candidate), crypto, "state"));
        Format.Require(state.FormatVersion == RepositoryIdentity.FormatVersion && state.RepositoryId == repository.RepositoryId && state.Root is not null && state.Epoch != Guid.Empty,
            "state identity");
        Format.Require(state.Sha256 == Format.Hash(JsonSerializer.SerializeToUtf8Bytes(state with { Sha256 = "" }, Options)), "state checksum");
        state.Root!.Validate(); MetadataCodec.RequireKind(state.Root, FrameKind.Root);
        var body = ReadControl(Path.Combine(directory, control), crypto, "prepared-root"); var root = MetadataCodec.DecodeRoot(body);
        var header = new SegmentHeader(repository.RepositoryId, state.Root.SegmentId, state.Epoch, state.Root.SegmentOrdinal, Guid.Empty, 0);
        Format.Require(MetadataCodec.EncodeFrame(header, state.Root.Offset, state.Root.Serial, FrameKind.Root, body, state.Root.BlockOffset).Address == state.Root,
            "prepared root binding");
        Format.Require(root.Generation == state.Generation && root.Sequence == state.MutationSequence &&
            root.TransactionId == state.TransactionId && root.Epoch == state.Epoch, "state/root agreement");
        using var reader = new SegmentReader(directory, repository, state.Epoch, state.Root, crypto: crypto);
        foreach (var pointer in new[] { root.Journal, root.Current, root.Versions, root.Sources, root.Retired })
            if (pointer is not null) reader.CheckAddress(pointer);
        return new(repository, root, state.Root);
    }
    internal static Guid CurrentEpoch(string directory, Guid repositoryId, RepositoryCryptography? crypto = null)
    {
        var state = Decode<State>(ReadControl(Path.Combine(directory, "state.json"), crypto, "state"));
        Format.Require(state.RepositoryId == repositoryId && state.FormatVersion == RepositoryIdentity.FormatVersion && state.Epoch != Guid.Empty &&
            state.Sha256 == Format.Hash(JsonSerializer.SerializeToUtf8Bytes(state with { Sha256 = "" }, Options)), "state identity/checksum");
        return state.Epoch;
    }
    private static T Decode<T>(byte[] bytes) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Format.Invalid("null JSON");
            Format.Require(JsonSerializer.SerializeToUtf8Bytes(value, Options).AsSpan().SequenceEqual(bytes), "noncanonical/duplicate/missing JSON field");
            return value;
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid v4 JSON.", e); }
    }
    private static byte[] ReadControl(string path, RepositoryCryptography? crypto, string purpose)
    { var bytes = ReadSmall(path); return crypto?.Unprotect(bytes, purpose) ?? bytes; }
    internal static byte[] ReadSmall(string path)
    {
        RepositoryLayout.RequireFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Format.Require(stream.Length is > 0 and <= MaximumControlBytes, "small metadata length");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    internal static void WriteNew(string path, byte[] bytes)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { stream.Write(bytes); stream.Flush(true); }
        Format.Require(ReadSmall(path).AsSpan().SequenceEqual(bytes), "metadata readback");
    }
    internal static void Replace(string directory, string candidate, string destinationName = "state.json")
    {
        var destination = Path.Combine(directory, destinationName); var source = Path.Combine(directory, candidate);
        RepositoryLayout.RequireFile(destination); RepositoryLayout.RequireFile(source);
        if (OperatingSystem.IsWindows())
        {
            // ReplaceFileW exposes an absent destination on the validated Samba server.
            // Same-directory rename has no copy/delete fallback and still reconciles
            // exact intended/predecessor authority on any denied or uncertain result.
            if (WindowsFileSystem.IsNetwork(directory))
            {
                if (!MoveFileExW(WindowsFileSystem.Native(source), WindowsFileSystem.Native(destination), 1))
                    throw new IOException("Network authority replacement rename failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            else if (!ReplaceFileW(WindowsFileSystem.Native(destination), WindowsFileSystem.Native(source), IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero))
                throw new IOException("ReplaceFileW failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        else File.Move(source, destination, true);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFileW(string replaced, string replacement, IntPtr backup, uint flags, IntPtr exclude, IntPtr reserved);
}
