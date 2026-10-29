using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using Xunit;

namespace FluffHistory.Tests;

public sealed partial class PresentationTests
{
    [Theory]
    [InlineData(UpdateChannel.Beta, "1.1.0-beta.5", "1.1.0.5", "1.1.0", false, "1.1.0.6", true)]
    [InlineData(UpdateChannel.Stable, "1.0.0", "1.0.0.1", "1.1.0-beta.5", true, "1.1.0.5", false)]
    [InlineData(UpdateChannel.Beta, "1.0.0", "1.0.0.1", "1.1.0-beta.5", true, "1.1.0.5", true)]
    [InlineData(UpdateChannel.Beta, "1.1.0", "1.1.0.6", "1.1.0-beta.5", true, "1.1.0.5", false)]
    [InlineData(UpdateChannel.Beta, "1.1.0-beta.5", "1.1.0.5", "1.2.0-beta.1", true, "1.2.0.1", true)]
    [InlineData(UpdateChannel.Stable, "1.0.0", "1.0.0.1", "1.1.0", false, "1.1.0.6", true)]
    public void PublishedChannelsKeepTheirOrderingThroughThePresentationFallback(
        UpdateChannel channel, string current, string currentBuild, string candidate, bool beta, string candidateBuild, bool expected)
    {
        // Published builds retain a semantic release identity. Four-part Windows
        // build numbers remain separate and do not override Stable/Beta ordering.
        Assert.True(ProductBuildVersion.MatchesRelease(Version.Parse(currentBuild), ReleaseVersion.Parse(current)));
        Assert.True(ProductBuildVersion.MatchesRelease(Version.Parse(candidateBuild), ReleaseVersion.Parse(candidate)));
        var release = new UpdateRelease(1, candidate, "Release", beta,
            $"https://github.com/sargonphin/Fluff-History/releases/tag/{candidate}", DateTimeOffset.UtcNow,
            1, 1, $"https://github.com/sargonphin/Fluff-History/releases/download/{candidate}/Fluff-History-Setup.exe", null);
        var selected = UpdateReleaseSelection.Select([release], channel, ReleaseVersion.Parse(current));
        var owner = new Context { InstalledProductVersion = currentBuild, Updates = new(current,
            UpdatePreferences.Default with { Channel = channel }, DateTimeOffset.UtcNow, UpdatePhase.Idle, selected) };
        Assert.Equal(expected, selected is not null);
        Assert.Equal(expected, new OverviewViewModel(owner, new Dialogs()).HasUpdate);
        Assert.Equal(expected, new SettingsViewModel(owner).HasUpdate);
    }
}
