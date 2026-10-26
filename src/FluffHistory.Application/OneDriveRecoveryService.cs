using System.Security.Cryptography;
using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum OneDriveRecoveryState { Off, Enabled, LegacyUpgradeRequired, ConfigurationRequired, Invalid, Unavailable }
public enum OneDriveRecoveryOutcome { Verified, Disabled, DisabledLocally, Failed, Cancelled, Uncertain }
[Flags]
public enum OneDriveRecoveryCleanup { None = 0, Remote = 1, Legacy = 2 }
public sealed record OneDriveRecoveryResult(OneDriveRecoveryOutcome Outcome,
    CloudRecoveryFailure? Failure = null, OneDriveRecoveryCleanup Cleanup = OneDriveRecoveryCleanup.None, string? CleanupFileName = null);
public sealed record CloudRecoveryCleanupReport(bool Required, string? FileName = null);

/// <summary>Cleanup only: never creates credentials, unwraps a key, or offers legacy recovery.</summary>
public interface ILegacyRecoveryCleanup
{
    Task RemoveAsync(string path, Guid repositoryId, CancellationToken token);
}

internal enum CloudRecoveryStage { BeforeAuthentication, AfterAppRoot, AfterUpload, AfterReadback, BeforePublication, AfterPublication, BeforeFinalVerification }

/// <summary>Explicit management operations under shared mutation admission. Desktop retires browser/
/// preview owners first. HTTP/auth never enters Storage, the Agent or ordinary repository opening.</summary>
public sealed class OneDriveRecoveryService(BackupOperationGate gate, ICloudRecoveryStoreFactory stores, ILegacyRecoveryCleanup? legacy = null)
{
    internal Func<string, Repository> OpenLocal { get; init; } = ApplicationRepository.Open;
    internal Action<CloudRecoveryStage>? Observed { get; init; }

