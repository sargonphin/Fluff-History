using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public async Task RoundTripRetainsStableRootAndUnrepresentedConfigurationFieldsWithoutRuntimeHistory()
    {
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json"));
        Assert.Equal(SettingsLoadState.Missing, (await store.LoadAsync()).State);
        var root = new SourceRootConfiguration(Guid.NewGuid(), "descriptive original", false, 12345, ["Excluded/Subtree"]);
        var settings = AppSettings.Empty with { RepositoryPath = temp.Child(), Sources = [SourceMapping.FromConfiguration(root, temp.Child("source"))],
            SchedulingEnabled = true, IntervalMinutes = 30 };
        await store.SaveAsync(settings); var loaded = await store.LoadAsync(); Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        Assert.Equal(root, loaded.Settings!.Sources[0].ToConfiguration()); Assert.Equal(settings.AnchorUtc, loaded.Settings.AnchorUtc);
        Assert.Equal(settings.Sources[0].SourceRootId, loaded.Settings.Sources[0].SourceRootId);
        var text = File.ReadAllText(store.FilePath);
        foreach (var forbidden in new[] { "password", "master", "dpapi", "lastAttempt", "lastSuccess", "cursor", "preview", "skippedCadences" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(loaded.Settings.ToBackupConfiguration().Sources);
    }

    [Theory]
    [InlineData("schema")] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("missing")] [InlineData("syntax")] [InlineData("oversized")] [InlineData("oldWorkspace")]
    public async Task InvalidSettingsAreExplicitlyCorruptAndNeverAutoOverwritten(string fault)
    {
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json"));
        await store.SaveAsync(AppSettings.Empty);
        var text = File.ReadAllText(store.FilePath);
        text = fault switch
        {
            "schema" => text.Replace("\"schema\":2", "\"schema\":3", StringComparison.Ordinal),
            "unknown" => text.Replace("{", "{\"password\":\"synthetic\",", StringComparison.Ordinal),
            "oldWorkspace" => text.Replace("{", "{\"capacityWorkspace\":null,", StringComparison.Ordinal),
            "duplicate" => text.Replace("\"schema\":2", "\"schema\":2,\"schema\":2", StringComparison.Ordinal),
            "missing" => text.Replace("\"schema\":2,", "", StringComparison.Ordinal),
            "syntax" => "{", _ => new string(' ', 4 * 1024 * 1024 + 1)
        };
        File.WriteAllText(store.FilePath, text);
        Assert.Equal(SettingsLoadState.Corrupt, (await store.LoadAsync()).State); Assert.Equal(text, File.ReadAllText(store.FilePath));
    }

    [Theory]
    [InlineData("relative")] [InlineData("interval")] [InlineData("root")] [InlineData("duplicates")] [InlineData("many")]
    public async Task SaveRejectsInvalidBoundsWithoutReplacingLastValidFile(string fault)
    {
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json")); var valid = AppSettings.Empty;
        await store.SaveAsync(valid); var before = File.ReadAllBytes(store.FilePath);
        var source = SourceMapping.New(temp.Child("source"));
        var invalid = fault switch
        {
            "relative" => valid with { RepositoryPath = "relative" }, "interval" => valid with { IntervalMinutes = 0 },
            "root" => valid with { Sources = [source with { SourceRootId = Guid.Empty }] },
            "duplicates" => valid with { Sources = [source, source] },
            _ => valid with { Sources = Enumerable.Range(0, 1025).Select(_ => source with { SourceRootId = Guid.NewGuid() }).ToImmutableArray() }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(invalid)); Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public async Task AtomicReplacementAndCancellationLeaveWholeValidDocumentAndNoOwnedTemps()
    {
        using var temp = new TemporaryDirectory(); var store = new AppSettingsStore(temp.Child("settings.json"));
        await store.SaveAsync(AppSettings.Empty); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(AppSettings.Empty with { IntervalMinutes = 30 }, cancel.Token));
        Assert.Equal(60, (await store.LoadAsync()).Settings!.IntervalMinutes);
        await Task.WhenAll(ProductConfiguration.Frequencies.Select(f => store.SaveAsync(AppSettings.Empty with { IntervalMinutes = f.Minutes })));
        Assert.Equal(SettingsLoadState.Loaded, (await store.LoadAsync()).State); Assert.Empty(Directory.GetFiles(temp.Root, "*.tmp"));
    }

    [Fact]
    public void ExistingOriginalPathNeverAutomaticallyBecomesRuntimeMapping()
    {
        var mapping = SourceMapping.FromConfiguration(new(Guid.NewGuid(), Path.GetTempPath(), maximumFileSize: 17, excludedDirectories: ["skip"]));
        Assert.Null(mapping.RuntimePath); Assert.Null(mapping.ToSelection());
        var changed = mapping with { RuntimePath = Path.GetTempPath() };
        Assert.Equal(mapping.SourceRootId, changed.SourceRootId); Assert.Equal(mapping.ToConfiguration(), changed.ToSelection()!.Configuration);
    }
}
