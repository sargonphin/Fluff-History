using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluffHistory.Core;
using FluffHistory.Engine;

namespace FluffHistory.Application;

public sealed record SourceMapping([property: JsonRequired] Guid SourceRootId, [property: JsonRequired] string OriginalPath,
    [property: JsonRequired] bool IsProtected, [property: JsonRequired] long MaximumFileSize,
    [property: JsonRequired] ImmutableArray<string> ExcludedDirectories, [property: JsonRequired] string? RuntimePath)
{
    public ImmutableArray<string> AutomaticExcludedDirectories { get; init; } = [];
    public bool ForceInclude { get; init; }
    public static SourceMapping FromConfiguration(SourceRootConfiguration configuration, string? runtimePath = null)
        => new(configuration.SourceRootId, configuration.OriginalPath, configuration.IsProtected, configuration.MaximumFileSize,
            configuration.ExcludedDirectories.ToImmutableArray(), runtimePath);
    public static SourceMapping New(string runtimePath) => FromConfiguration(new(Guid.NewGuid(), runtimePath), runtimePath);
    public SourceRootConfiguration ToConfiguration()
    {
        _ = new SourceRootConfiguration(SourceRootId, OriginalPath, IsProtected, MaximumFileSize, ExcludedDirectories);
        _ = new SourceRootConfiguration(SourceRootId, OriginalPath, IsProtected, MaximumFileSize, AutomaticExcludedDirectories);
        return new(SourceRootId, OriginalPath, IsProtected, MaximumFileSize,
            ExcludedDirectories.Concat(AutomaticExcludedDirectories).Select(p => new HistoryPath(SourceRootId, p).CanonicalPath).Distinct(StringComparer.Ordinal));
    }
    public SourceSelection? ToSelection() => RuntimePath is null ? null : new(ToConfiguration(), RuntimePath);
    public override string ToString() => $"{OriginalPath} → {RuntimePath ?? "unmapped (disabled)"}";
}

