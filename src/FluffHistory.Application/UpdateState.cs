using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluffHistory.Application;

public sealed record UpdatePreferences([property: JsonRequired] int Schema, [property: JsonRequired] UpdateChannel Channel,
    [property: JsonRequired] UpdateFrequency Frequency)
{
    public static UpdatePreferences Default { get; } = new(1, UpdateChannel.Stable, UpdateFrequency.Weekly);
    public void Validate()
    {
        if (Schema != 1 || !Enum.IsDefined(Channel) || !Enum.IsDefined(Frequency)) throw new InvalidDataException("Invalid update preferences.");
    }
}
public sealed record UpdateDiskState([property: JsonRequired] int Schema,
    [property: JsonRequired] DateTimeOffset? LastAttemptUtc, [property: JsonRequired] DateTimeOffset? LastSuccessUtc,
    [property: JsonRequired] string? ETag, [property: JsonRequired] ImmutableArray<UpdateRelease> Releases,
    [property: JsonRequired] bool AutomaticPending)
{
    public static UpdateDiskState Empty { get; } = new(1, null, null, null, [], false);
    public void Validate()
    {
        if (Schema != 1 || LastAttemptUtc?.Offset != null && LastAttemptUtc.Value.Offset != TimeSpan.Zero ||
            LastSuccessUtc?.Offset != null && LastSuccessUtc.Value.Offset != TimeSpan.Zero ||
            ETag is not null && !GitHubReleaseSource.ValidETag(ETag) || Releases.IsDefault || Releases.Length > GitHubReleaseSource.MaximumReleases)
            throw new InvalidDataException("Invalid update state.");
        foreach (var release in Releases) (release ?? throw new InvalidDataException("Null update release.")).Validate();
    }
}
public sealed record UpdateStoreResult<T>(SettingsLoadState State, T Value);
public sealed record UpdateInstallResult([property: JsonRequired] int Schema, [property: JsonRequired] string ReleaseVersion,
    [property: JsonRequired] DateTimeOffset CompletedUtc, [property: JsonRequired] int? InstallerExitCode,
    [property: JsonRequired] bool Installed, [property: JsonRequired] UpdateFailure Failure,
    [property: JsonRequired] bool RelaunchFailed)
{
    public void Validate()
    {
        if (Schema != 1 || !Application.ReleaseVersion.TryParse(ReleaseVersion, out _) || CompletedUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(Failure))
            throw new InvalidDataException("Invalid update result.");
    }
}

/// <summary>Single-owner bounded atomic JSON. Never reads or writes repository/package settings.</summary>
public sealed class UpdateJsonStore<T>(string filePath, T defaults, Action<T> validate, int maximumBytes = 256 * 1024)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter<UpdateChannel>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<UpdateFrequency>(JsonNamingPolicy.CamelCase, false) }
    };
    public async Task<UpdateStoreResult<T>> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    CheckPath(FilePath); token.ThrowIfCancellationRequested();
                    using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > maximumBytes) throw new InvalidDataException("Update state exceeds bound.");
                    var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                    using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 12 }); GitHubReleaseSource.Unique(doc.RootElement);
                    var value = JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Missing update state.");
                    validate(value); return new UpdateStoreResult<T>(SettingsLoadState.Loaded, value);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { return new(SettingsLoadState.Missing, defaults); }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or FormatException)
                { return new(SettingsLoadState.Corrupt, defaults); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { return new(SettingsLoadState.Unavailable, defaults); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(T value, CancellationToken token = default)
    {
        validate(value); var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Update state exceeds bound.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                CheckPath(FilePath); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); CheckPath(FilePath);
                var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(bytes); output.Flush(true); }
                    token.ThrowIfCancellationRequested(); CheckPath(FilePath);
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    internal static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update paths cannot be reparse points."); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }
}

public static class UpdateSchedule
{
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(6);
    public static TimeSpan Interval(UpdateFrequency frequency) => frequency switch
    { UpdateFrequency.Daily => TimeSpan.FromDays(1), UpdateFrequency.Weekly => TimeSpan.FromDays(7),
        UpdateFrequency.Monthly => TimeSpan.FromDays(30), UpdateFrequency.Never => TimeSpan.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)) };
    public static bool IsDue(UpdatePreferences preferences, UpdateDiskState state, DateTimeOffset now)
    {
        if (preferences.Frequency == UpdateFrequency.Never) return false;
        // Normalize future observations separately before evaluating; callers persist that one reanchor.
        if (state.LastAttemptUtc > now || state.LastSuccessUtc > now) return false;
        if (state.LastAttemptUtc is { } attempt && state.LastSuccessUtc != attempt && now - attempt < FailureBackoff) return false;
        return state.LastSuccessUtc is not { } success || now - success >= Interval(preferences.Frequency);
    }
    public static UpdateDiskState ReanchorFuture(UpdateDiskState state, DateTimeOffset now) =>
        state.LastAttemptUtc > now || state.LastSuccessUtc > now ? state with
        { LastAttemptUtc = now, LastSuccessUtc = state.LastSuccessUtc is null ? null : now } : state;
}
