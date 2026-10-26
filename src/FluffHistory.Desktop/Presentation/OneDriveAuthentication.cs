using FluffHistory.Application;
using FluffHistory.Windows;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Publisher-owned public application metadata; this ID is not a credential.
/// Personal-account registration uses the WAM broker redirect and AppFolder scope.</summary>
internal static class OneDriveApplicationRegistration
{
    internal static string ClientId => "9ffb1f5d-107e-4f78-a4e5-b2fed2958562";
}

internal sealed class OneDriveRecoveryStoreFactory(string? clientId, Func<nint> ownerWindow) : ICloudRecoveryStoreFactory
{
    private IPublicClientApplication? _application;
    public bool IsConfigured => Guid.TryParseExact(clientId, "D", out var id) && id != Guid.Empty;

    public async Task<ICloudRecoveryStore> OpenAsync(CancellationToken token)
    {
        if (!IsConfigured) throw new CloudRecoveryException(CloudRecoveryFailure.ConfigurationRequired);
        token.ThrowIfCancellationRequested();
        var hwnd = ownerWindow();
        if (hwnd == 0) throw new CloudRecoveryException(CloudRecoveryFailure.AuthenticationRequired);
        try
        {
            _application ??= PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(OneDriveRecoveryPermissions.Authority)
                .WithRedirectUri(OneDriveRecoveryPermissions.RedirectUri)
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .Build();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            // Explicit account selection: the Windows sign-in account need not own this recovery object.
            // WAM owns the account dialog/cache; no custom cache persistence or PII logging.
            var result = await _application.AcquireTokenInteractive([OneDriveRecoveryPermissions.Scope])
                .WithParentActivityOrWindow(hwnd).WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(deadline.Token);
            OneDriveRecoveryPermissions.ValidateGrantedScopes(result.Scopes);
            return GraphAppFolderStore.Create(result.AccessToken);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CloudRecoveryException(CloudRecoveryFailure.SignInCancelled); }
        catch (MsalClientException error)
        {
            throw new CloudRecoveryException(error.ErrorCode is "authentication_canceled" or "user_canceled"
                ? CloudRecoveryFailure.SignInCancelled : CloudRecoveryFailure.AuthenticationRequired);
        }
        catch (MsalServiceException error)
        {
            throw new CloudRecoveryException(error.ErrorCode is "access_denied" or "consent_required"
                ? CloudRecoveryFailure.ConsentDenied : CloudRecoveryFailure.AuthenticationRequired);
        }
    }
}
