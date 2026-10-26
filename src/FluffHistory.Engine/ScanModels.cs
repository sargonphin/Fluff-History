using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Engine;

/// <summary>An explicit runtime location for a configured logical root; absolute location is never path identity.</summary>
public sealed record SourceSelection(SourceRootConfiguration Configuration, string DirectoryPath, IReadOnlyList<string>? ExcludedFiles = null,
    IReadOnlyList<string>? SafetyExcludedDirectories = null, SourceMetadataExclusions? MetadataExclusions = null);

/// <summary>Transient Windows policy supplied by the native adapter; never persisted in history.</summary>
public sealed record SourceMetadataExclusions(IReadOnlyList<string> ManagedShortcutFolders, IReadOnlyList<string> OneDriveFolders)
{
    internal bool Excludes(string path, FileAttributes attributes)
    {
        const FileAttributes compatibility = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System;
        if ((attributes & compatibility) == compatibility) return true;
        if ((attributes & FileAttributes.Directory) != 0) return false;
        var name = Path.GetFileName(path);
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
            ((attributes & FileAttributes.System) != 0 || ManagedShortcutFolders.Any(folder => Within(path, folder)))) return true;
        return OneDriveFolders.Any(folder => Within(path, folder)) &&
            (name.Equals(".849C9593-D756-4E56-8D6E-42412F2A707B", StringComparison.OrdinalIgnoreCase) ||
             (attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System));
    }
    private static bool Within(string path, string folder) => path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record ScanOptions(int BatchSize = 128, int MaximumSourceAttempts = 2,
    int MaximumStateEntries = 1_000_000, long MaximumStateBytes = 256 * 1024 * 1024, string? WorkspaceRoot = null,
    long MaximumPendingPayloadBytes = 64 * 1024 * 1024, ScanCapacityOptions? Capacity = null)
{
    internal void Validate()
    {
        if (BatchSize is < 1 or > MutationBatch.MaximumChanges || MaximumSourceAttempts is < 1 or > 4 ||
            MaximumStateEntries < 1 || MaximumStateBytes < 1 || MaximumPendingPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(ScanOptions));
        Capacity?.Validate();
    }
}

public enum SourceProblemKind { Access, UnsupportedName, AmbiguousAlias, UnsupportedBoundary, Oversized, SourceChanged, RootBoundary, DirectoryBoundary }
public sealed record SourceProblem(Guid SourceRootId, string RelativePath, string Reason, SourceProblemKind Kind = SourceProblemKind.Access, int? NativeError = null);

/// <summary>Transient operation counters, never repository history. Repository/control failures throw.</summary>
public sealed class ScanResult
{
    public ScanOutcome Outcome { get; internal set; }
    public bool CapacityBlocked => Outcome is ScanOutcome.CapacityBlocked or ScanOutcome.CapacityTargetImpossible or ScanOutcome.CapacityWorkLimitExceeded;
    public bool Partial => Outcome != ScanOutcome.Completed && (BatchesCommitted != 0 || Capacity.RemovalBatches != 0);
    public bool Cancelled { get; internal set; }
    public bool ReconciliationMayBeRequired { get; internal set; }
    public AuthorityIdentity Authority { get; internal set; } = null!;
    public AuthorityIdentity? ComparisonAuthority { get; internal set; }
    public long WriterAcquisitions { get; internal set; }
    public long CurrentComparisonScans { get; internal set; }
    public long HistoricalReaderRefreshes { get; internal set; }
    public long PeakPendingPayloadCount { get; internal set; }
    public long PeakPendingPayloadBytes { get; internal set; }
    public long MetadataBytesAppended { get; internal set; }
    public ScanCapacityStatistics Capacity { get; } = new();
    public ReusablePayloadStatistics Reuse { get; internal set; } = new();
    /// <summary>Logical lengths of adopted exact reuse hits, whose source content streams were not opened.</summary>
    public long SourceContentBytesAvoided { get; internal set; }
    public long FilesEnumerated { get; internal set; }
    public long DirectoriesEnumerated { get; internal set; }
    public long ComparisonEntries { get; internal set; }
    public long ComparisonBytesRequested { get; internal set; }
    public long HistoryMetadataBytesRequested { get; internal set; }
    /// <summary>Instrumented comparison, lookup and capacity-selection reads; excludes writer workspaces/control I/O.</summary>
    public long MetadataBytesRequested => checked(ComparisonBytesRequested + HistoryMetadataBytesRequested + Capacity.MetadataBytesRequested);
    public long HistoricalLookups { get; internal set; }
    public long Unchanged { get; internal set; }
    public long ContentStreamsOpened { get; internal set; }
    public long SourceBytesRead { get; internal set; }
    public long PayloadBytesWritten { get; internal set; }
    public long VersionsCreated { get; internal set; }
    public long Reactivations { get; internal set; }
    public long Removals { get; internal set; }
    public long BatchesCommitted { get; internal set; }
    public long SourceSkips { get; internal set; }
    public long RetryAttempts { get; internal set; }
    public long BudgetEntriesCharged { get; internal set; }
    public long BudgetBytesCharged { get; internal set; }
    private readonly List<SourceProblem> _problems = [];
    public IReadOnlyList<SourceProblem> Problems => _problems.AsReadOnly();
    internal void Skip(Guid root, string path, string reason, SourceProblemKind kind = SourceProblemKind.Access, int? nativeError = null)
    {
        SourceSkips++;
        if (_problems.Count < 256) _problems.Add(new(root, path, reason, kind, nativeError));
    }
    internal void Skip(Guid root, string path, Exception error, SourceProblemKind kind)
    {
        int? code = null;
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is System.ComponentModel.Win32Exception native) { code = native.NativeErrorCode; break; }
            if (OperatingSystem.IsWindows() && ((uint)cause.HResult & 0xFFFF0000u) == 0x80070000u) code ??= cause.HResult & 0xFFFF;
        }
        Skip(root, path, error.Message, kind, code);
    }
}

/// <summary>Safety budget exhausted. Earlier batches remain committed; incomplete root coverage cannot cause deletion.</summary>
public sealed class ScanLimitExceededException : InvalidOperationException
{
    internal ScanLimitExceededException() : base("Transient current/observation safety budget exceeded. No deletion is inferred from incomplete coverage.") { }
}

internal sealed class ScanBudget(ScanOptions options, ScanResult result)
{
    internal void Charge(int characters, int overhead = 128)
    {
        var bytes = checked(overhead + 2L * characters);
        if (result.BudgetEntriesCharged >= options.MaximumStateEntries || bytes > options.MaximumStateBytes - result.BudgetBytesCharged)
            throw new ScanLimitExceededException();
        result.BudgetEntriesCharged++; result.BudgetBytesCharged += bytes;
    }
}
