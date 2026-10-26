using System.Collections.Immutable;
using FluffHistory.Engine;
using FluffHistory.Core;

namespace FluffHistory.Application;

/// <summary>Immutable runtime input. No settings or history persistence. Replace the coordinator to change configuration.</summary>
public sealed class BackupConfiguration
{
    public BackupConfiguration(string repositoryPath, IEnumerable<SourceSelection> sources, ScanOptions? scanOptions = null,
        SafeguardPolicy? safeguards = null, RetentionPolicy retentionPolicy = RetentionPolicy.Forever)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(sources);
        var selections = sources.Take(1025).ToImmutableArray();
        if (selections.Length is < 1 or > 1024 || selections.Any(s => s is null || s.Configuration is null || string.IsNullOrWhiteSpace(s.DirectoryPath)))
            throw new ArgumentException("Supply 1..1024 source selections.", nameof(sources));
        selections = selections.Select(s => s with { ExcludedFiles = s.ExcludedFiles?.Take(257).ToImmutableArray(),
            SafetyExcludedDirectories = s.SafetyExcludedDirectories?.Take(257).ToImmutableArray() }).ToImmutableArray();
        foreach (var source in selections.Where(s => s.Configuration.IsProtected))
            if (SourcePaths.Overlaps(repositoryPath, source.DirectoryPath)) throw new ArgumentException(SourcePaths.OverlapMessage);
        if (!Enum.IsDefined(retentionPolicy)) throw new ArgumentOutOfRangeException(nameof(retentionPolicy));
        RepositoryPath = repositoryPath; Sources = selections; ScanOptions = scanOptions ?? new(); Safeguards = safeguards ?? new(); RetentionPolicy = retentionPolicy;
    }
    public string RepositoryPath { get; }
    public ImmutableArray<SourceSelection> Sources { get; }
    public ScanOptions ScanOptions { get; }
    public SafeguardPolicy Safeguards { get; }
    public RetentionPolicy RetentionPolicy { get; }
    public ImmutableHashSet<Guid> ForcedSourceIds { get; init; } = [];
}

/// <summary>Cadence is anchor + n * interval, n >= 0. Start chooses a strictly future occurrence.</summary>
public sealed record BackupSchedule
{
    public BackupSchedule(TimeSpan interval, DateTimeOffset anchorUtc)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (anchorUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Use UTC.", nameof(anchorUtc));
        Interval = interval; AnchorUtc = anchorUtc;
    }
    public TimeSpan Interval { get; }
    public DateTimeOffset AnchorUtc { get; }
    public DateTimeOffset? NextAfter(DateTimeOffset now)
    {
        var delta = now.UtcTicks - AnchorUtc.UtcTicks;
        if (delta < 0) return AnchorUtc;
        var steps = delta / Interval.Ticks + 1;
        if (steps > (DateTimeOffset.MaxValue.UtcTicks - AnchorUtc.UtcTicks) / Interval.Ticks) return null;
        return AnchorUtc.AddTicks(steps * Interval.Ticks);
    }
}

public enum SchedulerLifecycle { Stopped, Idle, Running }
public enum AttemptTrigger { Scheduled, Manual }
public enum BackupPhase { None, CheckingSafeguards, OpeningRepository, Scanning, Maintaining, Finished,
    CheckingMetadata, CompactingMetadata, CleaningMetadata, CompressingMetadata, Encrypting, Decrypting }
public enum BackupOutcome { Completed, CompletedWithSourceProblems, Cancelled, CapacityBlocked, RecoveryRequired, RepositoryUnavailable, ScanControlFailure, SafeguardDeferred, EncryptionMigrationRequired }
public enum ImpactState { NotRequested, Applied, Unsupported, SetupFailed, RestoreFailed }
public sealed record ImpactDiagnostic(ImpactState State, int? NativeError = null);
public sealed record BackupProgress(BackupPhase Phase, long? CompletedUnits = null, long? TotalUnits = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDeterminate => CompletedUnits is >= 0 && TotalUnits is > 0 && CompletedUnits <= TotalUnits;
    public void Validate()
    {
        if (!Enum.IsDefined(Phase) || CompletedUnits < 0 || TotalUnits <= 0 ||
            (CompletedUnits is not null) != (TotalUnits is not null) || CompletedUnits > TotalUnits)
            throw new InvalidDataException("Invalid backup progress.");
    }
}

