using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Application;

/// <summary>Deletes only the exact captured historical version. Shares backup/cleanup admission;
/// the confirmation UI owns no writer, and execution revalidates after confirmation.</summary>
public sealed class ExactVersionRemovalService(BackupOperationGate gate, string? workspace = null)
{
    public bool IsBusy => gate.IsBusy;
    internal Action<RepositoryWriter>? ConfigureWriter { get; init; }
    public Task<bool> IsCurrentAsync(string path, BrowserVersion version, CancellationToken token = default) => Task.Run(() =>
    {
        if (!gate.TryEnter()) throw new ApplicationOperationException(ApplicationFailure.Busy);
        try
        {
            token.ThrowIfCancellationRequested(); using var repository = ApplicationRepository.Open(path);
            using var reader = repository.OpenReader(); return Validate(reader, version);
        }
        catch (OperationCanceledException) { throw; }
        catch (ApplicationOperationException) { throw; }
        catch (Exception error) { throw ApplicationRepository.Failure(error); }
        finally { gate.Exit(); }
    });

    public Task<MaintenanceSummary> RemoveAsync(string path, BrowserVersion version, CancellationToken token = default) => Task.Run(() =>
    {
        if (!gate.TryEnter()) throw new ApplicationOperationException(ApplicationFailure.Busy);
        try
        {
            token.ThrowIfCancellationRequested(); using var repository = ApplicationRepository.Open(path);
            using var writer = repository.OpenWriter(workspace ?? BackupWorkspace.DefaultRoot, streamMetadataReads: true);
            using (var reader = repository.OpenReader())
                if (Validate(reader, version)) throw new ApplicationOperationException(ApplicationFailure.InvalidSelection);
            ConfigureWriter?.Invoke(writer);
            try { return Summary(writer.RemoveVersions([version.Identity], version.Authority, cancellationToken: token)); }
            catch (VersionRemovalCommittedException error) { return Summary(error.Result) with { State = MaintenanceState.NeedsAttention, ChangesMayExist = true }; }
            catch (PublicationUncertainException) { return new MaintenanceSummary(MaintenanceState.NeedsAttention, ChangesMayExist: true); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new MaintenanceSummary(MaintenanceState.Cancelled); }
        catch (ApplicationOperationException) { throw; }
        catch (Exception error) { throw ApplicationRepository.Failure(error); }
        finally { gate.Exit(); }
    });
    private static bool Validate(RepositoryReader reader, BrowserVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (reader.Authority != version.Authority || reader.GetVersion(version.Identity) is null)
            throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
        return reader.GetCurrent(version.Identity.Path)?.Identity == version.Identity;
    }
    private static MaintenanceSummary Summary(VersionRemovalResult result) => new(result.Cleanup.Cancelled ? MaintenanceState.Cancelled : MaintenanceState.Completed, result.Removed,
        result.RemovedLogicalBytes, result.Cleanup.Deferred, result.Cleanup.Failed, result.Removed > 0);
}
