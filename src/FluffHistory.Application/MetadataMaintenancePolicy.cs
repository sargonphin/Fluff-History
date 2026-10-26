namespace FluffHistory.Application;

internal static class MetadataMaintenancePolicy
{
    // One normal 64 MiB logical metadata segment. The M13 sparse-write and M19.1
    // Baseline measurements show why ratio alone churns small compressed repositories.
    internal const long MinimumReclaimableBytes = 64L * 1024 * 1024;
    internal static bool Eligible(long activeBytes, long reclaimableBytes) => activeBytes > 0 &&
        reclaimableBytes >= MinimumReclaimableBytes && reclaimableBytes <= activeBytes &&
        (decimal)reclaimableBytes * 10 >= (decimal)activeBytes * 3;
}
