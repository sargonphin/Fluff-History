using FluffHistory.Application;
using FluffHistory.Storage;
using FluffHistory.Windows;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class BackupSafeguardTests
{
    [Fact]
    public async Task ManualBatteryOverrideRetainsScheduledBatteryAndManualNetworkSafeguards()
    {
        var probes = new Probes { Power = new(PowerSupply.Battery) };
        var safeguards = new BackupSafeguards(probes, probes, probes);
        var time = new ManualSchedulerTime(BackupCoordinatorTests.Anchor.AddMinutes(-1));
        var runner = new BackupCoordinatorTests.ControlledRunner();
        await using var coordinator = new BackupCoordinator(BackupCoordinatorTests.Configuration(),
            new(TimeSpan.FromMinutes(10), BackupCoordinatorTests.Anchor), runner, safeguards, time);
        Assert.True(coordinator.TryRunNow()); var manual = await runner.Next(); Assert.Equal(AttemptTrigger.Manual, manual.Trigger);
        manual.Complete(); await BackupCoordinatorTests.Until(coordinator, s => !s.IsAttemptActive);
        coordinator.Start(); time.Advance(TimeSpan.FromMinutes(1));
        await BackupCoordinatorTests.Until(coordinator, s => !s.IsAttemptActive && s.LastSafeguard.Reason == SafeguardReason.OnBattery);
        Assert.Equal(1, runner.Calls);
        probes.Path = PathNetworkDependency.Network; probes.Network = new(NetworkAvailability.Available, NetworkCost.Metered);
        Assert.True(coordinator.TryRunNow());
        await BackupCoordinatorTests.Until(coordinator, s => !s.IsAttemptActive && s.LastSafeguard.Reason == SafeguardReason.MeteredNetwork);
        Assert.Equal(1, runner.Calls);
        probes.Network = new(NetworkAvailability.Unavailable, NetworkCost.Unrestricted);
        Assert.Equal(SafeguardReason.NetworkUnavailable, safeguards.Evaluate(BackupCoordinatorTests.Configuration(), AttemptTrigger.Manual).Reason);
        probes.Power = new(PowerSupply.Unknown);
        Assert.Equal(SafeguardReason.UnknownPower, safeguards.Evaluate(BackupCoordinatorTests.Configuration(new(BlockUnknownPower: true)), AttemptTrigger.Manual).Reason);
    }

    [Theory]
    [InlineData(PowerSupply.AC, false, false, true, SafeguardReason.None)]
    [InlineData(PowerSupply.Battery, true, false, true, SafeguardReason.None)]
    [InlineData(PowerSupply.Battery, false, false, false, SafeguardReason.OnBattery)]
    [InlineData(PowerSupply.Unknown, false, false, true, SafeguardReason.None)]
    [InlineData(PowerSupply.Unknown, false, true, false, SafeguardReason.UnknownPower)]
    public void ExplicitPowerPolicy(PowerSupply supply, bool allowBattery, bool blockUnknown, bool allowed, SafeguardReason reason)
    {
        var probes = new Probes { Power = new(supply) };
        var decision = new BackupSafeguards(probes, probes, probes).Evaluate(
            BackupCoordinatorTests.Configuration(new(AllowOnBattery: allowBattery, BlockUnknownPower: blockUnknown)));
        Assert.Equal(allowed, decision.Allowed); Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(PathNetworkDependency.Local, NetworkAvailability.Unavailable, NetworkCost.Metered, false, true)]
    [InlineData(PathNetworkDependency.Unknown, NetworkAvailability.Unavailable, NetworkCost.Metered, false, true)]
    [InlineData(PathNetworkDependency.Network, NetworkAvailability.Available, NetworkCost.Unrestricted, false, true)]
    [InlineData(PathNetworkDependency.Network, NetworkAvailability.Available, NetworkCost.Metered, false, false)]
    [InlineData(PathNetworkDependency.Network, NetworkAvailability.Available, NetworkCost.Costed, false, false)]
    [InlineData(PathNetworkDependency.Network, NetworkAvailability.Available, NetworkCost.Metered, true, true)]
    [InlineData(PathNetworkDependency.Network, NetworkAvailability.Unavailable, NetworkCost.Unknown, false, false)]
    [InlineData(PathNetworkDependency.Network, NetworkAvailability.Unknown, NetworkCost.Unknown, false, true)]
    public void NetworkHintsOnlyApplyToConfidentNetworkDependency(PathNetworkDependency path, NetworkAvailability available,
        NetworkCost cost, bool allowMetered, bool allowed)
    {
        var probes = new Probes { Path = path, Network = new(available, cost) };
        var decision = new BackupSafeguards(probes, probes, probes).Evaluate(
            BackupCoordinatorTests.Configuration(new(AllowMeteredNetwork: allowMetered)));
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(path == PathNetworkDependency.Network ? 1 : 0, probes.NetworkReads);
        if (!allowed) Assert.Equal(available == NetworkAvailability.Unavailable ? SafeguardReason.NetworkUnavailable : SafeguardReason.MeteredNetwork, decision.Reason);
    }

    [Fact]
    public void NetworkSourceCanRequireNetworkAndUnavailablePolicyCanBeDisabled()
    {
        var probes = new Probes { Path = PathNetworkDependency.Network, Network = new(NetworkAvailability.Unavailable, NetworkCost.Unknown), RepositoryLocal = true };
        var safeguard = new BackupSafeguards(probes, probes, probes);
        Assert.Equal(SafeguardReason.NetworkUnavailable, safeguard.Evaluate(BackupCoordinatorTests.Configuration()).Reason);
        Assert.True(safeguard.Evaluate(BackupCoordinatorTests.Configuration(new(DeferNetworkPathsWhenUnavailable: false))).Allowed);
    }

    [Fact]
    public void FailingProbesAndAmbiguousPathsAreUnknownAndNeverRepositoryTruth()
    {
        var probes = new Probes { Throw = true };
        var safeguard = new BackupSafeguards(probes, probes, probes);
        Assert.True(safeguard.Evaluate(BackupCoordinatorTests.Configuration()).Allowed);
        Assert.Equal(SafeguardReason.UnknownPower, safeguard.Evaluate(BackupCoordinatorTests.Configuration(new(BlockUnknownPower: true))).Reason);
        Assert.Equal(PathNetworkDependency.Unknown, RepositoryLocation.ClassifyNetworkDependency("ambiguous-relative-path"));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void MeteredPreferenceCoversNetworkDestinationAndNetworkSource(bool localDestination, bool allowMetered)
    {
        var probes = new Probes { RepositoryLocal = localDestination, Path = PathNetworkDependency.Network,
            Network = new(NetworkAvailability.Available, NetworkCost.Metered) };
        var decision = new BackupSafeguards(probes, probes, probes).Evaluate(
            BackupCoordinatorTests.Configuration(new(AllowMeteredNetwork: allowMetered)), AttemptTrigger.Manual);
        Assert.Equal(allowMetered, decision.Allowed);
        Assert.Equal(allowMetered ? SafeguardReason.None : SafeguardReason.MeteredNetwork, decision.Reason);
    }

    [WindowsTheory]
    [InlineData(@"\\server\share\folder")] [InlineData(@"\\?\UNC\server\share\folder")]
    public void UncClassificationDoesNotNeedServerAccess(string path) =>
        Assert.Equal(PathNetworkDependency.Network, RepositoryLocation.ClassifyNetworkDependency(path));

    [Theory]
    [InlineData(0u, NetworkCost.Unknown)] [InlineData(1u, NetworkCost.Unrestricted)]
    [InlineData(2u, NetworkCost.Metered)] [InlineData(4u, NetworkCost.Metered)]
    [InlineData(0x10001u, NetworkCost.Costed)] [InlineData(0x20001u, NetworkCost.Costed)]
    [InlineData(0x40002u, NetworkCost.Costed)] [InlineData(0x80004u, NetworkCost.Costed)]
    public void NativeCostFlagsMapWithoutDependingOnHostNetwork(uint flags, NetworkCost expected) =>
        Assert.Equal(expected, WindowsNetworkStateProvider.ClassifyCost(flags));

    private sealed class Probes : IPowerStateProvider, INetworkStateProvider, IPathNetworkClassifier
    {
        internal PowerState Power = new(PowerSupply.AC);
        internal NetworkState Network = new(NetworkAvailability.Available, NetworkCost.Unrestricted);
        internal PathNetworkDependency Path = PathNetworkDependency.Local;
        internal bool Throw, RepositoryLocal;
        internal int NetworkReads;
        PowerState IPowerStateProvider.Read() => Throw ? throw new IOException("probe") : Power;
        NetworkState INetworkStateProvider.Read() { NetworkReads++; return Throw ? throw new IOException("probe") : Network; }
        public PathNetworkDependency Classify(string path) => Throw ? throw new IOException("probe") : RepositoryLocal && path == "repository" ? PathNetworkDependency.Local : Path;
    }
}
