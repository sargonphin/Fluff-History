using Windows.Security.Credentials;
using FluffHistory.Application;
using FluffHistory.Storage;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Exact legacy cleanup only. Never enumerates, reads a secret or creates a credential.</summary>
internal sealed class DesktopLegacyRecoveryCleanup : ILegacyRecoveryCredentialCleanup, ILegacyRecoveryCleanup
{
    private static PasswordCredential? Find(RecoveryCredentialCoordinate coordinate)
    {
        if (coordinate.Name != "FluffHistory" || !coordinate.Resource.StartsWith("FluffHistory-Recovery-v1-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(coordinate.Resource[25..], "N", out var id) || coordinate != RecoveryCredentialCoordinate.ForRepository(id))
            throw new ArgumentException("Invalid legacy recovery coordinate.", nameof(coordinate));
        try { return new PasswordVault().Retrieve(coordinate.Resource, coordinate.Name); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { return null; }
    }
    public bool Exists(RecoveryCredentialCoordinate coordinate) => Find(coordinate) is not null;
    public void Remove(RecoveryCredentialCoordinate coordinate)
    {
        if (Find(coordinate) is { } credential) new PasswordVault().Remove(credential);
        if (Exists(coordinate)) throw new IOException("Legacy recovery credential removal was not confirmed.");
    }
    public Task RemoveAsync(string path, Guid repositoryId, CancellationToken token) => Task.Run(() =>
    {
        using var repository = Repository.OpenWithWindowsUser(path);
        if (repository.Identity.RepositoryId != repositoryId) throw new InvalidDataException("Legacy cleanup repository changed.");
        repository.RemoveLegacyMicrosoftRecovery(this, token);
    }, token);
}
