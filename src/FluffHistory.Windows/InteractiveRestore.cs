using System.Diagnostics;
using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Windows;

public enum ShellCopyOutcome { Copied, Skipped, Failed, Unconfirmed }
public sealed record ShellRestoreItem(VersionIdentity Identity, string RequestedPath, string? ActualPath,
    ShellCopyOutcome Outcome, int HResult, string? Warning);

/// <summary>Transient Shell outcomes; completed content is not rolled back by later failure or metadata warnings.</summary>
public sealed class InteractiveRestoreResult
{
    public AuthorityIdentity Authority { get; internal set; } = null!;
    public HistoryScope Scope { get; internal set; } = null!;
    public HistoryView View { get; internal set; } = null!;
    public int SelectedVisibleItems { get; internal set; }
    public int ExpandedFiles { get; internal set; }
    public long PlannedBytes { get; internal set; }
    public long PlanChargedBytes { get; internal set; }
    public long VerifiedPayloads { get; internal set; }
    public long VerificationBytes { get; internal set; }
    public long QueuedCopies { get; internal set; }
    public long PerformOperationsCount { get; internal set; }
    public long CopiesAttempted { get; internal set; }
    public long Copied { get; internal set; }
    public long CopiedPayloadBytes { get; internal set; }
    public long Skipped { get; internal set; }
    public long Failed { get; internal set; }
    public long Unconfirmed { get; internal set; }
    public long NoCompletionReported { get; internal set; }
    public bool OperationsAborted { get; internal set; }
    public bool CancellationRequested { get; internal set; }
    public int HResult { get; internal set; }
    public int AbortedQueryHResult { get; internal set; }
    public int FinishHResult { get; internal set; }
    public long IntegrityFailures { get; internal set; }
    public long CallbackFailures { get; internal set; }
    public long MetadataWarnings { get; internal set; }
    public long ChangeDetectionTimestampFailed { get; internal set; }
    public int PeakVerificationHandles { get; internal set; }
    public long VerificationElapsedTicks { get; internal set; }
    public long ShellElapsedTicks { get; internal set; }
    public long PlaintextStagingBytes { get; internal set; }
    public int PlaintextStagingFiles { get; internal set; }
    public long PlaintextStagingElapsedTicks { get; internal set; }
    private readonly List<ShellRestoreItem> _items = [];
    private readonly List<string> _problems = [];
    public IReadOnlyList<ShellRestoreItem> Items => _items.AsReadOnly();
    public IReadOnlyList<string> Problems => _problems.AsReadOnly();
    internal void Item(ShellRestoreItem item) { if (_items.Count < 256) _items.Add(item); }
    internal void Problem(string text) { if (_problems.Count < 256) _problems.Add(text); }
}

