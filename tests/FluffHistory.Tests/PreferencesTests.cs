using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class PreferencesTests
{
    [Fact]
    public async Task LanguageIsOptionalForOldFilesAndResetReturnsToWindows()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("preferences-v1.json");
        File.WriteAllText(path, "{\"schema\":1,\"appearance\":\"dark\",\"timelineViewMode\":\"list\"}");
        var store = new ApplicationPreferencesStore(path); var old = await store.LoadAsync();
        Assert.Equal(SettingsLoadState.Loaded, old.State); Assert.Null(old.Preferences.Language);
        var selected = old.Preferences with { Language = "fi-FI" }; await store.SaveAsync(selected);
        Assert.Equal(selected, (await new ApplicationPreferencesStore(path).LoadAsync()).Preferences);
        await store.SaveAsync(selected with { Language = null });
        var reset = (await store.LoadAsync()).Preferences;
        Assert.Null(reset.Language); Assert.Equal(AppearancePreference.Dark, reset.Appearance); Assert.Equal(TimelineViewMode.List, reset.TimelineViewMode);
        Assert.DoesNotContain("language", File.ReadAllText(path));
    }
    [Theory]
    [InlineData("")]
    [InlineData("fi")]
    [InlineData("fi-fi")]
    [InlineData("unknown")]
    public async Task UnsupportedLanguageCannotReplaceExistingPreference(string language)
    {
        using var temp = new TemporaryDirectory(); var store = new ApplicationPreferencesStore(temp.Child("preferences-v1.json"));
        await store.SaveAsync(ApplicationPreferences.Default); var bytes = File.ReadAllBytes(store.FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(ApplicationPreferences.Default with { Language = language }));
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
    }
    [Fact]
    public async Task MissingUsesSystemAndLargeIconsWithoutCreatingAFile()
    {
        using var temp = new TemporaryDirectory(); var store = new ApplicationPreferencesStore(temp.Child("preferences-v1.json"));
        var result = await store.LoadAsync(); Assert.Equal(SettingsLoadState.Missing, result.State);
        Assert.Equal(AppearancePreference.System, result.Preferences.Appearance); Assert.Equal(TimelineViewMode.LargeIcons, result.Preferences.TimelineViewMode);
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData(AppearancePreference.System, TimelineViewMode.LargeIcons)]
    [InlineData(AppearancePreference.Light, TimelineViewMode.List)]
    [InlineData(AppearancePreference.Dark, TimelineViewMode.List)]
    public async Task AtomicRoundtripIsIndependentOfBackupConfiguration(AppearancePreference appearance, TimelineViewMode view)
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("preferences-v1.json"); var backup = temp.Child("settings-v2.json");
        File.WriteAllText(backup, "untouched"); var store = new ApplicationPreferencesStore(path);
        await store.SaveAsync(ApplicationPreferences.Default); var preferences = new ApplicationPreferences(1, appearance, view);
        await store.SaveAsync(preferences); var reloaded = await new ApplicationPreferencesStore(path).LoadAsync();
        Assert.Equal(SettingsLoadState.Loaded, reloaded.State); Assert.Equal(preferences, reloaded.Preferences);
        Assert.Equal("untouched", File.ReadAllText(backup)); Assert.Empty(Directory.EnumerateFiles(temp.Root, "*.tmp"));
        var json = File.ReadAllText(path); Assert.DoesNotContain("repository", json); Assert.DoesNotContain("password", json); Assert.DoesNotContain("status", json);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schema\":2,\"appearance\":\"system\",\"timelineViewMode\":\"list\"}")]
    [InlineData("{\"schema\":1,\"appearance\":\"other\",\"timelineViewMode\":\"list\"}")]
    [InlineData("{\"schema\":1,\"appearance\":1,\"timelineViewMode\":\"list\"}")]
    [InlineData("{\"schema\":1,\"schema\":1,\"appearance\":\"dark\",\"timelineViewMode\":\"list\"}")]
    [InlineData("{\"schema\":1,\"appearance\":\"dark\",\"timelineViewMode\":\"list\",\"language\":\"en\"}")]
    public async Task MalformedUnknownAndDuplicateInputUsesDefaultsWithoutOverwriting(string json)
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("preferences-v1.json"); File.WriteAllText(path, json);
        var result = await new ApplicationPreferencesStore(path).LoadAsync();
        Assert.Equal(SettingsLoadState.Corrupt, result.State); Assert.Equal(ApplicationPreferences.Default, result.Preferences); Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public async Task OversizedInputAndCancelledSavePreserveExistingBytes()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("preferences-v1.json"); var bytes = new string(' ', 16385); File.WriteAllText(path, bytes);
        var store = new ApplicationPreferencesStore(path); Assert.Equal(SettingsLoadState.Corrupt, (await store.LoadAsync()).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(ApplicationPreferences.Default, new(true)));
        Assert.Equal(bytes, File.ReadAllText(path));
    }

    [WindowsFact]
    public async Task UnavailablePreferencesUsesDefaultsWithoutChangingBackupOrLockedFile()
    {
        using var temp = new TemporaryDirectory(); var path = temp.Child("preferences-v1.json"); File.WriteAllText(path, "locked");
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await new ApplicationPreferencesStore(path).LoadAsync();
        Assert.Equal(SettingsLoadState.Unavailable, result.State); Assert.Equal(ApplicationPreferences.Default, result.Preferences);
    }
}
