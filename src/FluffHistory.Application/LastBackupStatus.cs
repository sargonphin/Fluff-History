using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluffHistory.Application;

/// <summary>One replaceable application display value. Never repository authority, run history,
/// scheduler input, a restore point or a source of logical Timeline revision.</summary>
public sealed record LastBackupStatus(int Schema, Guid RepositoryId, string LocationHash,
    DateTimeOffset FinishedUtc, long DurationTicks, BackupOutcome Outcome, long? VersionsSaved, long? FileProblems)
{
    [JsonIgnore] public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
    [JsonIgnore] public bool Successful => Outcome is BackupOutcome.Completed or BackupOutcome.CompletedWithSourceProblems;
    public void Validate()
    {
        if (Schema != 1 || RepositoryId == Guid.Empty || LocationHash is null || LocationHash.Length != 64 ||
            LocationHash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            FinishedUtc == default || FinishedUtc.Offset != TimeSpan.Zero || DurationTicks < 0 ||
            !Enum.IsDefined(Outcome) || Outcome == BackupOutcome.SafeguardDeferred || VersionsSaved < 0 || FileProblems < 0 ||
            (VersionsSaved is null) != (FileProblems is null) || !Successful && VersionsSaved is not null)
            throw new InvalidDataException("Invalid last backup display status.");
    }
    public bool Matches(string location, Guid? repositoryId) => repositoryId == RepositoryId && location.Length != 0 && LocationHash == HashLocation(location);
    internal static string HashLocation(string location)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
    internal static LastBackupStatus FromAttempt(string location, Guid repositoryId, AttemptSnapshot attempt)
    {
        var complete = (attempt.Result.Outcome is BackupOutcome.Completed or BackupOutcome.CompletedWithSourceProblems) &&
            attempt.Result.Scan is { Partial: false, Cancelled: false, Outcome: FluffHistory.Engine.ScanOutcome.Completed };
        var value = new LastBackupStatus(1, repositoryId, HashLocation(location), attempt.FinishedUtc.ToUniversalTime(),
            attempt.Duration.Ticks, attempt.Result.Outcome, complete ? attempt.Result.Scan!.VersionsCreated : null,
            complete ? attempt.Result.Scan!.SourceSkips : null);
        value.Validate(); return value;
    }
}

/// <summary>Bounded flat JSON, with one canonical file and one fixed transient replacement file.
/// Invalid/unavailable input is ignored; no quarantine, backup copies or status history.</summary>
public sealed class LastBackupStatusStore(string filePath)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);
    public const int MaximumBytes = 4096;
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 4 };
    public async Task<LastBackupStatus?> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested(); CheckOrdinary(FilePath);
                    if (!File.Exists(FilePath)) return null;
                    using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length is <= 0 or > MaximumBytes) return null;
                    var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
                    using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in document.RootElement.EnumerateObject())
                        if (!names.Add(property.Name)) return null;
                    var value = JsonSerializer.Deserialize<LastBackupStatus>(bytes, Options) ?? throw new InvalidDataException();
                    value.Validate(); return value;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { return null; }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(LastBackupStatus value, CancellationToken token = default)
    {
        value.Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Last backup status exceeds its byte bound.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); CheckOrdinary(FilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); CheckOrdinary(FilePath);
                var temporary = FilePath + ".tmp"; CheckOrdinary(temporary); var owned = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    { owned = true; stream.Write(bytes); stream.Flush(true); }
                    token.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, overwrite: true); owned = false;
                }
                finally { if (owned) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private static void CheckOrdinary(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Last status requires ordinary application storage.");
    }
}