public sealed class InteractiveRestore(Repository repository)
{
    internal const uint ProductionFlags = 0; // Standard Shell conflict/error/progress UI; no automatic collision policy.
    public Task<InteractiveRestoreResult> RestoreAsync(DisplayedRestoreRequest request, RestorePlanLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Interactive restore requires Windows Shell.");
        return StaOperation.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var plan = new DisplayedRestorePlanner(repository).Plan(request, limits, cancellationToken);
            return Execute(plan, CreateNative, cancellationToken);
        });
    }
    private static IShellCopyOperation CreateNative()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new NativeShellCopyOperation();
    }

    public Task<InteractiveRestoreResult> RestoreExactAsync(AuthorityIdentity authority, VersionIdentity identity,
        InteractiveRestoreDestination mode, string? destinationDirectory = null, SourceSelection? originalSource = null,
        nint ownerWindow = 0, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Interactive restore requires Windows Shell.");
        return StaOperation.Run(() =>
        {
            using var plan = new DisplayedRestorePlanner(repository).PlanExact(authority, identity, mode, destinationDirectory, originalSource, ownerWindow, cancellationToken);
            return Execute(plan, CreateNative, cancellationToken);
        });
    }

    internal static InteractiveRestoreResult Execute(DisplayedRestorePlan plan, Func<IShellCopyOperation> createOperation,
        CancellationToken token = default, Action<string, DateTime>? setLastWrite = null, Func<DateTimeOffset>? clock = null)
    {
        var result = new InteractiveRestoreResult { Authority = plan.Reader.Authority, Scope = plan.Request.Scope, View = plan.Request.View,
            SelectedVisibleItems = plan.Request.Items.Count, ExpandedFiles = plan.Files.Count, PlannedBytes = plan.PlannedBytes, PlanChargedBytes = plan.ChargedBytes };
        using var staging = new PlaintextStaging();
        RestoreCallbacks? callbacks = null;
        try
        {
            // Planner proved every selected namespace/parent before any destination directory creation.
            token.ThrowIfCancellationRequested();
            IReadOnlyList<PlannedRestoreFile> files;
            var stagingStart = Stopwatch.GetTimestamp();
            try { files = staging.Materialize(plan, token); }
            finally
            {
                result.PlaintextStagingBytes = staging.BytesWritten; result.PlaintextStagingFiles = staging.FilesVerified;
                result.PlaintextStagingElapsedTicks = Stopwatch.GetTimestamp() - stagingStart;
            }
            callbacks = new RestoreCallbacks(plan, result, token, setLastWrite ?? File.SetLastWriteTimeUtc, clock ?? (() => DateTimeOffset.UtcNow), files);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                _ = RestoreDestination.Validate(file.DestinationPath, plan.Repository.DirectoryPath);
                RestoreDestination.CheckParents(Path.GetDirectoryName(file.DestinationPath)!, create: false);
                _ = RestoreDestination.TargetExists(file.DestinationPath);
            }
            foreach (var file in plan.Files) { token.ThrowIfCancellationRequested(); RestoreDestination.CheckParents(Path.GetDirectoryName(file.DestinationPath)!, create: true); }
            using var operation = createOperation(); operation.Configure(plan.Request.OwnerWindow, ProductionFlags, callbacks);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                RestoreDestination.CheckParents(Path.GetDirectoryName(file.DestinationPath)!, create: false);
                operation.Queue(file.PayloadPath, Path.GetDirectoryName(file.DestinationPath)!, Path.GetFileName(file.DestinationPath)); result.QueuedCopies++;
            }
            var start = Stopwatch.GetTimestamp(); result.PerformOperationsCount++;
            var execution = operation.Perform(); result.ShellElapsedTicks = Stopwatch.GetTimestamp() - start;
            result.HResult = execution.HResult; result.OperationsAborted = execution.Aborted;
            result.AbortedQueryHResult = execution.AbortedQueryHResult;
            if (execution.HResult < 0) result.Problem($"Shell operation failed: 0x{execution.HResult:X8}; completed copies remain.");
            if (execution.AbortedQueryHResult < 0) result.Problem("Shell aborted-state query failed; completion is not fully confirmed.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { result.CancellationRequested = true; result.OperationsAborted = true; result.HResult = RestoreCallbacks.Cancelled; }
        catch (Exception e)
        { result.HResult = e.HResult < 0 ? e.HResult : RestoreCallbacks.Failure; result.Problem(e.Message); }
        finally { result.CancellationRequested |= token.IsCancellationRequested; callbacks?.Summarize(); callbacks?.Dispose(); }
        return result;
    }
}

