namespace FluffHistory.Core;

/// <summary>Bytes available to the calling identity on the repository filesystem, including quota restrictions.</summary>
public sealed record CapacityObservation
{
    public long Capacity { get; }
    public long Free { get; }
    public CapacityObservation(long capacity, long free)
    {
        if (capacity <= 0 || free < 0 || free > capacity) throw new ArgumentOutOfRangeException(nameof(capacity), "Require C > 0 and 0 <= F <= C.");
        Capacity = capacity; Free = free;
    }
}

/// <summary>No clock or age policy. UpcomingAllocation is supplied explicitly, never inferred from logical version lengths.</summary>
public sealed record CapacityTarget
{
    public CapacityObservation Observation { get; }
    public long UpcomingAllocation { get; }
    public long Reserve { get; }
    public long RequiredFree { get; }
    public long Deficit => Math.Max(RequiredFree - Observation.Free, 0);
    public bool Impossible => RequiredFree > Observation.Capacity;
    public bool Satisfied => Observation.Free >= RequiredFree;
    public CapacityTarget(CapacityObservation observation, long upcomingAllocation)
    {
        ArgumentNullException.ThrowIfNull(observation); ArgumentOutOfRangeException.ThrowIfNegative(upcomingAllocation);
        Observation = observation; UpcomingAllocation = upcomingAllocation;
        Reserve = checked(observation.Capacity / 20 + (observation.Capacity % 20 == 0 ? 0 : 1));
        RequiredFree = checked(upcomingAllocation + Reserve);
    }
}
