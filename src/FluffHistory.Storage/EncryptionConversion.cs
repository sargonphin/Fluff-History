using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using FluffHistory.Core;

namespace FluffHistory.Storage;

public sealed record EncryptionConversionProgress(string Phase, bool TargetEncrypted, long CompletedBytes,
    long TotalBytes, long CompletedFiles, long TotalFiles);
public sealed record EncryptionConversionResult(bool Encrypted, string SemanticDigest, long Versions, long LogicalBytes,
    long SourcePhysicalBytes, long TargetPhysicalBytes)
{ public bool PostCommitAuthorityAdvanced { get; init; } }
public sealed record EncryptionConversionOptions(string? WorkspaceRoot = null, MaintenanceLimits? Limits = null)
{
    internal Func<string, CapacityObservation>? CapacityProbe { get; init; }
    internal long? AllocationUnit { get; init; }
    internal Action<EncryptionConversionStage>? Observed { get; init; }
}
internal enum EncryptionConversionStage
{
    BootstrapPrepared, MarkerCandidateFlushed, MarkerInstalled, BeforePayload, PayloadWritten,
    PayloadFlushed, BeforePayloadReplace, AfterPayloadReplace, PayloadsCompleted, BeforeMetadata,
    MetadataSegmentCreated, MetadataFlushed, TargetStatePrepared, BeforeBootstrapPivot, AfterBootstrapPivot, BeforeStatePivot,
    AfterStatePivot, FinalVerified, BeforeMarkerRemoval, AfterMarkerRemoval,
    BeforeFence, AfterFence, BeforeReady, ReadyRecorded
}

public sealed partial class Repository
{
    public static EncryptionMigrationInfo? InspectEncryptionMigration(string directory)
    {
        directory = RepositoryLayout.NormalizeDirectory(directory);
        return EncryptionMigrationControl.Exists(directory) ? EncryptionMigrationControl.Read(directory).Information : null;
    }

