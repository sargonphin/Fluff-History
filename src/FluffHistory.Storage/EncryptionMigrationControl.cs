using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed record EncryptionMigrationInfo(bool SourceEncrypted, bool TargetEncrypted, string Phase);
public sealed class EncryptionMigrationRequiredException(EncryptionMigrationInfo? information, Exception? inner = null)
    : IOException("Backup encryption conversion must be completed before ordinary access.", inner)
{ public EncryptionMigrationInfo? Information { get; } = information; }
public sealed class EncryptionMigrationSpaceException(long requiredFree)
    : IOException("More free space is required to continue encryption conversion.")
{ public long RequiredFree { get; } = requiredFree; }

internal sealed record EncryptionMigrationControl(string Product, int Schema, Guid OperationId, Guid RepositoryId,
    bool SourceEncrypted, bool TargetEncrypted, Guid TargetEpoch, int MaximumSegments, string SourceBootstrap, string TargetBootstrap,
    string SourceState, string? TargetState, string SemanticDigest, string Phase, string Sha256,
    long VersionCount, long LogicalBytes, long SourcePhysicalBytes, long TargetPhysicalBytes, string? CompletionProof = null)
{
    internal const string FileName = "encryption-migration.json";
    internal const int MaximumBytes = 65_536;
    internal const int MaximumTargetSegments = 1024;
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal EncryptionMigrationInfo Information => new(SourceEncrypted, TargetEncrypted, Phase);
    private byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    internal byte[] Bytes() => (this with { Sha256 = Format.Hash((this with { Sha256 = "" }).Encode()) }).Encode();
    internal byte[] FenceBytes() => (RepositoryBootstrap.Plain(RepositoryBootstrap.Parse(Convert.FromBase64String(SourceBootstrap)).Identity)
        with { EncryptionMode = "maintenance-" + OperationId.ToString("N") }).Bytes();
    internal byte[] CompletionBinding() => SHA256.HashData((this with { Phase = "Ready", Sha256 = "", CompletionProof = null }).Encode());
    internal byte[] CompletionContext() => System.Text.Encoding.ASCII.GetBytes("FluffHistory/v4/m25.2/conversion-commit/" + OperationId.ToString("N"));
    internal static bool Exists(string directory)
    {
        try { _ = File.GetAttributes(Path.Combine(directory, FileName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal static void RefuseOrdinaryOpen(string directory)
    {
        if (!Exists(directory)) return;
        EncryptionMigrationInfo? info;
        try { info = Read(directory).Information; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        { throw new EncryptionMigrationRequiredException(null, error); }
        throw new EncryptionMigrationRequiredException(info);
    }
    internal static EncryptionMigrationControl Read(string directory)
    {
        var path = Path.Combine(directory, FileName); RepositoryLayout.RequireFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Format.Require(stream.Length is > 0 and <= MaximumBytes, "encryption migration control bound");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        try
        {
            var control = JsonSerializer.Deserialize<EncryptionMigrationControl>(bytes, Options) ?? throw Format.Invalid("null migration control");
            Format.Require(control.Bytes().AsSpan().SequenceEqual(bytes) && control.Product == "Fluff-History" && control.Schema == 1 &&
                control.OperationId != Guid.Empty && control.RepositoryId != Guid.Empty && control.TargetEpoch != Guid.Empty &&
                control.SourceEncrypted != control.TargetEncrypted && control.MaximumSegments is > 0 and <= MaximumTargetSegments && control.SemanticDigest.Length == 64 &&
                control.VersionCount >= 0 && control.LogicalBytes >= 0 && control.SourcePhysicalBytes >= 0 && control.TargetPhysicalBytes >= 0 &&
                control.Phase is "Preparing" or "Payloads" or "Metadata" or "Pivot" or "Ready", "encryption migration control identity/schema");
            foreach (var text in new[] { control.SourceBootstrap, control.TargetBootstrap, control.SourceState, control.TargetState })
                if (text is not null) Format.Require(Convert.FromBase64String(text).Length is > 0 and <= AuthorityFiles.MaximumControlBytes, "migration embedded control bound");
            var source = RepositoryBootstrap.Parse(Convert.FromBase64String(control.SourceBootstrap));
            var target = RepositoryBootstrap.Parse(Convert.FromBase64String(control.TargetBootstrap));
            Format.Require(control.Phase == "Ready" ? control.CompletionProof is not null && Convert.FromBase64String(control.CompletionProof).Length == 96
                : control.CompletionProof is null, "migration completion proof phase/bound");
            Format.Require(source.Identity == target.Identity && source.RepositoryId == control.RepositoryId && source.Encrypted == control.SourceEncrypted &&
                target.Encrypted == control.TargetEncrypted && (control.TargetState is not null || control.Phase is "Preparing" or "Payloads" or "Metadata"), "migration bootstrap binding");
            return control;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Invalid encryption migration control.", error); }
    }
    internal void Install(string directory, bool initial, Action? beforeReplace = null)
    {
        var name = "encryption-control-" + Guid.NewGuid().ToString("N") + ".tmp";
        var path = Path.Combine(directory, name); var bytes = Bytes();
        Format.Require(bytes.Length <= MaximumBytes, "encryption migration control bound");
        bool created = false;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { created = true; stream.Write(bytes); stream.Flush(true); }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { var actual = new byte[bytes.Length]; stream.ReadExactly(actual); Format.Require(stream.Length == bytes.Length && actual.AsSpan().SequenceEqual(bytes), "migration candidate readback"); }
            beforeReplace?.Invoke();
            try
            {
                if (initial) File.Move(path, Path.Combine(directory, FileName));
                else AuthorityFiles.Replace(directory, name, FileName);
            }
            catch
            {
                if (!Exists(directory) || !Read(directory).Bytes().AsSpan().SequenceEqual(bytes)) throw;
            }
            Format.Require(Read(directory).Bytes().AsSpan().SequenceEqual(bytes), "migration installed readback");
        }
        finally
        {
            if (created)
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }
    internal Guid SegmentId(long ordinal)
    {
        Format.Require(ordinal > 0 && ordinal <= MaximumSegments, "migration segment count bound");
        var bytes = System.Text.Encoding.ASCII.GetBytes($"FluffHistory/v4/m25.2/conversion-segment/{OperationId:N}/{TargetEpoch:N}/{ordinal}");
        return new Guid(SHA256.HashData(bytes).AsSpan(0, 16), bigEndian: true);
    }
}
