namespace FluffHistory.Engine;

internal static class RestoreTimestamps
{
    // Original-location restore is a new live modification, even when restoring current bytes.
    internal static bool Apply(string path, DateTimeOffset historical, DateTimeOffset? baseline,
        DateTimeOffset? restoredAt, Action<string, DateTime> setLastWrite)
    {
        var time = restoredAt ?? historical; setLastWrite(path, time.UtcDateTime);
        if (restoredAt is null) return false;
        var actual = File.GetLastWriteTimeUtc(path);
        if (baseline is null || actual != baseline.Value.UtcDateTime) return false;
        for (var i = 0; i < 8; i++)
        {
            var delta = TimeSpan.FromSeconds(2 << i);
            var candidate = time <= DateTimeOffset.MaxValue - delta ? time + delta : time - delta;
            setLastWrite(path, candidate.UtcDateTime);
            if (File.GetLastWriteTimeUtc(path) != baseline.Value.UtcDateTime) return true;
        }
        throw new IOException("Cannot represent an original-location modification time distinct from the successful baseline.");
    }
}