/// <summary>A detached final result; never shares the Engine's mutable counters across threads.</summary>
public sealed record ScanSnapshot(
    ScanOutcome Outcome, bool Cancelled, bool Partial, bool CapacityBlocked, bool ReconciliationMayBeRequired,
    long SourceSkips, ImmutableArray<SourceProblem> Problems,
    long VersionsCreated, long Reactivations, long Removals, long Unchanged,
    long FilesEnumerated, long DirectoriesEnumerated, long SourceBytesRead, long PayloadBytesWritten,
    long BatchesCommitted, long CapacityRemovalBatches, long CapacityVersionsRemoved,
    CapacityRetentionOutcome? CapacityOutcome)
{
    public static ScanSnapshot FromResult(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Outcome, result.Cancelled, result.Partial, result.CapacityBlocked, result.ReconciliationMayBeRequired,
            result.SourceSkips, result.Problems.Take(256).Select(p => new SourceProblem(p.SourceRootId,
                Bounded(p.RelativePath, 4096), Bounded(p.Reason, 1024), p.Kind, p.NativeError)).ToImmutableArray(),
            result.VersionsCreated, result.Reactivations, result.Removals, result.Unchanged,
            result.FilesEnumerated, result.DirectoriesEnumerated, result.SourceBytesRead, result.PayloadBytesWritten,
            result.BatchesCommitted, result.Capacity.RemovalBatches, result.Capacity.VersionsRemoved, result.Capacity.LatestRetentionOutcome);
    }
    private static string Bounded(string value, int limit) => value.Length <= limit ? value : value[..limit];
}

public sealed record BackupAttemptResult(BackupOutcome Outcome, ScanSnapshot? Scan = null,
    ImpactDiagnostic? Impact = null, string? SafeSummary = null, bool CommittedProgressMayExist = false, MaintenanceSummary? Maintenance = null,
    RepositoryInspection? OpenedRepository = null, MetadataMaintenanceSummary? MetadataMaintenance = null)
{
    public bool TimelineChanged => CommittedProgressMayExist || Scan is { ReconciliationMayBeRequired: true } ||
        Scan is { BatchesCommitted: > 0 } or { CapacityRemovalBatches: > 0 } || Maintenance?.ChangesMayExist == true;
    public static BackupAttemptResult FromScan(ScanResult scan)
    {
        var outcome = scan.Outcome switch
        {
            ScanOutcome.Cancelled => BackupOutcome.Cancelled,
            ScanOutcome.CapacityBlocked or ScanOutcome.CapacityTargetImpossible or ScanOutcome.CapacityWorkLimitExceeded => BackupOutcome.CapacityBlocked,
            ScanOutcome.ControlError => BackupOutcome.ScanControlFailure,
            _ => scan.SourceSkips > 0 ? BackupOutcome.CompletedWithSourceProblems : BackupOutcome.Completed
        };
        return new(outcome, ScanSnapshot.FromResult(scan));
    }
}

public sealed record AttemptSnapshot(AttemptTrigger Trigger, DateTimeOffset? ScheduledUtc, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, TimeSpan Duration, BackupAttemptResult Result);

/// <summary>Complete immutable state. Revision lets consumers ignore stale/out-of-order notifications.</summary>
public sealed record BackupStatus(long Revision, SchedulerLifecycle Lifecycle, bool ScheduleEnabled,
    BackupSchedule Schedule, DateTimeOffset? NextScheduledUtc, bool IsAttemptActive, bool CancellationRequested, DateTimeOffset? ActiveStartedUtc,
    AttemptTrigger? ActiveTrigger, BackupProgress Progress, AttemptSnapshot? LastAttempt, DateTimeOffset? LastSuccessfulUtc,
    long SkippedCadences, SafeguardDecision LastSafeguard, DateTimeOffset? LastDeferredUtc)
{
    public bool RecoveryRequired => LastAttempt?.Result.Outcome == BackupOutcome.RecoveryRequired;
    public BackupOutcome? LastActivityOutcome => !LastSafeguard.Allowed ? BackupOutcome.SafeguardDeferred : LastAttempt?.Result.Outcome;
}

public interface IBackupAttemptRunner
{
    Task<BackupAttemptResult> RunAsync(BackupConfiguration configuration, AttemptTrigger trigger,
        Action<BackupProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Enter/Dispose must occur synchronously on the same dedicated worker. No await inside a scope.</summary>
public interface IWorkerImpact { IWorkerImpactScope Enter(); }
public interface IWorkerImpactScope : IDisposable { ImpactDiagnostic Diagnostic { get; } }
