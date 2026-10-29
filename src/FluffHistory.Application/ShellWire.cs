using System.Text;

namespace FluffHistory.Application;

public abstract record ShellMessage;
public sealed record ShellOpenRequest(string Path, ShellItemKind Kind, string? Language = null, bool VersionTable = false) : ShellMessage;
public sealed record ShellRestoreRequest(string SessionId, string VersionToken, ulong OwnerWindow) : ShellMessage;
public sealed record ShellPreviewRequest(string SessionId, string VersionToken, ulong OwnerWindow) : ShellMessage;
public sealed record ShellCloseRequest : ShellMessage;
public sealed record ShellOpenReply(ShellHistorySummary Value) : ShellMessage;
public sealed record ShellTableReply(ShellHistorySummary Value) : ShellMessage;
public sealed record ShellResultReply(string Message) : ShellMessage;

/// <summary>Strict v1 little-endian framing shared by transient activation files and user-only pipes.
/// Strings are length-prefixed UTF-16LE, with no terminator; no optional/unknown fields within an operation.
/// Open4 adds an explicit bounded UI language; original Open1/replies remain byte-compatible.</summary>
public static class ShellWire
{
    public const uint Magic = 0x31485346, Version = 1;
    public const int HeaderBytes = 16, MaximumBodyBytes = 128 * 1024;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    public static bool IsToken(string? token) => token is { Length: 32 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static byte[] Encode(ShellMessage message)
    {
        using var body = new MemoryStream(); using var w = new BinaryWriter(body, Utf16, true);
        uint operation;
        switch (message)
        {
            case ShellOpenRequest open:
                operation = open.VersionTable ? 5u : open.Language is null ? 1u : 4u;
                if (!Enum.IsDefined(open.Kind) || open.VersionTable && open.Language is null) throw new InvalidDataException();
                _ = SourcePaths.Normalize(open.Path); w.Write((uint)open.Kind); Write(w, open.Path, 32760);
                if (open.Language is { } language) { ValidateLanguage(language); Write(w, language, 85); }
                break;
            case ShellRestoreRequest restore:
                operation = 2; RequireToken(restore.SessionId); RequireToken(restore.VersionToken);
                Write(w, restore.SessionId, 32); Write(w, restore.VersionToken, 32); w.Write(restore.OwnerWindow); break;
            case ShellPreviewRequest preview:
                operation = 6; RequireToken(preview.SessionId); RequireToken(preview.VersionToken);
                Write(w, preview.SessionId, 32); Write(w, preview.VersionToken, 32); w.Write(preview.OwnerWindow); break;
            case ShellCloseRequest: operation = 3; break;
            case ShellOpenReply reply:
                operation = 101; var value = reply.Value;
                if (!Enum.IsDefined(value.Status) || value.Rows.IsDefault || value.Rows.Length > ShellPathSession.MaximumRecent ||
                    value.Rows.Length > 0 && !IsToken(value.SessionId) || value.CanRestore && value.Rows.Length == 0) throw new InvalidDataException();
                w.Write((uint)value.Status); Write(w, value.SessionId, 32); Write(w, value.Summary, 4096); Write(w, value.BackupLocation, 32760);
                var legacyRows = value.Rows.Take(ShellPathSession.LegacyMaximumRecent).ToArray();
                w.Write(value.CanRestore ? 1u : 0u); w.Write((uint)legacyRows.Length);
                foreach (var row in legacyRows) { RequireToken(row.Token); Write(w, row.Token, 32); Write(w, row.Display, 1024); }
                break;
            case ShellTableReply table:
                operation = 103; var detail = table.Value;
                if (!Enum.IsDefined(detail.Status) || detail.Rows.IsDefault || detail.Rows.Length > ShellPathSession.MaximumRecent ||
                    detail.Rows.Length > 0 && !IsToken(detail.SessionId) || detail.CanRestore && detail.Rows.Length == 0 ||
                    detail.Rows.Select(row => row.Token).Distinct(StringComparer.Ordinal).Count() != detail.Rows.Length) throw new InvalidDataException();
                w.Write((uint)detail.Status); Write(w, detail.SessionId, 32); Write(w, detail.Summary, 4096); Write(w, detail.VersionCount, 1024);
                w.Write(detail.CanRestore ? 1u : 0u); w.Write((uint)detail.Rows.Length);
                foreach (var row in detail.Rows)
                {
                    RequireToken(row.Token); ValidateTicks(row.ModifiedUtcTicks);
                    Write(w, row.Token, 32); Write(w, row.Name, 1024); Write(w, row.Modified, 1024); w.Write(row.ModifiedUtcTicks);
                    w.Write((row.Latest ? 1u : 0u) | (row.Oldest ? 2u : 0u));
                }
                break;
            case ShellResultReply result: operation = 102; Write(w, result.Message, 4096); break;
            default: throw new InvalidDataException("Unknown Shell operation.");
        }
        if (body.Length > MaximumBodyBytes) throw new InvalidDataException("Shell frame exceeds its bound.");
        using var frame = new MemoryStream(); using var header = new BinaryWriter(frame);
        header.Write(Magic); header.Write(Version); header.Write(operation); header.Write((uint)body.Length); header.Write(body.ToArray()); return frame.ToArray();
    }
    public static ShellMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderBytes || frame.Length > HeaderBytes + MaximumBodyBytes) throw new InvalidDataException();
        using var stream = new MemoryStream(frame.ToArray(), false); using var r = new BinaryReader(stream, Utf16, true);
        if (r.ReadUInt32() != Magic || r.ReadUInt32() != Version) throw new InvalidDataException("Unsupported Shell protocol.");
        var operation = r.ReadUInt32(); var length = r.ReadUInt32();
        if (length != frame.Length - HeaderBytes) throw new InvalidDataException("Invalid Shell frame length.");
        ShellMessage message;
        try
        {
            message = operation switch { 1 => ReadOpen(r), 4 => ReadOpen(r, localized: true), 5 => ReadOpen(r, localized: true, table: true),
                2 => ReadRestore(r), 6 => ReadPreview(r), 3 => new ShellCloseRequest(), 101 => ReadReply(r), 103 => ReadTable(r),
                102 => new ShellResultReply(Read(r, 4096)), _ => throw new InvalidDataException("Unknown Shell operation.") };
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or ArgumentException) { throw new InvalidDataException("Malformed Shell message.", error); }
        if (stream.Position != stream.Length) throw new InvalidDataException("Unknown Shell fields.");
        return message;
    }
    public static async Task<ShellMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[HeaderBytes]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (length > MaximumBodyBytes) throw new InvalidDataException("Shell frame exceeds its bound.");
        var frame = new byte[HeaderBytes + (int)length]; header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(HeaderBytes), token).ConfigureAwait(false); return Decode(frame);
    }
    private static ShellOpenRequest ReadOpen(BinaryReader r, bool localized = false, bool table = false)
    {
        var kind = (ShellItemKind)r.ReadUInt32(); if (!Enum.IsDefined(kind)) throw new InvalidDataException();
        var path = Read(r, 32760); _ = SourcePaths.Normalize(path);
        var language = localized ? Read(r, 85) : null;
        if (language is not null) ValidateLanguage(language);
        return new(path, kind, language, table);
    }
    private static void ValidateLanguage(string language)
    {
        if (language.Length is < 1 or > 85 || !char.IsAsciiLetter(language[0]) || language.EndsWith('-') || language.Contains("--", StringComparison.Ordinal) ||
            language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid Shell UI language.");
    }
    private static ShellRestoreRequest ReadRestore(BinaryReader r)
    { var session = Read(r, 32); var token = Read(r, 32); RequireToken(session); RequireToken(token); return new(session, token, r.ReadUInt64()); }
    private static ShellPreviewRequest ReadPreview(BinaryReader r)
    { var session = Read(r, 32); var token = Read(r, 32); RequireToken(session); RequireToken(token); return new(session, token, r.ReadUInt64()); }
    private static ShellOpenReply ReadReply(BinaryReader r)
    {
        var status = (ShellHistoryStatus)r.ReadUInt32(); if (!Enum.IsDefined(status)) throw new InvalidDataException();
        var session = Read(r, 32); if (session.Length > 0) RequireToken(session);
        var summary = Read(r, 4096); var location = Read(r, 32760); var restore = r.ReadUInt32(); var count = r.ReadUInt32();
        if (restore > 1 || count > ShellPathSession.LegacyMaximumRecent || count > 0 && session.Length == 0 || restore == 1 && count == 0) throw new InvalidDataException();
        var rows = System.Collections.Immutable.ImmutableArray.CreateBuilder<ShellRecentVersion>();
        for (var i = 0; i < count; i++) { var key = Read(r, 32); RequireToken(key); rows.Add(new(key, Read(r, 1024))); }
        if (rows.Select(v => v.Token).Distinct(StringComparer.Ordinal).Count() != rows.Count) throw new InvalidDataException();
        return new(new(status, session, summary, location, rows.ToImmutable(), restore == 1));
    }
    private static ShellTableReply ReadTable(BinaryReader r)
    {
        var status = (ShellHistoryStatus)r.ReadUInt32(); if (!Enum.IsDefined(status)) throw new InvalidDataException();
        var session = Read(r, 32); if (session.Length > 0) RequireToken(session);
        var summary = Read(r, 4096); var title = Read(r, 1024); var restore = r.ReadUInt32(); var count = r.ReadUInt32();
        if (restore > 1 || count > ShellPathSession.MaximumRecent || count > 0 && session.Length == 0 || restore == 1 && count == 0) throw new InvalidDataException();
        var rows = System.Collections.Immutable.ImmutableArray.CreateBuilder<ShellRecentVersion>();
        for (var i = 0; i < count; i++)
        {
            var key = Read(r, 32); RequireToken(key); var name = Read(r, 1024); var modified = Read(r, 1024);
            var ticks = r.ReadInt64(); ValidateTicks(ticks); var flags = r.ReadUInt32(); if (flags > 3) throw new InvalidDataException();
            rows.Add(new(key, "", name, modified, ticks, (flags & 1) != 0, (flags & 2) != 0));
        }
        if (rows.Select(row => row.Token).Distinct(StringComparer.Ordinal).Count() != rows.Count) throw new InvalidDataException();
        return new(new(status, session, summary, "", rows.ToImmutable(), restore == 1, title));
    }
    private static void ValidateTicks(long value)
    { if (value < DateTime.MinValue.Ticks || value > DateTime.MaxValue.Ticks) throw new InvalidDataException(); }
    private static void Write(BinaryWriter w, string value, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value); if (value.Length > maximum || value.Contains('\0')) throw new InvalidDataException();
        var bytes = Utf16.GetBytes(value); w.Write((uint)value.Length); w.Write(bytes);
    }
    private static string Read(BinaryReader r, int maximum)
    {
        var length = r.ReadUInt32(); if (length > maximum || length * 2 > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException();
        var value = Utf16.GetString(r.ReadBytes((int)length * 2)); if (value.Contains('\0')) throw new InvalidDataException(); return value;
    }
    private static void RequireToken(string token) { if (!IsToken(token)) throw new InvalidDataException("Invalid Shell token."); }
}
