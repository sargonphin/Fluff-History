using FluffHistory.Core;

namespace FluffHistory.Engine;

public enum ExistingTargetPolicy { FailIfExists, SkipExisting, OverwriteFile }
public enum RestoreFailurePolicy { StopOnFailure, ContinueIndependentFiles }
public sealed record RestoreOptions(ExistingTargetPolicy ExistingTarget = ExistingTargetPolicy.FailIfExists,
    RestoreFailurePolicy FailurePolicy = RestoreFailurePolicy.StopOnFailure);
public sealed record RestoreProblem(VersionIdentity Identity, string Destination, string Reason);

/// <summary>Transient progress. A folder has independent per-file install boundaries, never a cross-file transaction.</summary>
public sealed class RestoreResult
{
    public AuthorityIdentity Authority { get; internal set; } = null!;
    public long Selected { get; internal set; }
    public bool SelectionComplete { get; internal set; }
    public long Restored { get; internal set; }
    public long Skipped { get; internal set; }
    public long Failed { get; internal set; }
    public long SelectionConflicts { get; internal set; }
    public long BytesRead { get; internal set; }
    public long BytesWritten { get; internal set; }
    public long ChecksumsVerified { get; internal set; }
    public long ChecksumFailures { get; internal set; }
    public long MtimeAdjustedForChangeDetection { get; internal set; }
    public bool Cancelled { get; internal set; }
    private readonly List<RestoreProblem> _problems = [];
    public IReadOnlyList<RestoreProblem> Problems => _problems.AsReadOnly();
    internal void Problem(VersionIdentity identity, string destination, string reason)
    { if (_problems.Count < 256) _problems.Add(new(identity, destination, reason)); }
}

public sealed class RestoreChecksumException : IOException
{
    internal RestoreChecksumException() : base("Restored content checksum does not match the exact stored version.") { }
}
