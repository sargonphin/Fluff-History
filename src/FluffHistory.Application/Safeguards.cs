using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum PowerSupply { Unknown, AC, Battery }
public enum NetworkAvailability { Unknown, Available, Unavailable }
public enum NetworkCost { Unknown, Unrestricted, Metered, Costed }
public enum SafeguardReason { None, OnBattery, UnknownPower, MeteredNetwork, NetworkUnavailable }
public sealed record PowerState(PowerSupply Supply);
/// <summary>OS hints only. Neither availability nor default-route cost proves anything about SMB reachability.</summary>
public sealed record NetworkState(NetworkAvailability Availability, NetworkCost Cost);
public sealed record SafeguardPolicy([property: System.Text.Json.Serialization.JsonRequired] bool AllowOnBattery = false,
    [property: System.Text.Json.Serialization.JsonRequired] bool BlockUnknownPower = false,
    [property: System.Text.Json.Serialization.JsonRequired] bool AllowMeteredNetwork = false,
    [property: System.Text.Json.Serialization.JsonRequired] bool DeferNetworkPathsWhenUnavailable = true);
public sealed record SafeguardDecision(bool Allowed, SafeguardReason Reason = SafeguardReason.None)
{
    public static SafeguardDecision Allow { get; } = new(true);
}
public interface IPowerStateProvider { PowerState Read(); }
public interface INetworkStateProvider { NetworkState Read(); }
public interface IPathNetworkClassifier { PathNetworkDependency Classify(string path); }
public interface IBackupSafeguard
{
    SafeguardDecision Evaluate(BackupConfiguration configuration);
    SafeguardDecision Evaluate(BackupConfiguration configuration, AttemptTrigger trigger) => Evaluate(configuration);
}

public sealed class BackupSafeguards(IPowerStateProvider power, INetworkStateProvider network,
    IPathNetworkClassifier paths) : IBackupSafeguard
{
    public SafeguardDecision Evaluate(BackupConfiguration configuration) => Evaluate(configuration, AttemptTrigger.Scheduled);
    public SafeguardDecision Evaluate(BackupConfiguration configuration, AttemptTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var policy = configuration.Safeguards;
        // Probe failures are unknown, never fabricated power/network/repository facts.
        PowerState supply;
        try { supply = power.Read(); } catch (Exception) { supply = new(PowerSupply.Unknown); }
        if (trigger == AttemptTrigger.Scheduled && supply.Supply == PowerSupply.Battery && !policy.AllowOnBattery) return new(false, SafeguardReason.OnBattery);
        if (supply.Supply == PowerSupply.Unknown && policy.BlockUnknownPower) return new(false, SafeguardReason.UnknownPower);
        bool needsNetwork = Classify(configuration.RepositoryPath) == PathNetworkDependency.Network ||
            configuration.Sources.Any(s => s.Configuration.IsProtected && Classify(s.DirectoryPath) == PathNetworkDependency.Network);
        if (!needsNetwork) return SafeguardDecision.Allow;
        NetworkState state;
        try { state = network.Read(); } catch (Exception) { state = new(NetworkAvailability.Unknown, NetworkCost.Unknown); }
        if (state.Availability == NetworkAvailability.Unavailable && policy.DeferNetworkPathsWhenUnavailable)
            return new(false, SafeguardReason.NetworkUnavailable);
        if (state.Cost is NetworkCost.Metered or NetworkCost.Costed && !policy.AllowMeteredNetwork)
            return new(false, SafeguardReason.MeteredNetwork);
        return SafeguardDecision.Allow;
    }
    private PathNetworkDependency Classify(string path)
    { try { return paths.Classify(path); } catch (Exception) { return PathNetworkDependency.Unknown; } }
}

public sealed class RepositoryPathNetworkClassifier : IPathNetworkClassifier
{
    public PathNetworkDependency Classify(string path) => RepositoryLocation.ClassifyNetworkDependency(path);
}
