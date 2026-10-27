using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum EncryptionChangeOutcome { Completed, Paused, MoreSpaceRequired, RecoveryRequired, NeedsAttention }
public enum EncryptionChangePhase { Preparing, CheckingSpace, EncryptingFiles, DecryptingFiles, RebuildingMetadata, Finalizing, Completed }
public sealed record EncryptionChangeProgress(EncryptionChangePhase Phase, long CompletedBytes, long TotalBytes, long CompletedFiles, long TotalFiles);
public sealed record EncryptionChangeResult(EncryptionChangeOutcome Outcome, bool CleanupDeferred = false,
    bool RemoteRecoveryCleanupRequired = false, string? RemoteCleanupFileName = null);

/// <summary>The management process owns conversion under shared mutation admission. UI closure cancels
/// bounded work, leaving durable resumable state. The background Agent never receives secrets or owns
/// an orphan conversion thread. Resume opens only explicit source/target maintenance representations.</summary>
public sealed class RepositoryEncryptionService(BackupOperationGate gate, ILegacyRecoveryCredentialCleanup protector, string workspace,
    OneDriveRecoveryService? cloudRecovery = null)
{
    internal EncryptionConversionOptions? Options { get; init; }
    public async Task<EncryptionChangeResult> ChangeAsync(string path, bool encrypted, char[]? ownedPassword,
        IProgress<EncryptionChangeProgress>? progress = null, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var lease = gate.TryAcquire() ?? throw new ApplicationOperationException(ApplicationFailure.Busy);
            var result = await Task.Run(() =>
            {
                try
                {
                    _ = Repository.ConvertEncryption(path, encrypted, ownedPassword ?? [], Options ?? new(workspace), protector,
                        new ProgressAdapter(progress), token);
                    bool deferred = false;
                    try
                    {
                        using var repository = ApplicationRepository.Open(path); using var writer = repository.OpenWriter(workspace, streamMetadataReads: true);
                        var cleanup = writer.CleanupRetiredMetadata(); deferred = cleanup.DeferredSegments != 0;
                    }
                    catch (Exception) { deferred = true; } // Exact target is already verified; cleanup never rolls back success.
                    return new EncryptionChangeResult(EncryptionChangeOutcome.Completed, deferred);
                }
                catch (ApplicationOperationException) { throw; }
                catch (EncryptionMigrationSpaceException) { return new(EncryptionChangeOutcome.MoreSpaceRequired); }
                catch (WindowsUserProtectorUnavailableException) { return new(EncryptionChangeOutcome.RecoveryRequired); }
                catch (OperationCanceledException) { return new(EncryptionChangeOutcome.Paused); }
                catch (Exception) { return new(EncryptionChangeOutcome.NeedsAttention); }
            });
            if (result.Outcome != EncryptionChangeOutcome.Completed || encrypted) return result;
            try
            {
                if (!Repository.HasPendingOneDriveCleanup(path)) return result;
                var remoteCleanup = cloudRecovery is not null
                    ? await cloudRecovery.CleanupAfterDecryptionAsync(path, token)
                    : new CloudRecoveryCleanupReport(true);
                return result with { RemoteRecoveryCleanupRequired = remoteCleanup.Required, RemoteCleanupFileName = remoteCleanup.FileName };
            }
            catch (Exception) { return result with { RemoteRecoveryCleanupRequired = true }; } // Committed local mode cannot be rolled back by bookkeeping failure.
        }
        catch (OperationCanceledException) { return new(EncryptionChangeOutcome.Paused); }
        finally { if (ownedPassword is not null) Array.Clear(ownedPassword); }
    }
    private sealed class ProgressAdapter(IProgress<EncryptionChangeProgress>? progress) : IProgress<EncryptionConversionProgress>
    {
        public void Report(EncryptionConversionProgress value) => progress?.Report(new(value.Phase switch
        {
            "Checking space" => EncryptionChangePhase.CheckingSpace, "Encrypting files" => EncryptionChangePhase.EncryptingFiles,
            "Decrypting files" => EncryptionChangePhase.DecryptingFiles, "Rebuilding metadata" => EncryptionChangePhase.RebuildingMetadata,
            "Finalizing backup" => EncryptionChangePhase.Finalizing, "Completed" => EncryptionChangePhase.Completed,
            _ => EncryptionChangePhase.Preparing
        }, value.CompletedBytes, value.TotalBytes, value.CompletedFiles, value.TotalFiles));
    }
}
