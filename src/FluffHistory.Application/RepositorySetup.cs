using System.Collections.Immutable;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

public enum RepositoryReadiness { Missing, Ready, RecoveryRequired, Invalid, Unavailable, EncryptionMigrationRequired }
public sealed record EncryptionChangeState(bool SourceEncrypted, bool TargetEncrypted, string Phase);
public sealed record RepositoryInspection(RepositoryReadiness State, bool Encrypted = false, Guid? RepositoryId = null,
    ImmutableArray<SourceRootConfiguration> Sources = default)
{ public EncryptionChangeState? EncryptionChange { get; init; } }
public enum BackupLocationKind { Existing, Empty, Unrelated, Unavailable }
public sealed record BackupLocationInspection(BackupLocationKind Kind, RepositoryInspection Backup);
public enum RecoveryOutcome { Success, WrongPassword, EnrollmentUncertain, RepositoryInvalid, Cancelled, Failure }
public sealed record RecoveryResult(RecoveryOutcome Outcome, bool LocalOpenVerified = false);
public enum ApplicationFailure { RecoveryRequired, RefreshRequired, InvalidRepository, Unavailable, InvalidSelection, Cancelled, Busy, RestoreStructureConflict, EncryptionMigrationRequired }
public sealed class ApplicationOperationException(ApplicationFailure failure)
    : Exception($"Operation requires attention: {failure}.")
{ public ApplicationFailure Failure { get; } = failure; }

internal static class ApplicationRepository
{
    internal static Repository Open(string path)
    {
        using var discovered = Repository.Discover(path) ?? throw new DirectoryNotFoundException();
        return discovered.IsEncrypted ? Repository.OpenWithWindowsUser(path) : Repository.Open(path);
    }
    internal static ApplicationOperationException Failure(Exception error) => new(error switch
    {
        EncryptionMigrationRequiredException => ApplicationFailure.EncryptionMigrationRequired,
        WindowsUserProtectorUnavailableException => ApplicationFailure.RecoveryRequired,
        ReaderGenerationExpiredException or PayloadVersionExpiredException => ApplicationFailure.RefreshRequired,
        InvalidDataException or NotSupportedException => ApplicationFailure.InvalidRepository,
        OperationCanceledException => ApplicationFailure.Cancelled,
        RestoreStructureConflictException => ApplicationFailure.RestoreStructureConflict,
        ArgumentException or InvalidOperationException => ApplicationFailure.InvalidSelection,
        _ => ApplicationFailure.Unavailable
    });
    internal static ImmutableArray<SourceRootConfiguration> Sources(RepositoryReader reader, CancellationToken token)
    {
        var sources = ImmutableArray.CreateBuilder<SourceRootConfiguration>();
        foreach (var source in reader.EnumerateSourceRoots())
        { token.ThrowIfCancellationRequested(); if (sources.Count == 1024) throw new InvalidDataException(); sources.Add(source); }
        return sources.ToImmutable();
    }
}

/// <summary>Short-lived worker commands. Password arrays transfer ownership and are cleared on every exit,
/// including cancellation before the worker starts. No password is stored by this service.</summary>
public sealed class RepositorySetupService
{
    public async Task<BackupLocationInspection> InspectLocationAsync(string path, CancellationToken token = default)
    {
        var inspection = await InspectAsync(path, token).ConfigureAwait(false);
        if (inspection.State is RepositoryReadiness.Ready or RepositoryReadiness.RecoveryRequired or RepositoryReadiness.EncryptionMigrationRequired) return new(BackupLocationKind.Existing, inspection);
        return await Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(path)) return new BackupLocationInspection(BackupLocationKind.Unavailable, inspection);
                return new BackupLocationInspection(RepositoryDestinationPolicy.IsEffectivelyEmpty(path) ? BackupLocationKind.Empty : BackupLocationKind.Unrelated, inspection);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(BackupLocationKind.Unavailable, inspection); }
        }).ConfigureAwait(false);
    }
    internal Action<Repository>? BeforeEnrollment { get; init; }
    public Task<RepositoryInspection> InspectAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var discovered = Repository.Discover(path);
            if (discovered is null) return new RepositoryInspection(RepositoryReadiness.Missing);
            try
            {
                using var repository = ApplicationRepository.Open(path); using var reader = repository.OpenReader();
                return new RepositoryInspection(RepositoryReadiness.Ready, repository.IsEncrypted, repository.Identity.RepositoryId,
                    ApplicationRepository.Sources(reader, token));
            }
            catch (WindowsUserProtectorUnavailableException)
            { return new RepositoryInspection(RepositoryReadiness.RecoveryRequired, true, discovered.Identity.RepositoryId); }
        }
        catch (OperationCanceledException) { throw; }
        catch (EncryptionMigrationRequiredException error)
        { return MigrationInspection(error.Information); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
        { return new RepositoryInspection(RepositoryReadiness.Invalid); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new RepositoryInspection(RepositoryReadiness.Unavailable); }
    });

    internal static RepositoryInspection MigrationInspection(EncryptionMigrationInfo? information) =>
        new(RepositoryReadiness.EncryptionMigrationRequired, information?.SourceEncrypted ?? false)
        { EncryptionChange = information is null ? null : new(information.SourceEncrypted, information.TargetEncrypted, information.Phase) };

    public Task<RepositoryInspection> CreateAsync(string path, char[]? ownedRecoveryPassword = null, CancellationToken token = default)
        => Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var repository = ownedRecoveryPassword is null ? Repository.Create(path, token)
                    : Repository.CreateEncrypted(path, ownedRecoveryPassword, useWindowsUserProtector: true, token);
                return new RepositoryInspection(RepositoryReadiness.Ready, repository.IsEncrypted, repository.Identity.RepositoryId, []);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw ApplicationRepository.Failure(error); }
            finally { if (ownedRecoveryPassword is not null) Array.Clear(ownedRecoveryPassword); }
        });

    public Task<RecoveryResult> RecoverAsync(string path, char[] ownedPassword, CancellationToken token = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(ownedPassword);
            var uncertain = false;
            try
            {
                token.ThrowIfCancellationRequested();
                using (var repository = Repository.OpenWithPassword(path, ownedPassword))
                {
                    token.ThrowIfCancellationRequested(); BeforeEnrollment?.Invoke(repository);
                    try { repository.EnrollCurrentWindowsUser(token); }
                    catch (OwnershipEnrollmentUncertainException) { uncertain = true; }
                }
                // Once replacement may have happened, always inspect a fresh local open even if cancellation arrived.
                using var verified = Repository.OpenWithWindowsUser(path); using var reader = verified.OpenReader();
                _ = reader.Authority;
                return new RecoveryResult(uncertain ? RecoveryOutcome.EnrollmentUncertain : RecoveryOutcome.Success, true);
            }
            catch (RecoveryPasswordAuthenticationException) { return new(RecoveryOutcome.WrongPassword); }
            catch (OperationCanceledException) { return new(RecoveryOutcome.Cancelled); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            { return new(uncertain ? RecoveryOutcome.EnrollmentUncertain : RecoveryOutcome.RepositoryInvalid); }
            catch (Exception) { return new(uncertain ? RecoveryOutcome.EnrollmentUncertain : RecoveryOutcome.Failure); }
            finally { Array.Clear(ownedPassword); }
        });
}