    public Task<OneDriveRecoveryState> InspectAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (Repository.InspectOneDriveRecovery(path) is not null) return OneDriveRecoveryState.Enabled;
            if (File.Exists(Path.Combine(path, "microsoft-account-recovery.json"))) return OneDriveRecoveryState.LegacyUpgradeRequired;
            return stores.IsConfigured ? OneDriveRecoveryState.Off : OneDriveRecoveryState.ConfigurationRequired;
        }
        catch (InvalidDataException) { return OneDriveRecoveryState.Invalid; }
        catch (IOException) { return OneDriveRecoveryState.Unavailable; }
        catch (UnauthorizedAccessException) { return OneDriveRecoveryState.Unavailable; }
    }, token);

    public Task<OneDriveRecoveryResult> EnableAsync(string path, bool repair = false, CancellationToken token = default)
        => Run(async () =>
        {
            using var lease = gate.TryAcquire() ?? throw new ApplicationOperationException(ApplicationFailure.Busy);
            using var repository = await Task.Run(() => OpenLocal(path), token);
            var authority = await Task.Run(() => { using var initial = repository.OpenReader(); return initial.Authority; }, token);
            var previous = await Task.Run(() => Repository.InspectOneDriveRecovery(path), token);
            Observed?.Invoke(CloudRecoveryStage.BeforeAuthentication);
            using var store = await stores.OpenAsync(token);
            await store.InspectAsync(token);
            Observed?.Invoke(CloudRecoveryStage.AfterAppRoot);
            if (previous is not null && !repair)
            {
                await VerifyExact(store, path, previous, false, token);
                return new(OneDriveRecoveryOutcome.Verified, Cleanup: await CleanupLegacy(path, repository.Identity.RepositoryId, token));
            }
            using var secret = OneDriveRecoverySecret.Create(repository.Identity.RepositoryId);
            var content = secret.EncodeCloudObject(); CloudRecoveryItem? created = null;
            bool publicationStarted = false, uploadAttempted = false;
            try
            {
                uploadAttempted = true;
                created = await store.CreateAsync(secret.RepositoryId, secret.GenerationId, content, token);
                Observed?.Invoke(CloudRecoveryStage.AfterUpload);
                var reference = secret.Reference(created.ItemId, created.ETag);
                using (var readback = await store.ReadExactAsync(created.ItemId, token))
                {
                    if (!readback.Content.Span.SequenceEqual(content)) throw new CloudRecoveryException(CloudRecoveryFailure.InvalidObject);
                    // Independently parse and validate the actual returned cloud bytes.
                    using var checkedSecret = OneDriveRecoverySecret.ParseCloudObject(readback.Content.Span, reference);
                    reference = reference with { ETag = readback.ETag };
                }
                Observed?.Invoke(CloudRecoveryStage.AfterReadback);
                Observed?.Invoke(CloudRecoveryStage.BeforePublication);
                // Once publication can have happened, failure must never delete this cloud half blindly.
                publicationStarted = true;
                try
                {
                    await Task.Run(() => repository.PublishOneDriveRecovery(secret, reference, authority, previous?.GenerationId, token), token);
                }
                catch (OneDriveRecoveryUncertainException) { throw; }
                catch { publicationStarted = false; throw; }
                Observed?.Invoke(CloudRecoveryStage.AfterPublication);
                Observed?.Invoke(CloudRecoveryStage.BeforeFinalVerification);
                await VerifyExact(store, path, reference, false, token);
                var cleanup = OneDriveRecoveryCleanup.None;
                if (previous is not null && !await CleanupExact(store, previous, CancellationToken.None)) cleanup |= OneDriveRecoveryCleanup.Remote;
                cleanup |= await CleanupLegacy(path, repository.Identity.RepositoryId, token);
                return new(OneDriveRecoveryOutcome.Verified, Cleanup: cleanup,
                    CleanupFileName: cleanup.HasFlag(OneDriveRecoveryCleanup.Remote) ? previous?.FileName : null);
            }
            catch (Exception error)
            {
                if (publicationStarted)
                    return new(OneDriveRecoveryOutcome.Uncertain, Failure: Classify(error));
                // A lost upload response does not prove absence. Without a returned ID we cannot
                // delete safely; report the generated exact filename for possible manual cleanup.
                bool cleaned = created is null ? !uploadAttempted : await CleanupExact(store, secret.Reference(created.ItemId, created.ETag), CancellationToken.None);
                return new(error is OperationCanceledException ? OneDriveRecoveryOutcome.Cancelled : OneDriveRecoveryOutcome.Failed,
                    Classify(error), cleaned ? OneDriveRecoveryCleanup.None : OneDriveRecoveryCleanup.Remote,
                    cleaned ? null : $"recovery-{secret.RepositoryId:N}-{secret.GenerationId:N}.json");
            }
            finally { created?.Dispose(); CryptographicOperations.ZeroMemory(content); }
        });

    public Task<OneDriveRecoveryResult> VerifyAsync(string path, bool enroll = false, CancellationToken token = default)
        => Run(async () =>
        {
            using var lease = gate.TryAcquire() ?? throw new ApplicationOperationException(ApplicationFailure.Busy);
            var reference = await Task.Run(() => Repository.InspectOneDriveRecovery(path), token)
                ?? throw new CloudRecoveryException(CloudRecoveryFailure.ObjectNotFound);
            using var store = await stores.OpenAsync(token);
            await VerifyExact(store, path, reference, enroll, token);
            return new(OneDriveRecoveryOutcome.Verified, Cleanup: await CleanupLegacy(path, reference.RepositoryId, token));
        });

    /// <summary>Local-only disable requires explicit user confirmation in Desktop. It does not claim
    /// remote deletion. Normal disable never retires a changed remote object.</summary>
    public Task<OneDriveRecoveryResult> DisableAsync(string path, bool localOnly = false, CancellationToken token = default)
        => Run(async () =>
        {
            using var lease = gate.TryAcquire() ?? throw new ApplicationOperationException(ApplicationFailure.Busy);
            using var repository = await Task.Run(() => OpenLocal(path), token);
            var reference = await Task.Run(() => Repository.InspectOneDriveRecovery(path), token);
            if (reference is null) return new(OneDriveRecoveryOutcome.Disabled);
            bool cleaned = false;
            if (!localOnly)
            {
                using var store = await stores.OpenAsync(token);
                cleaned = await CleanupExact(store, reference, token);
                if (!cleaned) return new(OneDriveRecoveryOutcome.Failed, CloudRecoveryFailure.CleanupRequired, OneDriveRecoveryCleanup.Remote, reference.FileName);
            }
            await Task.Run(() => repository.RemoveOneDriveRecovery(reference.GenerationId, token), token);
            return new(localOnly ? OneDriveRecoveryOutcome.DisabledLocally : OneDriveRecoveryOutcome.Disabled,
                Cleanup: cleaned ? OneDriveRecoveryCleanup.None : OneDriveRecoveryCleanup.Remote,
                CleanupFileName: cleaned ? null : reference.FileName);
        });

    // Called by Application encryption orchestration under the same already-held gate.
    // Local retirement stays in the verified Storage transition; Graph failure never blocks conversion.
    internal async Task<CloudRecoveryCleanupReport> CleanupAfterDecryptionAsync(OneDriveRecoveryReference reference, CancellationToken token)
    {
        try
        {
            using var store = await stores.OpenAsync(token);
            return new(!await CleanupExact(store, reference, token), reference.FileName);
        }
        catch (Exception) { return new(true, reference.FileName); } // Local conversion already committed, including on auth cancellation.
    }
    private async Task<OneDriveRecoveryCleanup> CleanupLegacy(string path, Guid repositoryId, CancellationToken token)
    {
        if (legacy is not null)
        {
            try { await legacy.RemoveAsync(path, repositoryId, token); return OneDriveRecoveryCleanup.None; }
            catch (Exception) { return OneDriveRecoveryCleanup.Legacy; }
        }
        return File.Exists(Path.Combine(path, "microsoft-account-recovery.json")) ? OneDriveRecoveryCleanup.Legacy : OneDriveRecoveryCleanup.None;
    }
    private static async Task VerifyExact(ICloudRecoveryStore store, string path, OneDriveRecoveryReference reference,
        bool enroll, CancellationToken token)
    {
        using var remote = await store.ReadExactAsync(reference.ItemId, token);
        if (remote.ItemId != reference.ItemId) throw new CloudRecoveryException(CloudRecoveryFailure.ObjectChanged);
        using var secret = OneDriveRecoverySecret.ParseCloudObject(remote.Content.Span, reference);
        await Task.Run(() =>
        {
            if (Repository.InspectOneDriveRecovery(path) != reference) throw new CloudRecoveryException(CloudRecoveryFailure.ObjectChanged);
            using (var repository = Repository.OpenWithOneDriveRecovery(path, secret))
            {
                using var reader = repository.OpenReader(); _ = reader.Authority;
                token.ThrowIfCancellationRequested();
                if (enroll) repository.EnrollCurrentWindowsUser(token);
            }
            if (enroll)
            {
                using var local = Repository.OpenWithWindowsUser(path);
                using var fresh = local.OpenReader(); _ = fresh.Authority;
            }
        }, token);
    }

    private static async Task<bool> CleanupExact(ICloudRecoveryStore store, OneDriveRecoveryReference reference, CancellationToken token)
    {
        try
        {
            using var remote = await store.ReadExactAsync(reference.ItemId, token);
            if (remote.ItemId != reference.ItemId) return false;
            using var secret = OneDriveRecoverySecret.ParseCloudObject(remote.Content.Span, reference);
            _ = await store.DeleteExactAsync(reference.ItemId, remote.ETag, token); return true;
        }
        catch (CloudRecoveryException error) when (error.Failure == CloudRecoveryFailure.ObjectNotFound) { return true; }
        catch (Exception) { return false; } // Cleanup never broadens ownership, retries writes or damages a verified new slot.
    }
    private static CloudRecoveryFailure Classify(Exception error) => error switch
    {
        CloudRecoveryException cloud => cloud.Failure,
        OperationCanceledException => CloudRecoveryFailure.SignInCancelled,
        InvalidDataException => CloudRecoveryFailure.InvalidObject,
        WindowsUserProtectorUnavailableException or RepositoryLockedException => CloudRecoveryFailure.AuthenticationRequired,
        _ => CloudRecoveryFailure.ServiceUnavailable
    };
    private static async Task<OneDriveRecoveryResult> Run(Func<Task<OneDriveRecoveryResult>> operation)
    {
        try { return await operation(); }
        catch (ApplicationOperationException) { throw; }
        catch (OneDriveRecoveryUncertainException) { return new(OneDriveRecoveryOutcome.Uncertain); }
        catch (OwnershipEnrollmentUncertainException) { return new(OneDriveRecoveryOutcome.Uncertain); }
        catch (OperationCanceledException) { return new(OneDriveRecoveryOutcome.Cancelled); }
        catch (Exception error) { return new(OneDriveRecoveryOutcome.Failed, Classify(error)); }
    }
}
