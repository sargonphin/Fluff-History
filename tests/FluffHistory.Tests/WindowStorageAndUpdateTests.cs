using FluffHistory.Application;
using FluffHistory.Engine;
using FluffHistory.Desktop.Presentation.Models;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Fact]
    public async Task WindowSizeIsSharedWithoutNavigationOrActiveBatchRescans()
    {
        var calls = 0; var unavailable = false;
        var owner = new Context { Settings = AppSettings.Empty with { RepositoryPath = Path.GetFullPath("backup") },
            Status = StatusForUi(false), BackupObserver = (_, _) => Task.FromResult<long?>(++calls > 0 && !unavailable ? calls * 100 : null) };
        var overview = new OverviewViewModel(owner, new Dialogs());
        var configuration = new ConfigurationViewModel(owner, new Dialogs(), overview.Storage);
        await overview.Storage.ActivateAsync(overview.StoragePath);
        Assert.Equal(overview.Storage.BackupSize, configuration.BackupSize);
        foreach (var page in new[] { "Configuration", "History", "Overview", "Settings", "Overview" })
        { owner.CurrentPage = page; owner.Notify(); }
        Assert.Equal(1, calls);
        owner.Status = StatusForUi(true); owner.Notify();
        owner.HistoryRevision++; owner.Notify(); Assert.Equal(1, calls);
        var now = DateTimeOffset.UtcNow;
        owner.Status = StatusForUi(false) with { LastAttempt = new(AttemptTrigger.Manual, null, now, now,
            TimeSpan.Zero, new(BackupOutcome.Completed)) };
        owner.Notify(); await Eventually(() => calls == 2);
        Assert.Equal(overview.Storage.BackupSize, configuration.BackupSize);
        owner.Notify(); Assert.Equal(2, calls);
        unavailable = true; owner.HistoryRevision++; owner.Notify();
        Assert.Equal(3, calls); Assert.Contains("200", overview.Storage.BackupSize);
        Assert.Contains("at last check", configuration.BackupSize); overview.Storage.Deactivate();
    }

    [Fact]
    public void OverviewOnlyClaimsUpToDateAfterSuccessfulCheck()
    {
        var owner = new Context { Updates = new("1.0.0", UpdatePreferences.Default, null, UpdatePhase.Idle, null) };
        var model = new OverviewViewModel(owner, new Dialogs());
        Assert.DoesNotContain("Up to date", model.UpdateText);
        owner.Updates = owner.Updates with { LastCheckedUtc = DateTimeOffset.UtcNow };
        Assert.Equal("Up to date at version 1.0.0", model.UpdateText);
        owner.Updates = owner.Updates with { Failure = UpdateFailure.Offline };
        Assert.DoesNotContain("Up to date", model.UpdateText);
        owner.Updates = owner.Updates with { Available = new(1, "1.1.0", "New release", false,
            "https://github.com/sargonphin/Fluff-History/releases/tag/v1.1.0", DateTimeOffset.UtcNow,
            1, 1, "https://example.invalid/setup.exe", null) };
        Assert.True(model.HasUpdate); Assert.False(model.HasNoUpdate);
        Assert.Equal("A new version is available", model.UpdateText);
    }
    [Theory]
    [InlineData("1.0.0", false)]
    [InlineData("1.1.0", false)]
    [InlineData("1.1.1", true)]
    [InlineData("1.2.0-beta.1", true)]
    public void DevelopmentBuildDoesNotAdvertiseAnOlderPublicRelease(string candidate, bool newer)
    {
        var owner = new Context { InstalledProductVersion = "1.1.0.5", Updates = new(null,
            UpdatePreferences.Default, DateTimeOffset.UtcNow, UpdatePhase.Available,
            new(1, candidate, "Release", candidate.Contains("beta"), "https://example.invalid/release",
                DateTimeOffset.UtcNow, 1, 1, "https://example.invalid/setup.exe", null)) };
        var overview = new OverviewViewModel(owner, new Dialogs());
        var settings = new SettingsViewModel(owner);
        Assert.Equal(newer, overview.HasUpdate); Assert.Equal(newer, settings.HasUpdate);
        Assert.False(settings.CanInstallUpdate); // Display comparison never unlocks development installs.
        Assert.Contains("1.1.0.5", settings.CurrentUpdateVersion);
        if (!newer) Assert.Equal("Up to date at version 1.1.0.5", overview.UpdateText);
    }}
