using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum ExperimentalRecoveryState { Off, Available, NotAvailable, Unavailable, Invalid, CleanupRequired, Legacy, ConfigurationRequired, AvailableWithCleanup }
public enum SecurityOperationOutcome { Success, NotAvailable, AuthenticationFailed, Unavailable, Uncertain, Cancelled }
public enum RepositorySecurityOperation { ChangePassword, EnableRoaming, DisableRoaming, RecoverRoaming, VerifyRoaming, RepairRoaming, DisableRoamingLocally, RetryCloudCleanup }
public sealed record SecurityOperationResult(SecurityOperationOutcome Outcome,
    CloudRecoveryFailure? CloudFailure = null, OneDriveRecoveryCleanup Cleanup = OneDriveRecoveryCleanup.None, string? CleanupFileName = null);

/// <summary>Local password maintenance only. OneDrive operations have their own async semantic service.</summary>
public sealed class RepositorySecurityService(BackupOperationGate gate)
{
    public async Task<SecurityOperationResult> ChangePasswordAsync(string path, char[] ownedPassword, CancellationToken token = default)
    {
        try
        {
            // Invocation owns the array even when the caller already cancelled.
            return await Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var lease = gate.TryAcquire() ?? throw new ApplicationOperationException(ApplicationFailure.Busy);
                    using var repository = ApplicationRepository.Open(path);
                    repository.ChangeRecoveryPassword(ownedPassword, token); return new SecurityOperationResult(SecurityOperationOutcome.Success);
                }
                catch (ApplicationOperationException) { throw; }
                catch (RecoveryPasswordChangeUncertainException) { return new(SecurityOperationOutcome.Uncertain); }
                catch (OperationCanceledException) { return new(SecurityOperationOutcome.Cancelled); }
                catch (Exception) { return new(SecurityOperationOutcome.Unavailable); }
            }).ConfigureAwait(false);
        }
        finally { Array.Clear(ownedPassword); }
    }
}
