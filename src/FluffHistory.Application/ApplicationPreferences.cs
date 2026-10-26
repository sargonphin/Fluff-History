using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluffHistory.Application;

public enum AppearancePreference { System, Light, Dark }
public enum TimelineViewMode { LargeIcons, List }
public sealed record ApplicationPreferences([property: JsonRequired] int Schema,
    [property: JsonRequired] AppearancePreference Appearance, [property: JsonRequired] TimelineViewMode TimelineViewMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Language = null)
{
    public static ApplicationPreferences Default { get; } = new(1, AppearancePreference.System, TimelineViewMode.LargeIcons);
    public void Validate()
    {
        if (Schema != 1 || !Enum.IsDefined(Appearance) || !Enum.IsDefined(TimelineViewMode) || !ApplicationLanguage.IsSupported(Language))
            throw new InvalidDataException("Unsupported application preference.");
    }
}
public sealed record PreferencesLoadResult(SettingsLoadState State, ApplicationPreferences Preferences);

/// <summary>Small desired UI preferences only. Corrupt/unavailable input uses safe defaults independently of backup configuration.</summary>
public sealed class ApplicationPreferencesStore(string filePath)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);
    private const int MaximumBytes = 16 * 1024;
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4,
        Converters = { new JsonStringEnumConverter<AppearancePreference>(JsonNamingPolicy.CamelCase, allowIntegerValues: false), new JsonStringEnumConverter<TimelineViewMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    public async Task<PreferencesLoadResult> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run<PreferencesLoadResult>(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > MaximumBytes) return new(SettingsLoadState.Corrupt, ApplicationPreferences.Default);
                    var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
                    using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException("Duplicate preference.");
                    var preferences = JsonSerializer.Deserialize<ApplicationPreferences>(bytes, Options) ?? throw new JsonException();
                    preferences.Validate(); return new(SettingsLoadState.Loaded, preferences);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
                { return new(SettingsLoadState.Corrupt, ApplicationPreferences.Default); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { return new(SettingsLoadState.Missing, ApplicationPreferences.Default); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { return new(SettingsLoadState.Unavailable, ApplicationPreferences.Default); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken token = default)
    {
        preferences.Validate(); await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, Options);
                    if (bytes.Length > MaximumBytes) throw new InvalidDataException("Preferences exceed their bound.");
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
