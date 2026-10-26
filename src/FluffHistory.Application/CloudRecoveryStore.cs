using System.Security.Cryptography;

namespace FluffHistory.Application;

public static class OneDriveRecoveryPermissions
{
    public const string Authority = "https://login.microsoftonline.com/consumers/";
    public const string Scope = "Files.ReadWrite.AppFolder";
    // MSAL's browser fallback only; WAM uses the registration's broker redirect.
    public const string RedirectUri = "http://localhost";
    public static void ValidateGrantedScopes(IEnumerable<string> scopes)
    {
        bool folder = false;
        foreach (var value in scopes)
        {
            var scope = value.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase)
                ? value["https://graph.microsoft.com/".Length..] : value;
            if (scope.Equals(Scope, StringComparison.OrdinalIgnoreCase)) { folder = true; continue; }
            if (scope is "openid" or "profile" or "offline_access") continue;
            throw new CloudRecoveryException(CloudRecoveryFailure.PermissionUnavailable);
        }
        if (!folder) throw new CloudRecoveryException(CloudRecoveryFailure.PermissionUnavailable);
    }
}

public enum CloudRecoveryFailure
{
    ConfigurationRequired, SignInCancelled, AuthenticationRequired, ConsentDenied, PermissionUnavailable,
    OneDriveUnavailable, ObjectNotFound, ObjectChanged, InvalidObject, NetworkUnavailable,
    Throttled, ServiceUnavailable, CleanupRequired
}

/// <summary>Sanitized semantic failure; never carries a token, URL or service response.</summary>
public sealed class CloudRecoveryException(CloudRecoveryFailure failure)
    : IOException("Cloud recovery operation failed: " + failure)
{
    public CloudRecoveryFailure Failure { get; } = failure;
}

/// <summary>Invocation-owned sensitive content. Do not log or put this in application status.</summary>
public sealed class CloudRecoveryItem(string itemId, string eTag, string name, byte[] ownedContent) : IDisposable
{
    public string ItemId { get; } = itemId;
    public string ETag { get; } = eTag;
    public string Name { get; } = name;
    public ReadOnlyMemory<byte> Content => _content ?? throw new ObjectDisposedException(nameof(CloudRecoveryItem));
    private byte[]? _content = ownedContent;
    public void Dispose()
    {
        var content = Interlocked.Exchange(ref _content, null);
        if (content is not null) CryptographicOperations.ZeroMemory(content);
    }
}

public enum CloudRecoveryDeleteResult { Deleted, AlreadyAbsent }

/// <summary>One authenticated invocation, limited to the application's OneDrive folder.
/// No listing, arbitrary paths, general Graph requests, or tokens enter the Application contract.</summary>
public interface ICloudRecoveryStore : IDisposable
{
    const int MaximumContentBytes = 16 * 1024;
    Task InspectAsync(CancellationToken token);
    Task<CloudRecoveryItem> CreateAsync(Guid repositoryId, Guid generationId, ReadOnlyMemory<byte> content, CancellationToken token);
    Task<CloudRecoveryItem> ReadExactAsync(string itemId, CancellationToken token);
    Task<CloudRecoveryDeleteResult> DeleteExactAsync(string itemId, string eTag, CancellationToken token);
}

public interface ICloudRecoveryStoreFactory
{
    bool IsConfigured { get; }
    // Explicit user action only. The Desktop implementation owns window-parented account selection.
    Task<ICloudRecoveryStore> OpenAsync(CancellationToken token);
}
