using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Windows;

public sealed record ShellRestoreSummary(long Copied, long Skipped, long Failed, long Unconfirmed,
    bool Cancelled, long MetadataWarnings, ImmutableArray<string> Problems);

/// <summary>Desktop composition only: repository/key owner outlives the existing async STA leaf. No UI Dispatcher.</summary>
public sealed class WindowsHistoryActions
{
    internal IHistoricalFileLauncher? PreviewLauncher { get; init; }
    internal Func<Repository, DisplayedRestoreRequest, CancellationToken, Task<InteractiveRestoreResult>>? ShellLeaf { get; init; }
    public async Task<ShellRestoreSummary> RestoreExactAsync(string repositoryPath, BrowserVersion version,
        string? destinationDirectory, nint ownerWindow, SourceSelection? originalSource = null, CancellationToken token = default)
    {
        try
        {
            using var repository = await Task.Run(() => { token.ThrowIfCancellationRequested(); return ApplicationRepository.Open(repositoryPath); }).ConfigureAwait(false);
            await Task.Run(() => RestoreService.CheckAuthority(repository, version.Authority)).ConfigureAwait(false);
            var result = await new InteractiveRestore(repository).RestoreExactAsync(version.Authority, version.Identity,
                originalSource is null ? InteractiveRestoreDestination.AnotherFolder : InteractiveRestoreDestination.OriginalLocation,
                destinationDirectory, originalSource, ownerWindow, token).ConfigureAwait(false);
            return new(result.Copied, result.Skipped, result.Failed, result.Unconfirmed, result.CancellationRequested || result.OperationsAborted,
                result.MetadataWarnings, result.Problems.Take(256).Select(_ => "Shell restore requires destination inspection.").ToImmutableArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (ApplicationOperationException) { throw; }
        catch (Exception error) { throw ApplicationRepository.Failure(error); }
    }
    public async Task<ShellRestoreSummary> RestoreAsync(string repositoryPath, BrowserSelection selection,
        string? destinationDirectory, nint ownerWindow, SourceSelection? originalSource = null, CancellationToken token = default)
    {
        try
        {
            using var repository = await Task.Run(() => { token.ThrowIfCancellationRequested(); return ApplicationRepository.Open(repositoryPath); }).ConfigureAwait(false);
            await Task.Run(() => RestoreService.CheckAuthority(repository, selection.Authority)).ConfigureAwait(false);
            var request = RestoreService.Request(selection, destinationDirectory, originalSource, ownerWindow);
            var result = await (ShellLeaf is null ? new InteractiveRestore(repository).RestoreAsync(request, cancellationToken: token)
                : ShellLeaf(repository, request, token)).ConfigureAwait(false);
            return new(result.Copied, result.Skipped, result.Failed, result.Unconfirmed, result.CancellationRequested || result.OperationsAborted,
                result.MetadataWarnings, result.Problems.Take(256).Select(_ => "Shell restore requires destination inspection.").ToImmutableArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (ApplicationOperationException) { throw; }
        catch (Exception error) { throw ApplicationRepository.Failure(error); }
    }

    public async Task<PreviewLease> PreviewAsync(string repositoryPath, BrowserVersion version, nint ownerWindow, CancellationToken token = default)
    {
        try
        {
            using var repository = await Task.Run(() => { token.ThrowIfCancellationRequested(); return ApplicationRepository.Open(repositoryPath); }).ConfigureAwait(false);
            await Task.Run(() => RestoreService.CheckAuthority(repository, version.Authority)).ConfigureAwait(false);
            if (PreviewLauncher is not null)
                return await StaOperation.Run(() => new HistoricalPreview(repository).Open(version.Identity, PreviewLauncher,
                    Path.Combine(Path.GetTempPath(), "Fluff-History", "Previews"), ownerWindow, token)).ConfigureAwait(false);
            return await new HistoricalPreview(repository).OpenAsync(version.Identity, ownerWindow, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ApplicationOperationException) { throw; }
        catch (Exception error) { throw ApplicationRepository.Failure(error); }
    }
}
