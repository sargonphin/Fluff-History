using System.Collections.Immutable;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

public sealed record RestoreSummary(long Selected, long Restored, long Skipped, long Failed, bool Cancelled,
    bool SelectionComplete, long Conflicts, long BytesWritten, long ChecksumsVerified, long MtimeAdjustments,
    ImmutableArray<string> Problems);

/// <summary>Commands own a fresh unlocked repository through worker completion. Displayed selections require
/// the exact captured authority; exact version handles never substitute a newer identity.</summary>
public sealed class RestoreService
{
    internal Action<RestoreEngine>? ConfigureEngine { get; init; }
    public Task<RestoreSummary> RestoreExactAsync(string repositoryPath, BrowserVersion version, string destinationFile,
        ExistingTargetPolicy policy = ExistingTargetPolicy.FailIfExists, SourceSelection? originalSource = null,
        CancellationToken token = default) => Task.Run(() =>
    {
        try
        {
            token.ThrowIfCancellationRequested(); using var repository = ApplicationRepository.Open(repositoryPath);
            CheckAuthority(repository, version.Authority);
            var engine = new RestoreEngine(repository);
            ConfigureEngine?.Invoke(engine);
            return Summary(originalSource is null ? engine.RestoreFile(version.Identity, destinationFile, policy, token)
                : engine.RestoreFileInPlace(version.Identity, originalSource, policy, token));
        }
        catch (OperationCanceledException) { throw; }
        catch (ApplicationOperationException) { throw; }
        catch (Exception error) { throw ApplicationRepository.Failure(error); }
    });

    public Task<RestoreSummary> RestoreDisplayedAsync(string repositoryPath, BrowserSelection selection, string? destinationDirectory,
        SourceSelection? originalSource = null, RestoreOptions? options = null, CancellationToken token = default)
        => Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested(); using var repository = ApplicationRepository.Open(repositoryPath);
                CheckAuthority(repository, selection.Authority);
                var request = Request(selection, destinationDirectory, originalSource);
                var engine = new RestoreEngine(repository); ConfigureEngine?.Invoke(engine);
                return Summary(engine.RestoreDisplayed(request, options, cancellationToken: token));
            }
            catch (OperationCanceledException) { throw; }
            catch (ApplicationOperationException) { throw; }
            catch (Exception error) { throw ApplicationRepository.Failure(error); }
        });

    internal static void CheckAuthority(Repository repository, AuthorityIdentity authority)
    {
        using var reader = repository.OpenReader();
        if (reader.Authority != authority) throw new ApplicationOperationException(ApplicationFailure.RefreshRequired);
    }
    internal static DisplayedRestoreRequest Request(BrowserSelection selection, string? destination, SourceSelection? source, nint owner = 0)
        => selection.Request with { Items = selection.Request.Items.ToImmutableArray(), DestinationMode = source is null
            ? InteractiveRestoreDestination.AnotherFolder : InteractiveRestoreDestination.OriginalLocation,
            DestinationDirectory = destination, OriginalSource = source, OwnerWindow = owner };
    private static RestoreSummary Summary(RestoreResult result) => new(result.Selected, result.Restored, result.Skipped, result.Failed,
        result.Cancelled, result.SelectionComplete, result.SelectionConflicts, result.BytesWritten, result.ChecksumsVerified,
        result.MtimeAdjustedForChangeDetection, result.Problems.Take(256).Select(_ => "File restore failed; destination/content/metadata requires inspection.").ToImmutableArray());
}