    /// <summary>Exclusive, resumable physical maintenance. Password is a new target password when
    /// enabling encryption, or recovery of an existing source/target wrapper on resume. Closing/cancellation
    /// pauses, never rolls back converted payloads. No password or master is sent to another process.</summary>
    public static EncryptionConversionResult ConvertEncryption(string directory, bool encrypted,
        ReadOnlySpan<char> recoveryPassword = default, EncryptionConversionOptions? options = null,
        ILegacyRecoveryCredentialCleanup? legacyRecoveryCleanup = null,
        IProgress<EncryptionConversionProgress>? progress = null, CancellationToken token = default)
    {
        options ??= new(); token.ThrowIfCancellationRequested();
        directory = RepositoryLayout.NormalizeDirectory(directory);
        var workspace = RepositoryLayout.NormalizeDirectory(options.WorkspaceRoot ?? Path.GetTempPath());
        if (workspace == directory || workspace.StartsWith(directory + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Conversion workspace must be outside the backup.", nameof(options));
        var existing = EncryptionMigrationControl.Exists(directory) ? EncryptionMigrationControl.Read(directory) : null;
        if (existing is not null && existing.TargetEncrypted != encrypted) throw new InvalidOperationException("Complete the active encryption conversion before requesting another direction.");
        if (existing?.Phase == "Ready") return CompleteEncryptionConversion(directory, existing, recoveryPassword, options, progress, token);
        var sourceBytes = existing is null ? AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")) : Convert.FromBase64String(existing.SourceBootstrap);
        var sourceBootstrap = RepositoryBootstrap.Parse(sourceBytes);
        if (sourceBootstrap.Encrypted == encrypted) throw new InvalidOperationException("The backup already uses the requested encryption mode.");
        var sourceState = existing is null ? AuthorityFiles.ReadSmall(Path.Combine(directory, "state.json")) : Convert.FromBase64String(existing.SourceState);
        using var source = new Repository(directory, sourceBootstrap, UnlockMaintenance(sourceBootstrap, recoveryPassword), sourceState);
        using var writer = source.OpenWriter(workspace, streamMetadataReads: true);
        if (existing?.Phase == "Preparing")
        {
            var liveBootstrap = AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json"));
            if (!liveBootstrap.AsSpan().SequenceEqual(existing.FenceBytes()))
            {
                var live = RepositoryBootstrap.Parse(liveBootstrap);
                Format.Require((live with { WindowsUserKey = null }) == (sourceBootstrap with { WindowsUserKey = null }), "pre-fence bootstrap changed");
                sourceBytes = liveBootstrap; sourceBootstrap = live;
                sourceState = AuthorityFiles.ReadSmall(Path.Combine(directory, "state.json"));
                source._maintenanceState = sourceState;
            }
        }
        using var sourceReader = source.OpenReader(); // Authenticates the captured source root even after a partial pivot.
        if (existing?.Phase == "Preparing")
        {
            existing = existing with { SourceBootstrap = Convert.ToBase64String(sourceBytes), SourceState = Convert.ToBase64String(sourceState),
                SemanticDigest = SemanticDigest(sourceReader, token) };
            existing.Install(directory, false);
        }
        if (existing is null)
        {
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")).AsSpan().SequenceEqual(sourceBytes) &&
                AuthorityFiles.ReadSmall(Path.Combine(directory, "state.json")).AsSpan().SequenceEqual(sourceState), "conversion initial predecessor changed");
            writer.RequireConversionCleanBoundary();
        }
        RepositoryBootstrap targetBootstrap;
        if (existing is not null) targetBootstrap = RepositoryBootstrap.Parse(Convert.FromBase64String(existing.TargetBootstrap));
        else if (!encrypted) targetBootstrap = RepositoryBootstrap.Plain(source.Identity);
        else
        {
            var created = RepositoryBootstrap.Create(source.Identity, recoveryPassword, OperatingSystem.IsWindows());
            created.Crypto.Dispose(); targetBootstrap = created.Bootstrap;
        }
        using var target = new Repository(directory, targetBootstrap, UnlockMaintenance(targetBootstrap, recoveryPassword), []);
        if (existing is not null && encrypted && OperatingSystem.IsWindows())
        {
            try
            {
                using var local = targetBootstrap.UnlockWindows();
                var check = target.Crypto!.Protect("conversion-key-continuity"u8, "conversion-key-test");
                Format.Require(local.Unprotect(check, "conversion-key-test").AsSpan().SequenceEqual("conversion-key-continuity"u8), "resumed current-user target master");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
            {
                writer.ValidateConversionControl(existing);
                // The target header has not been exposed yet. Rewrap the authenticated same target master.
                targetBootstrap = targetBootstrap with { WindowsUserKey = target.Crypto!.ProtectCurrentWindowsUser() };
                using var confirmed = targetBootstrap.UnlockWindows();
                var challenge = target.Crypto.Protect("conversion-key-continuity"u8, "conversion-key-test");
                Format.Require(confirmed.Unprotect(challenge, "conversion-key-test").AsSpan().SequenceEqual("conversion-key-continuity"u8), "resumed target DPAPI enrollment");
                existing = existing with { TargetBootstrap = Convert.ToBase64String(targetBootstrap.Bytes()) };
                existing.Install(directory, false);
            }
        }
        // A new encrypted target is checked through both portable password and current-user DPAPI before any marker/write.
        if (existing is null && encrypted)
        {
            using var portable = targetBootstrap.Unlock(recoveryPassword);
            if (OperatingSystem.IsWindows())
            {
                using var windows = targetBootstrap.UnlockWindows();
                var challenge = target.Crypto!.Protect("conversion-key-continuity"u8, "conversion-key-test");
                Format.Require(windows.Unprotect(challenge, "conversion-key-test").AsSpan().SequenceEqual("conversion-key-continuity"u8) &&
                    portable.Unprotect(challenge, "conversion-key-test").AsSpan().SequenceEqual("conversion-key-continuity"u8), "conversion target key continuity");
            }
        }
        var unit = options.AllocationUnit ?? source.ObserveAllocationGeometry(workspace).AllocationUnit;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unit);
        var workspaceProbe = workspace;
        while (!Directory.Exists(workspaceProbe)) workspaceProbe = Path.GetDirectoryName(workspaceProbe) ?? throw new IOException("No workspace ancestor.");
        var inventory = Inventory(sourceReader, source, target, unit, token);
        var assessment = writer.AssessMetadataForEncryption(encrypted, options.Limits, token);
        var limits = options.Limits ?? new();
        // Reject unknown/corrupt cache layout before publishing a marker or changing any representation.
        if (existing is null) writer.RetireConversionCaches(limits, token, validateOnly: true);
        CapacityObservation Probe(string path) => options.CapacityProbe?.Invoke(path) ?? RepositoryCapacity.ObserveDirectory(path);
        void Space(long bytes, bool includeWorkspace = false)
        {
            var allocated = AllocationGeometry.RoundUp(bytes, unit);
            var capacity = new CapacityTarget(Probe(directory), allocated);
            if (!capacity.Satisfied) throw new EncryptionMigrationSpaceException(capacity.RequiredFree);
            if (includeWorkspace)
            {
                var temporary = new CapacityTarget(Probe(workspaceProbe), allocated);
                if (!temporary.Satisfied) throw new EncryptionMigrationSpaceException(temporary.RequiredFree);
            }
        }
        progress?.Report(new("Checking space", encrypted, 0, inventory.LogicalBytes, 0, inventory.Versions));
        // No compression credit, no pressure-retention deletion. Full scratch budget is charged deliberately.
        var preparation = checked(assessment.MaximumOutputBytes + limits.MaximumScratchBytes + 4L * EncryptionMigrationControl.MaximumBytes + 1024 * unit);
        Space(checked(Math.Max(0, inventory.TargetAllocated - inventory.ActualAllocated) + inventory.LargestCandidate + preparation), true);
        var digest = SemanticDigest(sourceReader, token);
        var control = existing ?? new EncryptionMigrationControl("Fluff-History", 1, Guid.NewGuid(), source.Identity.RepositoryId,
            source.IsEncrypted, encrypted, Guid.NewGuid(), checked((int)(assessment.DenseDecodedBytes /
                (Format.MaximumSegmentBytes - Format.MaximumFrameBytes - Format.FrameHeaderBytes - Format.SealBytes) + 2)), Convert.ToBase64String(sourceBytes), Convert.ToBase64String(targetBootstrap.Bytes()),
            Convert.ToBase64String(sourceState), null, digest, "Preparing", "", inventory.Versions, inventory.LogicalBytes, inventory.SourcePhysical, inventory.TargetPhysical);
        if (control.Phase == "Preparing") control = control with
        {
            MaximumSegments = checked((int)(assessment.DenseDecodedBytes /
                (Format.MaximumSegmentBytes - Format.MaximumFrameBytes - Format.FrameHeaderBytes - Format.SealBytes) + 2)),
            VersionCount = inventory.Versions, LogicalBytes = inventory.LogicalBytes,
            SourcePhysicalBytes = inventory.SourcePhysical, TargetPhysicalBytes = inventory.TargetPhysical
        };
        Format.Require(control.SemanticDigest == digest && control.TargetEpoch != sourceReader.Captured.Root.Epoch, "conversion captured semantic identity");
        options.Observed?.Invoke(EncryptionConversionStage.BootstrapPrepared);
        token.ThrowIfCancellationRequested();
        if (existing is null)
        {
            control.Install(directory, initial: true, () => { options.Observed?.Invoke(EncryptionConversionStage.MarkerCandidateFlushed); token.ThrowIfCancellationRequested(); });
            options.Observed?.Invoke(EncryptionConversionStage.MarkerInstalled);
        }
        else if (control.Phase == "Preparing") control.Install(directory, false);
        writer.ValidateConversionControl(control);
        if (control.Phase == "Preparing")
        {
            // Unsupported ordinary mode is a durable fence for binaries predating this marker.
            Pivot("repository.json", Convert.FromBase64String(control.SourceBootstrap), control.FenceBytes(),
                EncryptionConversionStage.BeforeFence, EncryptionConversionStage.AfterFence);
            control = control with { Phase = "Payloads" }; control.Install(directory, false);
        }
        writer.RetireConversionCaches(limits, token);
        var clock = Stopwatch.StartNew(); long lastReport = -1000;
        void Report(string phase, long bytes, long files, bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastReport < 250) return;
            lastReport = clock.ElapsedMilliseconds;
            progress?.Report(new(phase, encrypted, bytes, inventory.LogicalBytes, files, inventory.Versions));
        }
        if (control.Phase is "Payloads" or "Metadata")
        {
            long bytes = 0, files = 0;
            Report(encrypted ? "Encrypting files" : "Decrypting files", 0, 0, true);
            foreach (var version in Surviving(sourceReader))
            {
                token.ThrowIfCancellationRequested(); writer.ValidateConversionControl(control);
                options.Observed?.Invoke(EncryptionConversionStage.BeforePayload);
                Space(checked(target.EstimatePayloadPhysicalBytes(version.Metadata.Length) + 2L * EncryptionMigrationControl.MaximumBytes));
                ConvertPayload(source, target, version, control, options,
                    copied => Report(encrypted ? "Encrypting files" : "Decrypting files", checked(bytes + copied), files), token);
                bytes = checked(bytes + version.Metadata.Length); files++;
                Report(encrypted ? "Encrypting files" : "Decrypting files", bytes, files);
            }
            options.Observed?.Invoke(EncryptionConversionStage.PayloadsCompleted);
            Report("Rebuilding metadata", bytes, files, force: true);
            control = control with { Phase = "Metadata" }; control.Install(directory, false);
            Space(preparation, true);
            options.Observed?.Invoke(EncryptionConversionStage.BeforeMetadata);
            var state = writer.BuildConvertedMetadata(target, control, limits, options, token);
            options.Observed?.Invoke(EncryptionConversionStage.MetadataFlushed);
            target._maintenanceState = state;
            using (var targetReader = target.OpenReader())
                Format.Require(SemanticDigest(targetReader, token) == digest, "converted metadata semantic equivalence");
            control = control with { TargetState = Convert.ToBase64String(state), Phase = "Pivot" };
            control.Install(directory, false);
            options.Observed?.Invoke(EncryptionConversionStage.TargetStatePrepared);
        }
        target._maintenanceState = Convert.FromBase64String(control.TargetState ?? throw Format.Invalid("missing converted state"));
        using (var reader = target.OpenReader())
        {
            Format.Require(SemanticDigest(reader, token) == digest, "prepared target semantic equivalence");
            foreach (var version in Surviving(reader)) VerifyPayload(target, version, target.GetPayloadPath(version.Identity.VersionId), token);
        }
        // Source wrappers/sidecar remain recoverable until every target object has been verified.
        if (!encrypted && (ExperimentalRecoverySidecar.Exists(directory) || legacyRecoveryCleanup?.Exists(RecoveryCredentialCoordinate.ForRepository(source.Identity.RepositoryId)) == true))
        {
            if (legacyRecoveryCleanup is null) throw new IOException("Legacy recovery must be retired before removing encryption.");
            writer.RetireLegacyMicrosoftRecoveryForConversion(control, legacyRecoveryCleanup, token);
        }
        // Cloud cleanup is Application-owned; a remote orphan never blocks verified physical conversion.
        // Retire the non-authoritative local half only after all target objects are proven.
        if (!encrypted && OneDriveRecoverySidecar.Exists(directory)) writer.RetireOneDriveRecoveryForConversion(control, token);
        token.ThrowIfCancellationRequested(); writer.ValidateConversionControl(control);
        Space(4L * EncryptionMigrationControl.MaximumBytes);
        Report("Finalizing backup", inventory.LogicalBytes, inventory.Versions, true);
        // Keep the unsupported header fence throughout the state pivot and full final verification.
        Pivot("state.json", sourceState, target._maintenanceState,
            EncryptionConversionStage.BeforeStatePivot, EncryptionConversionStage.AfterStatePivot);
        // Fresh explicit target instance, independent of the source/key owner. No ordinary hybrid read path.
        using (var verified = new Repository(directory, targetBootstrap, UnlockMaintenance(targetBootstrap, recoveryPassword), target._maintenanceState))
        using (var reader = verified.OpenReader())
        {
            Format.Require(AuthorityFiles.ReadSmall(Path.Combine(directory, "repository.json")).AsSpan().SequenceEqual(control.FenceBytes()) &&
                AuthorityFiles.ReadSmall(Path.Combine(directory, "state.json")).AsSpan().SequenceEqual(target._maintenanceState) &&
                SemanticDigest(reader, token) == digest, "converted final controls/semantics");
            foreach (var version in Surviving(reader)) VerifyPayload(verified, version, verified.GetPayloadPath(version.Identity.VersionId), token);
        }
        options.Observed?.Invoke(EncryptionConversionStage.FinalVerified);
        writer.ValidateConversionControl(control);
        options.Observed?.Invoke(EncryptionConversionStage.BeforeReady);
        control = control with { Phase = "Ready" };
        control = control with { CompletionProof = Convert.ToBase64String((source.Crypto ?? target.Crypto)!
            .Protect(control.CompletionBinding(), "state", control.CompletionContext())) };
        control.Install(directory, false);
        options.Observed?.Invoke(EncryptionConversionStage.ReadyRecorded);
        // Reuse the held writer. Recovery enters the same completion path before opening source metadata.
        var completion = CompleteEncryptionConversionOwned(target, writer, control, source.Crypto ?? target.Crypto!, recoveryPassword, options, token);
        Report("Completed", inventory.LogicalBytes, inventory.Versions, true);
        return completion;

        void Pivot(string destination, byte[] prior, byte[] intended, EncryptionConversionStage before, EncryptionConversionStage after)
        {
            writer.ValidateConversionControl(control);
            var actual = AuthorityFiles.ReadSmall(Path.Combine(directory, destination));
            if (actual.AsSpan().SequenceEqual(intended)) return;
            Format.Require(actual.AsSpan().SequenceEqual(prior), "unrecognized conversion pivot predecessor");
            var name = "encryption-pivot-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                AuthorityFiles.WriteNew(Path.Combine(directory, name), intended);
                options.Observed?.Invoke(before);
                try
                {
                    AuthorityFiles.Replace(directory, name, destination); options.Observed?.Invoke(after);
                }
                catch
                {
                    actual = AuthorityFiles.ReadSmall(Path.Combine(directory, destination));
                    if (!actual.AsSpan().SequenceEqual(intended)) throw;
                }
                Format.Require(AuthorityFiles.ReadSmall(Path.Combine(directory, destination)).AsSpan().SequenceEqual(intended), "conversion pivot readback");
            }
            finally
            {
                try { File.Delete(Path.Combine(directory, name)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static RepositoryCryptography? UnlockMaintenance(RepositoryBootstrap bootstrap, ReadOnlySpan<char> password)
    {
        if (!bootstrap.Encrypted) return null;
        if (!password.IsEmpty) return bootstrap.Unlock(password);
        try { return bootstrap.UnlockWindows(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or PlatformNotSupportedException)
        { if (password.IsEmpty) throw new WindowsUserProtectorUnavailableException(error); return bootstrap.Unlock(password); }
    }
    private sealed record ConversionInventory(long Versions, long LogicalBytes, long ActualPhysical, long TargetPhysical, long LargestCandidate, long ActualAllocated, long TargetAllocated, long SourcePhysical);
    private static ConversionInventory Inventory(RepositoryReader reader, Repository source, Repository target, long unit, CancellationToken token)
    {
        long count = 0, logical = 0, actual = 0, desired = 0, largest = 0, actualAllocated = 0, desiredAllocated = 0, expectedSource = 0;
        foreach (var version in Surviving(reader))
        {
            token.ThrowIfCancellationRequested();
            var path = source.GetPayloadPath(version.Identity.VersionId); RepositoryLayout.RequireFile(path);
            var length = new FileInfo(path).Length;
            Format.Require(length == source.EstimatePayloadPhysicalBytes(version.Metadata.Length) || length == target.EstimatePayloadPhysicalBytes(version.Metadata.Length), "migration payload representation length");
            var required = target.EstimatePayloadPhysicalBytes(version.Metadata.Length);
            count++; logical = checked(logical + version.Metadata.Length); actual = checked(actual + length); desired = checked(desired + required); largest = Math.Max(largest, required);
            actualAllocated = checked(actualAllocated + AllocationGeometry.RoundUp(length, unit)); desiredAllocated = checked(desiredAllocated + AllocationGeometry.RoundUp(required, unit));
            expectedSource = checked(expectedSource + source.EstimatePayloadPhysicalBytes(version.Metadata.Length));
        }
        return new(count, logical, actual, desired, largest, actualAllocated, desiredAllocated, expectedSource);
    }
    private static IEnumerable<StoredVersion> Surviving(RepositoryReader reader)
    {
        foreach (var entry in reader.VersionIndex.Range(reader.Captured.Root.Versions, null, null))
            yield return reader.Version(entry.Value, versionKey: entry.Key);
    }
    internal static string SemanticDigest(RepositoryReader reader, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(byte type, byte[] bytes)
        {
            token.ThrowIfCancellationRequested(); Span<byte> header = stackalloc byte[9]; header[0] = type;
            BinaryPrimitives.WriteInt64LittleEndian(header[1..], bytes.Length); hash.AppendData(header); hash.AppendData(bytes);
        }
        Add(0, AuthorityFiles.HeaderBytes(reader.Captured.Repository));
        var sequence = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(sequence, reader.Captured.Root.Sequence); Add(4, sequence);
        foreach (var source in reader.EnumerateSourceRoots()) Add(1, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: source)));
        foreach (var version in reader.EnumerateCurrent()) Add(2, PathKeys.Version(version.Identity));
        foreach (var version in Surviving(reader)) Add(3, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, null, Version: version)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ConvertPayload(Repository source, Repository target, StoredVersion version, EncryptionMigrationControl control,
        EncryptionConversionOptions options, Action<long> copiedProgress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = source.GetPayloadPath(version.Identity.VersionId);
        var candidate = path + ".migration-" + control.OperationId.ToString("N") + ".tmp";
        if (new FileInfo(path).Length == target.EstimatePayloadPhysicalBytes(version.Metadata.Length))
        { VerifyPayload(target, version, path, token); DeleteCandidate(); return; }
        try
        {
            DeleteCandidate();
            using (var input = PayloadInput.Open(path))
            using (var plain = source.IsEncrypted ? new EncryptedPayload.Reader(source, version.Identity.VersionId, input, version.Metadata.Length) : (Stream)input)
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            using (var encryptedOutput = target.IsEncrypted ? new EncryptedPayload.Writer(target, version.Identity.VersionId, output) : null)
            {
                Format.Require(input.Length == source.EstimatePayloadPhysicalBytes(version.Metadata.Length) &&
                    (File.GetAttributes(input.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) == 0,
                    "conversion source ordinary payload/length");
                var checksum = new XxHash64(); var buffer = new byte[EncryptedPayload.ChunkBytes]; long copied = 0;
                try
                {
                    int count;
                    while ((count = plain.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested(); copied = checked(copied + count);
                        Format.Require(copied <= version.Metadata.Length, "migration source payload length"); checksum.Append(buffer.AsSpan(0, count));
                        (encryptedOutput ?? (Stream)output).Write(buffer.AsSpan(0, count));
                        copiedProgress(copied);
                    }
                    Format.Require(copied == version.Metadata.Length && Convert.ToHexString(checksum.GetCurrentHash()) == version.Checksum.Value, "migration source payload checksum");
                    encryptedOutput?.Complete(version.Metadata.Length);
                    options.Observed?.Invoke(EncryptionConversionStage.PayloadWritten);
                    output.Flush(true);
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
            options.Observed?.Invoke(EncryptionConversionStage.PayloadFlushed);
            VerifyPayload(target, version, candidate, token);
            token.ThrowIfCancellationRequested();
            Format.Require(EncryptionMigrationControl.Read(source.DirectoryPath).Bytes().AsSpan().SequenceEqual(control.Bytes()), "migration payload control changed");
            options.Observed?.Invoke(EncryptionConversionStage.BeforePayloadReplace);
            try
            {
                AuthorityFiles.Replace(Path.GetDirectoryName(path)!, Path.GetFileName(candidate), Path.GetFileName(path));
                options.Observed?.Invoke(EncryptionConversionStage.AfterPayloadReplace);
            }
            catch
            {
                // Exact physical length distinguishes representations; full authentication/checksum decides validity.
                if (new FileInfo(path).Length != target.EstimatePayloadPhysicalBytes(version.Metadata.Length)) { VerifyPayload(source, version, path); throw; }
                VerifyPayload(target, version, path);
            }
            VerifyPayload(target, version, path);
        }
        finally { DeleteCandidate(); }
        void DeleteCandidate()
        {
            try { RepositoryLayout.RequireFile(candidate); File.Delete(candidate); }
            catch (FileNotFoundException) { }
        }
    }
    private static void VerifyPayload(Repository repository, StoredVersion version, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var file = PayloadInput.Open(path);
        Format.Require(file.Length == repository.EstimatePayloadPhysicalBytes(version.Metadata.Length) &&
            (File.GetAttributes(file.SafeFileHandle) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Encrypted)) == 0, "conversion ordinary payload/length");
        using var plain = repository.IsEncrypted ? new EncryptedPayload.Reader(repository, version.Identity.VersionId, file, version.Metadata.Length) : (Stream)file;
        var checksum = new XxHash64(); var buffer = new byte[EncryptedPayload.ChunkBytes]; long length = 0;
        try
        {
            int count;
            while ((count = plain.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); length = checked(length + count); checksum.Append(buffer.AsSpan(0, count)); }
            Format.Require(length == version.Metadata.Length && Convert.ToHexString(checksum.GetCurrentHash()) == version.Checksum.Value, "converted payload checksum/length");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}

public sealed partial class RepositoryWriter
{
    internal void RetireConversionCaches(MaintenanceLimits limits, CancellationToken token, bool validateOnly = false)
    {
        Check(); RequireNoCandidates(); RequireIdle();
        long names = 0;
        var ready = Path.Combine(ReuseRoot, "ready");
        if (RepositoryLayout.IsExistingDirectory(ready))
        {
            SafeDirectory(ready);
            foreach (var shard in Directory.EnumerateFileSystemEntries(ready))
            {
                token.ThrowIfCancellationRequested(); if (++names > limits.MaximumRecords) throw new IOException("Conversion cache budget exceeded.");
                SafeDirectory(shard);
                foreach (var bucket in Directory.EnumerateFileSystemEntries(shard))
                {
                    token.ThrowIfCancellationRequested(); if (++names > limits.MaximumRecords) throw new IOException("Conversion cache budget exceeded.");
                    SafeDirectory(bucket);
                    foreach (var name in Directory.EnumerateFileSystemEntries(bucket))
                    {
                        token.ThrowIfCancellationRequested(); if (++names > limits.MaximumRecords) throw new IOException("Conversion cache budget exceeded.");
                        var descriptor = ReadReusable(name);
                        Format.Require(descriptor.State == ReusablePayloadState.Ready && name == ReadyName(descriptor), "conversion exact Ready cache ownership");
                        if (!validateOnly)
                        {
                            _ = RemoveReadyReusablePayload(new(descriptor));
                            try { _ = File.GetAttributes(name); throw new IOException("Conversion Ready retirement incomplete."); }
                            catch (FileNotFoundException) { }
                            catch (DirectoryNotFoundException) { }
                        }
                    }
                }
            }
        }
        var bound = Path.Combine(ReuseRoot, "bound");
        if (RepositoryLayout.IsExistingDirectory(bound))
        {
            SafeDirectory(bound);
            foreach (var transaction in Directory.EnumerateFileSystemEntries(bound))
            {
                token.ThrowIfCancellationRequested(); if (++names > limits.MaximumRecords) throw new IOException("Conversion cache budget exceeded.");
                SafeDirectory(transaction);
                foreach (var name in Directory.EnumerateFileSystemEntries(transaction))
                {
                    token.ThrowIfCancellationRequested(); if (++names > limits.MaximumRecords) throw new IOException("Conversion cache budget exceeded.");
                    var descriptor = ReadReusable(name);
                    Format.Require(descriptor.State == ReusablePayloadState.TransactionBound && name == BoundName(descriptor), "conversion exact bound cache ownership");
                    if (!validateOnly) File.Delete(name);
                }
            }
        }
        if (validateOnly) return;
        PayloadCleanupCursor? cursor = null; long records = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            var remaining = limits.MaximumRecords - records;
            if (remaining <= 0) throw new IOException("Conversion retired-payload cleanup budget exceeded.");
            var page = CleanupRetiredPayloads(new(remaining), cursor, token); records += page.RecordsRead; cursor = page.Continuation;
            if (page.Result.Deferred != 0 || page.Result.Failed != 0 || page.Result.Cancelled)
                throw new IOException("An already-retired payload could not be removed before conversion.");
        } while (cursor is not null);
    }
    internal void RequireConversionCleanBoundary() { Check(); RequireNoCandidates(); RequireIdle(); }
    internal void ValidateConversionControl(EncryptionMigrationControl control)
    {
        Check();
        Format.Require(EncryptionMigrationControl.Read(_repository.DirectoryPath).Bytes().AsSpan().SequenceEqual(control.Bytes()), "conversion control changed");
        var bootstrap = AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, "repository.json"));
        var state = AuthorityFiles.ReadSmall(Path.Combine(_repository.DirectoryPath, "state.json"));
        Format.Require((bootstrap.AsSpan().SequenceEqual(control.FenceBytes()) || control.Phase == "Preparing" && bootstrap.AsSpan().SequenceEqual(Convert.FromBase64String(control.SourceBootstrap))) &&
            (state.AsSpan().SequenceEqual(Convert.FromBase64String(control.SourceState)) || control.Phase == "Pivot" && control.TargetState is not null &&
                state.AsSpan().SequenceEqual(Convert.FromBase64String(control.TargetState))), "unrecognized conversion authority combination");
    }
    internal byte[] BuildConvertedMetadata(Repository target, EncryptionMigrationControl control, MaintenanceLimits limits, EncryptionConversionOptions options, CancellationToken token)
    {
        ValidateConversionControl(control);
        // Every possible output name is reserved by this random operation/epoch before first creation.
        // Cleanup touches only those exact derived candidates, never arbitrary metadata names.
        for (long ordinal = 1; ordinal <= control.MaximumSegments; ordinal++)
        {
            token.ThrowIfCancellationRequested(); var path = SegmentReader.SegmentPath(_repository.DirectoryPath, control.SegmentId(ordinal));
            try
            {
                RepositoryLayout.RequireFile(path);
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length >= Format.SegmentHeaderBytes)
                    {
                        var bytes = new byte[Format.SegmentHeaderBytes]; input.ReadExactly(bytes); var header = MetadataCodec.DecodeSegment(bytes);
                        Format.Require(header.RepositoryId == control.RepositoryId && header.Epoch == control.TargetEpoch && header.Ordinal == ordinal &&
                            header.SegmentId == control.SegmentId(ordinal), "conversion candidate segment ownership");
                    }
                }
                File.Delete(path);
            }
            catch (FileNotFoundException) { }
        }
        using var workspace = new MetadataWorkspace(_repository, _workspaceRoot, token, streamMetadataReads: true) { MaximumMaterializedBytes = limits.MaximumMaterializedBytes };
        using var baseline = _repository.OpenWorkingReader(workspace);
        using var fold = new SemanticHistoryFold(baseline, workspace, limits, token, HistoryFoldBufferEntries);
        using var append = new SegmentAppender(_repository.DirectoryPath, _repository.Identity, control.TargetEpoch, null, crypto: target.Crypto)
            { MaximumOutputBytes = limits.MaximumOutputBytes, SegmentIdentity = control.SegmentId,
                SegmentCreated = () => options.Observed?.Invoke(EncryptionConversionStage.MetadataSegmentCreated) };
        var root = BuildDenseBase(baseline, fold, control.TargetEpoch, append.Append, limits, token);
        var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
        ValidateConversionControl(control);
        return AuthorityFiles.StateBytes(new(_repository.Identity, root, address), target.Crypto);
    }
}
