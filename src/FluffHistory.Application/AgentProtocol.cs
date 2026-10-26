using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluffHistory.Application;

public enum AgentOperation { GetState, WaitForState, RunNow, CancelActive, ApplySettings, Reinspect, ShutdownForUpdate,
    ReloadUpdatePreferences, CheckUpdates, InstallUpdate }
public enum AgentError { None, Busy, Conflict, InvalidRequest, Unavailable, ShuttingDown }
public sealed record AgentRequest(AgentOperation Operation, AppSettings? Settings = null, bool Reset = false,
    long SinceRevision = -1, Guid AgentId = default, long ConfigurationRevision = -1, bool ConfirmUpdateCancellation = false);
public sealed record AgentState(Guid AgentId, int ProcessId, DateTimeOffset StartedUtc, string ProductVersion,
    long Revision, long ConfigurationRevision, long TimelineRevision, AppSettings Settings, RepositoryInspection Inspection,
    bool ConfigurationNeedsReset, string ConfigurationProblem, BackupStatus? Backup, bool ShuttingDown,
    LastBackupStatus? LastBackupStatus = null, ConfigurationIssueCode? ConfigurationIssue = null, UpdateSnapshot? Updates = null);
public sealed record AgentReply(AgentError Error, AgentState State, BackupAdmission? Admission = null);

/// <summary>Fixed v1 operations, complete detached snapshots, bounded length framing. No generic RPC or secrets.</summary>
public static class AgentProtocol
{
    public const int Version = 2;
    public const int MaximumRequestBytes = 4 * 1024 * 1024 + 4096;
    // Settings <=4MiB, plus <=256 bounded source problems (including JSON escaping) and fixed status fields.
    public const int MaximumReplyBytes = 16 * 1024 * 1024;
    private const uint Magic = 0x31414846; // FHA1
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    public static void Validate(AgentRequest request)
    {
        if (!Enum.IsDefined(request.Operation) || request.SinceRevision < -1 ||
            request.ConfirmUpdateCancellation && request.Operation != AgentOperation.InstallUpdate ||
            request.Operation != AgentOperation.WaitForState && request.SinceRevision != -1 ||
            request.Operation != AgentOperation.ApplySettings &&
                (request.Settings is not null || request.Reset || request.AgentId != default || request.ConfigurationRevision != -1))
            throw new InvalidDataException("Invalid agent request.");
        if (request.Operation == AgentOperation.ApplySettings)
        {
            if (request.Settings is null || request.AgentId == default || request.ConfigurationRevision < 0) throw new InvalidDataException("Missing settings authority.");
            request.Settings.Validate();
            if (JsonSerializer.SerializeToUtf8Bytes(request.Settings, Options).Length > 4 * 1024 * 1024) throw new InvalidDataException("Settings too large.");
        }
    }
    public static async Task WriteAsync<T>(Stream stream, T value, bool reply, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (data.Length == 0 || data.Length > (reply ? MaximumReplyBytes : MaximumRequestBytes)) throw new InvalidDataException("Agent frame too large.");
        var header = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), Version); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, bool reply, CancellationToken token)
    {
        var header = new byte[12]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != Version ||
            count <= 0 || count > (reply ? MaximumReplyBytes : MaximumRequestBytes)) throw new InvalidDataException("Invalid agent frame.");
        var data = new byte[count]; await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(data, new() { MaxDepth = 24 });
        Unique(document.RootElement);
        var result = JsonSerializer.Deserialize<T>(data, Options) ?? throw new InvalidDataException("Missing agent message.");
        if (result is AgentRequest request) Validate(request);
        if (result is AgentReply response && (!Enum.IsDefined(response.Error) || response.State is null || response.State.AgentId == default || response.State.Revision < 0))
            throw new InvalidDataException("Invalid agent reply.");
        if (result is AgentReply issue && issue.State.ConfigurationIssue is { } code && !Enum.IsDefined(code))
            throw new InvalidDataException("Invalid configuration issue.");
        if (result is AgentReply updates) updates.State.Updates?.Validate();
        if (result is AgentReply valid && valid.State.Backup is { } backup)
        {
            if (backup.Progress is null) throw new InvalidDataException("Missing backup progress.");
            backup.Progress.Validate(); backup.LastAttempt?.Result.MetadataMaintenance?.Validate();
            if (backup.LastAttempt?.Result.Scan is { } scan && (scan.Problems.IsDefault || scan.Problems.Length > 256 || scan.Problems.Any(p => !Enum.IsDefined(p.Kind))))
                throw new InvalidDataException("Invalid source problem kind.");
        }
        if (result is AgentReply display && display.State.LastBackupStatus is { } saved)
        {
            saved.Validate();
            if (!saved.Matches(display.State.Settings.RepositoryPath, display.State.Inspection.RepositoryId))
                throw new InvalidDataException("Last backup display status binding mismatch.");
        }
        return result;
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate agent field."); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var element in value.EnumerateArray()) Unique(element);
    }
}
