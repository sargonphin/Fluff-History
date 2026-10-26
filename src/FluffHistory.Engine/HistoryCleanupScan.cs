using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>One validated path-major traversal, with one pending publication for latest selection.</summary>
internal sealed class HistoryCleanupScan : IDisposable
{
    private readonly IEnumerator<RetentionVersion> _versions;
    private readonly HistoryCleanupEvaluation _evaluation;
    private readonly long _maximum;
    private readonly CancellationToken _token;
    private RetentionVersion? _pending;
    private VersionIdentity? _after;
    private HistoryPath? _lastPath, _lastAffected;
    private bool _end, _currentSeen;
    internal long Examined { get; private set; }
    internal long PathsExamined { get; private set; }
    internal long Protected { get; private set; }
    internal long Eligible { get; private set; }
    internal long AffectedPaths { get; private set; }
    internal long EligibleLogicalBytes { get; private set; }
    internal bool BudgetExceeded { get; private set; }
    internal bool Complete => _end && _pending is null;
    internal CleanupPosition Position => new(_after, _pending, _currentSeen);

    internal HistoryCleanupScan(RepositoryReader reader, HistoryCleanupEvaluation evaluation, long maximum,
        CancellationToken token, CleanupPosition? position = null)
    {
        _evaluation = evaluation; _maximum = maximum; _token = token;
        _after = position?.After; _pending = position?.Pending; _lastPath = _after?.Path; _currentSeen = position?.CurrentSeen ?? false;
        _versions = reader.EnumerateRetentionVersions(_after, token).GetEnumerator();
    }

    internal bool Next(out RetentionVersion version, out bool eligible)
    {
        _token.ThrowIfCancellationRequested(); version = null!; eligible = false;
        if (_evaluation.Mode != HistoryCleanupMode.KeepOnlyMostRecent)
        {
            if (!Read(out version)) return false;
            eligible = !version.IsCurrent && version.StoredUtc < _evaluation.CutoffUtc;
        }
        else
        {
            if (_pending is null)
            {
                if (!Read(out var first)) return false;
                _pending = first;
            }
            if (Read(out var next))
            {
                version = _pending;
                if (version.Identity.Path == next.Identity.Path)
                {
                    if (version.Identity.Sequence > next.Identity.Sequence ||
                        (version.Identity.Sequence == next.Identity.Sequence && version.Identity.Ordinal >= next.Identity.Ordinal))
                        throw new InvalidDataException("Ambiguous publication order within logical path.");
                    eligible = true;
                }
                _pending = next;
            }
            else
            {
                if (!_end) return false; // Work stop: retain the undecided publication in the cursor.
                version = _pending; _pending = null; // The final publication survives, with or without Current.
            }
        }
        if (eligible)
        {
            Eligible++; EligibleLogicalBytes = checked(EligibleLogicalBytes + version.Length);
            if (_lastAffected != version.Identity.Path) { AffectedPaths++; _lastAffected = version.Identity.Path; }
        }
        return true;
    }

    private bool Read(out RetentionVersion version)
    {
        version = null!; if (_end) return false;
        _token.ThrowIfCancellationRequested();
        if (Examined >= _maximum) { BudgetExceeded = true; return false; }
        if (!_versions.MoveNext()) { _end = true; return false; }
        version = _versions.Current; _after = version.Identity; Examined++;
        if (version.IsCurrent) Protected++;
        if (_lastPath == version.Identity.Path && _currentSeen)
            throw new InvalidDataException("Current version is not the newest surviving publication.");
        if (_lastPath != version.Identity.Path) { PathsExamined++; _lastPath = version.Identity.Path; _currentSeen = false; }
        _currentSeen |= version.IsCurrent;
        return true;
    }
    public void Dispose() => _versions.Dispose();
}