internal sealed class RestoreCallbacks : IShellCopyCallbacks, IDisposable
{
    internal const int Failure = unchecked((int)0x80004005), Cancelled = unchecked((int)0x800704C7);
    private readonly DisplayedRestorePlan _plan;
    private readonly InteractiveRestoreResult _result;
    private readonly CancellationToken _token;
    private readonly Action<string, DateTime> _setLastWrite;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, PlannedRestoreFile> _files;
    private readonly Dictionary<string, Stream> _active;
    private readonly HashSet<string> _reported, _completed;
    private readonly byte[] _buffer = new byte[128 * 1024];
    private readonly XxHash64 _hash = new();
    private readonly object _gate = new(); // Serialize sink state/readers even if a Shell extension dispatches callbacks concurrently.
    private bool _failed;
    internal RestoreCallbacks(DisplayedRestorePlan plan, InteractiveRestoreResult result, CancellationToken token,
        Action<string, DateTime> setLastWrite, Func<DateTimeOffset> clock, IReadOnlyList<PlannedRestoreFile>? files = null)
    {
        _plan = plan; _result = result; _token = token; _setLastWrite = setLastWrite; _clock = clock;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _files = (files ?? plan.Files).ToDictionary(f => PhysicalPaths.Normalize(f.PayloadPath), comparer); _active = new(comparer); _reported = new(comparer); _completed = new(comparer);
    }
    public int CheckCancellation() { lock (_gate) return CheckCancellationCore(); }
    private int CheckCancellationCore()
    {
        if (_token.IsCancellationRequested) { _result.CancellationRequested = true; return Cancelled; }
        return _failed ? Failure : 0;
    }
    private PlannedRestoreFile Correlate(string source, string destination)
    {
        if (!_files.TryGetValue(PhysicalPaths.Normalize(source), out var file) || !SamePath(destination, Path.GetDirectoryName(file.DestinationPath)!))
            throw new InvalidDataException("Shell callback does not match an exact planned payload/destination.");
        return file;
    }
    private static bool SamePath(string a, string b) => string.Equals(PhysicalPaths.Normalize(a), PhysicalPaths.Normalize(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public int PreCopy(string source, string destinationFolder) { lock (_gate) return PreCopyCore(source, destinationFolder); }
    private int PreCopyCore(string source, string destinationFolder)
    {
        try
        {
            if (CheckCancellation() is var cancelled && cancelled != 0) return cancelled;
            var file = Correlate(source, destinationFolder);
            source = PhysicalPaths.Normalize(file.PayloadPath);
            if (_completed.Contains(source)) throw new InvalidDataException("Shell attempted a completed item again.");
            RestoreDestination.CheckParents(destinationFolder, create: false);
            _ = RestoreDestination.Validate(file.DestinationPath, _plan.Repository.DirectoryPath);
            _ = RestoreDestination.TargetExists(file.DestinationPath);
            if (_active.ContainsKey(source)) return 0; // A repeated notification keeps the same immutable verified handle.
            if (_active.Count == 8) throw new IOException("Shell exceeded the active payload verification-handle bound.");
            _result.CopiesAttempted++;
            var start = Stopwatch.GetTimestamp();
            try
            {
                Stream payload;
                if (_plan.Repository.IsEncrypted)
                {
                    using var surviving = _plan.Reader.OpenVersionPayload(file.Version.Identity) ?? throw new InvalidDataException("Exact version no longer survives.");
                    RestoreDestination.CheckParents(Path.GetDirectoryName(source)!, create: false);
                    if (RestoreDestination.Attributes(source) is not { } attributes ||
                        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new IOException("Unsafe selected plaintext staging file.");
                    payload = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                else payload = (_plan.Reader.OpenVersionPayload(file.Version.Identity) ?? throw new InvalidDataException("Exact version no longer survives.")).Content;
                _active.Add(source, payload); _result.PeakVerificationHandles = Math.Max(_result.PeakVerificationHandles, _active.Count);
                _hash.Reset(); long bytes = 0;
                while (true)
                {
                    _token.ThrowIfCancellationRequested(); var count = payload.Read(_buffer);
                    _result.VerificationBytes += count; bytes += count;
                    if (bytes > file.Version.Metadata.Length) throw new InvalidDataException("Payload grew beyond recorded length.");
                    if (count == 0) break; _hash.Append(_buffer.AsSpan(0, count));
                }
                if (bytes != file.Version.Metadata.Length || Convert.ToHexString(_hash.GetCurrentHash()) != file.Version.Checksum.Value)
                    throw new InvalidDataException("Exact payload length/checksum verification failed.");
                _result.VerifiedPayloads++; return 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { _result.IntegrityFailures++; throw; }
            finally { _result.VerificationElapsedTicks += Stopwatch.GetTimestamp() - start; }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        { _result.CancellationRequested = true; Dispose(); return Cancelled; }
        catch (Exception e) { Fault(e); Dispose(); return Failure; }
    }
    public int PostCopy(string source, string destinationFolder, int hResult, string? actualPath)
    { lock (_gate) return PostCopyCore(source, destinationFolder, hResult, actualPath); }
    private int PostCopyCore(string source, string destinationFolder, int hResult, string? actualPath)
    {
        try
        {
            var file = Correlate(source, destinationFolder);
            source = PhysicalPaths.Normalize(file.PayloadPath);
            if (_completed.Contains(source)) throw new InvalidDataException("Duplicate completion notification.");
            // Windows can defer a collision/error to its later UI pass. This is not a
            // completed copy. Release this verification handle; a retry must verify again.
            if (hResult == 0x0027000B) return 0; // COPYENGINE_S_PENDING
            _reported.Add(source);
            if (hResult == 0x00270005) // COPYENGINE_S_USER_IGNORED: user-declined is determinable from this result.
            { _completed.Add(source); _result.Skipped++; _result.Item(new(file.Version.Identity, file.DestinationPath, actualPath, ShellCopyOutcome.Skipped, hResult, null)); return 0; }
            if (hResult < 0)
            { _result.Failed++; _result.Item(new(file.Version.Identity, file.DestinationPath, actualPath, ShellCopyOutcome.Failed, hResult, null)); return 0; }
            if (actualPath is null || !_active.ContainsKey(source))
            {
                _result.Unconfirmed++; _result.Item(new(file.Version.Identity, file.DestinationPath, actualPath, ShellCopyOutcome.Unconfirmed, hResult,
                    "Shell supplied no confirmed output or no matching verified PreCopy; no metadata was applied."));
                Fault(new InvalidDataException("Shell completion cannot be safely correlated.")); return Failure;
            }
            _completed.Add(source); _result.Copied++; _result.CopiedPayloadBytes += file.Version.Metadata.Length;
            if (!SamePath(Path.GetDirectoryName(actualPath)!, destinationFolder)) throw new IOException("Shell output escaped its permitted final folder.");
            _ = RestoreDestination.Validate(actualPath, _plan.Repository.DirectoryPath);
            RestoreDestination.CheckParents(destinationFolder, create: false);
            if (!RestoreDestination.TargetExists(actualPath)) throw new IOException("Shell output is absent or unsupported.");
            var warnings = new List<string>(); var original = _plan.Request.DestinationMode == InteractiveRestoreDestination.OriginalLocation;
            // Content is already visible. Each metadata step is independent, especially the change-detection timestamp.
            Apply(() => File.SetAttributes(actualPath, FileAttributes.Normal));
            if (OperatingSystem.IsWindows()) Apply(() => File.SetCreationTimeUtc(actualPath, file.Version.Metadata.CreationTimeUtc.UtcDateTime));
            try
            {
                var baseline = original ? _plan.Reader.GetCurrent(file.Version.Identity.Path) ?? _plan.Reader.GetNewestVersion(file.Version.Identity.Path) : null;
                RestoreTimestamps.Apply(actualPath, file.Version.Metadata.LastWriteTimeUtc, baseline?.Metadata.LastWriteTimeUtc,
                    original ? _clock() : null, _setLastWrite);
            }
            catch (Exception e)
            { warnings.Add(e.Message); if (original) _result.ChangeDetectionTimestampFailed++; }
            Apply(() => File.SetAttributes(actualPath, file.Version.Metadata.Attributes));
            if (warnings.Count != 0) _result.MetadataWarnings++;
            _result.Item(new(file.Version.Identity, file.DestinationPath, actualPath, ShellCopyOutcome.Copied, hResult,
                warnings.Count == 0 ? null : string.Join("; ", warnings)));
            return 0; // Metadata fidelity warnings must not abort independent later copies.
            void Apply(Action action) { try { action(); } catch (Exception e) { warnings.Add(e.Message); } }
        }
        catch (Exception e) { Fault(e); return Failure; }
        finally { if (_active.Remove(source, out var payload)) payload.Dispose(); }
    }
    public int Finish(int hResult) { lock (_gate) { _result.FinishHResult = hResult; Dispose(); return 0; } }
    internal void Summarize()
    {
        lock (_gate) SummarizeCore();
    }
    private void SummarizeCore()
    {
        _result.NoCompletionReported = _files.Count - _reported.Count;
        if (_result.PerformOperationsCount != 0 && _result.HResult >= 0 && !_result.OperationsAborted && _result.NoCompletionReported != 0)
        {
            _result.Unconfirmed += _result.NoCompletionReported;
            _result.Problem("Shell did not report completion for every queued item; no success is inferred for those items.");
        }
    }
    public void Fault(Exception exception)
    { lock (_gate) { _failed = true; _result.CallbackFailures++; _result.Problem(exception.Message); } }
    public void Dispose() { lock (_gate) { foreach (var payload in _active.Values) payload.Dispose(); _active.Clear(); } }
}