/// <summary>Desired configuration only. No passwords, cursors, leases or runtime attempt/status history.</summary>
public sealed record AppSettings(
    [property: JsonRequired] int Schema,
    [property: JsonRequired] string RepositoryPath,
    [property: JsonRequired] ImmutableArray<SourceMapping> Sources,
    [property: JsonRequired] int IntervalMinutes,
    [property: JsonRequired] DateTimeOffset AnchorUtc,
    [property: JsonRequired] bool SchedulingEnabled,
    [property: JsonRequired] SafeguardPolicy Safeguards)
{
    [JsonRequired] public string RetentionPolicyName { get; init; } = ProductConfiguration.DefaultRetentionPolicyName;
    public ImmutableArray<string> ExcludedFolders { get; init; } = [];
    [JsonIgnore] public RetentionPolicy RetentionPolicy => ProductConfiguration.Retention(RetentionPolicyName).Policy;
    public static AppSettings Empty => new(2, "", [], 60, DateTimeOffset.UtcNow, true, new());
    public void Validate()
    {
        if (Schema != 2 || Sources.IsDefault || Sources.Length > 1024 || !ProductConfiguration.Frequencies.Any(f => f.Minutes == IntervalMinutes) ||
            AnchorUtc == default || AnchorUtc.Offset != TimeSpan.Zero || Safeguards is null) throw new InvalidDataException("Invalid app configuration.");
        _ = RetentionPolicy;
        ValidatePath(RepositoryPath, allowEmpty: true);
        if (Sources.Any(s => s is null)) throw new InvalidDataException("Invalid source folder.");
        if (Sources.Select(s => s.SourceRootId).Distinct().Count() != Sources.Length) throw new InvalidDataException("Duplicate logical root.");
        foreach (var source in Sources)
        {
            if (source is null || source.ExcludedDirectories.IsDefault || source.AutomaticExcludedDirectories.IsDefault) throw new InvalidDataException("Invalid source folder.");
            _ = source.ToConfiguration();
            if (source.RuntimePath is not null) ValidatePath(source.RuntimePath);
        }
        var active = FolderExclusions.Apply(Sources, ExcludedFolders).Where(s => s.IsProtected && s.RuntimePath is not null).ToArray();
        if (active.Select(s => SourcePaths.Normalize(s.RuntimePath!)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Length) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.DuplicateFolder, "A folder can only be selected once.");
        foreach (var source in active)
        {
            if (RepositoryPath.Length == 0 || SourcePaths.Overlaps(source.RuntimePath!, RepositoryPath)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.SourceRepositoryOverlap, SourcePaths.OverlapMessage);
            foreach (var child in active.Where(c => c.SourceRootId != source.SourceRootId && SourcePaths.Within(c.RuntimePath!, source.RuntimePath!)))
            {
                var relative = new HistoryPath(source.SourceRootId, SourcePaths.Relative(source.RuntimePath!, child.RuntimePath!)).CanonicalPath;
                if (!source.ToConfiguration().ExcludedDirectories.Any(e => relative == e || relative.StartsWith(e + "/", StringComparison.Ordinal)))
                    throw ConfigurationIssues.Invalid(ConfigurationIssueCode.NestedFolder, "Nested selected folders must be excluded from their parent folder.");
            }
        }
    }
    private static void ValidatePath(string value, bool allowEmpty = false)
    {
        if (value is null || value.Length > 32760 || value.Any(char.IsControl) ||
            !(allowEmpty && value.Length == 0) && !Path.IsPathFullyQualified(value) && !(value.Length >= 3 && value[1] == ':' || value.StartsWith(@"\\", StringComparison.Ordinal))) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.AbsolutePathRequired, "An absolute configured path is required.");
        if (value.Length != 0) _ = SourcePaths.Normalize(value);
    }
    public BackupConfiguration ToBackupConfiguration(ISourceFolderPolicy? folderPolicy = null, string? workspace = null)
    {
        Validate();
        workspace ??= BackupWorkspace.DefaultRoot;
        return new(RepositoryPath, (folderPolicy ?? SourceFolderPolicy.Empty).Select(RepositoryPath, FolderExclusions.Apply(Sources, ExcludedFolders), workspace),
            new(WorkspaceRoot: workspace, Capacity: new(RetentionPolicy)), Safeguards, RetentionPolicy)
            { ForcedSourceIds = Sources.Where(s => s.ForceInclude).Select(s => s.SourceRootId).ToImmutableHashSet() };
    }
    public BackupSchedule ToSchedule() => new(TimeSpan.FromMinutes(IntervalMinutes), AnchorUtc);
}
public enum SettingsLoadState { Missing, Loaded, Corrupt, Unavailable }
public sealed record SettingsLoadResult(SettingsLoadState State, AppSettings? Settings);

/// <summary>Bounded strict application-local JSON. Atomic same-directory replacement, no repository writes.</summary>
public sealed class AppSettingsStore(string filePath)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested(); if (!File.Exists(FilePath)) return new SettingsLoadResult(SettingsLoadState.Missing, null);
                    using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > MaximumBytes) return new(SettingsLoadState.Corrupt, null);
                    var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
                    using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 }); RequireUnique(document.RootElement);
                    var settings = JsonSerializer.Deserialize<AppSettings>(bytes, Options) ?? throw new InvalidDataException();
                    settings.Validate(); return new(SettingsLoadState.Loaded, settings);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NullReferenceException)
                { return new(SettingsLoadState.Corrupt, null); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(SettingsLoadState.Unavailable, null); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken token = default)
    {
        settings.Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("App configuration exceeds its byte bound.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp"; var owned = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { owned = true; stream.Write(bytes); stream.Flush(true); }
                    token.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, overwrite: true); owned = false;
                }
                finally { if (owned) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new InvalidDataException(); RequireUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RequireUnique(item);
    }
}
