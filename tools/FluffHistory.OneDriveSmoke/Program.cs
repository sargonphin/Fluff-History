using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluffHistory.Application;
using FluffHistory.Windows;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

// Explicit manual diagnostic only. Not in the solution, installer, normal tests or hosted CI.
if (!OperatingSystem.IsWindows() || args.Length != 5 || args[0] != "--run-live" ||
    args[1] != "--client-id" || !Guid.TryParseExact(args[2], "D", out var clientId) || clientId == Guid.Empty ||
    args[3] != "--evidence" || !Path.IsPathFullyQualified(args[4]))
{
    Console.Error.WriteLine("Opt-in Windows diagnostic: --run-live --client-id <registered public client GUID> --evidence <absolute JSON path>");
    return 2;
}
var evidence = new Dictionary<string, object>
{
    ["scope"] = OneDriveRecoveryPermissions.Scope, ["startedUtc"] = DateTimeOffset.UtcNow,
    ["authenticated"] = false, ["appFolder"] = false, ["exactReadback"] = false,
    ["deleted"] = false, ["confirmedAbsent"] = false, ["cleanupRequired"] = false
};
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline.Cancel(); };
var random = RandomNumberGenerator.GetBytes(32);
var content = JsonSerializer.SerializeToUtf8Bytes(new { synthetic = Convert.ToBase64String(random) });
GraphAppFolderStore? store = null; CloudRecoveryItem? created = null;
try
{
    var hwnd = Native.GetAncestor(Native.GetConsoleWindow(), 3);
    if (hwnd == 0) throw new CloudRecoveryException(CloudRecoveryFailure.AuthenticationRequired);
    var app = PublicClientApplicationBuilder.Create(clientId.ToString("D"))
        .WithAuthority(OneDriveRecoveryPermissions.Authority)
        .WithRedirectUri(OneDriveRecoveryPermissions.RedirectUri)
        .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)).Build();
    var auth = await app.AcquireTokenInteractive([OneDriveRecoveryPermissions.Scope])
        .WithParentActivityOrWindow(hwnd).WithPrompt(Prompt.SelectAccount).ExecuteAsync(deadline.Token);
    OneDriveRecoveryPermissions.ValidateGrantedScopes(auth.Scopes);
    evidence["authenticated"] = true;
    store = GraphAppFolderStore.Create(auth.AccessToken);
    try { await store.InspectAsync(deadline.Token); }
    catch (CloudRecoveryException error) when (error.Failure == CloudRecoveryFailure.InvalidObject)
    {
        // Diagnose only the documented fixed endpoint's shape, never its values or raw body.
        await InspectRootShape(auth.AccessToken, evidence, deadline.Token);
        throw;
    }
    evidence["appFolder"] = true;
    created = await store.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), content, deadline.Token);
    using (var readback = await store.ReadExactAsync(created.ItemId, deadline.Token))
    {
        if (!readback.Content.Span.SequenceEqual(content)) throw new CloudRecoveryException(CloudRecoveryFailure.InvalidObject);
        evidence["exactReadback"] = true; evidence["sizeBytes"] = content.Length;
        evidence["syntheticSha256"] = Convert.ToHexString(SHA256.HashData(content));
        _ = await store.DeleteExactAsync(readback.ItemId, readback.ETag, deadline.Token); evidence["deleted"] = true;
    }
    try { using var unexpected = await store.ReadExactAsync(created.ItemId, deadline.Token); }
    catch (CloudRecoveryException error) when (error.Failure == CloudRecoveryFailure.ObjectNotFound)
    { evidence["confirmedAbsent"] = true; }
    return (bool)evidence["confirmedAbsent"] ? 0 : 1;
}
catch (Exception error)
{
    // Never print MSAL messages, account names, URLs, response bodies or nested exception details.
    evidence["failure"] = error is CloudRecoveryException cloud ? cloud.Failure.ToString()
        : error is OperationCanceledException ? "Cancelled" : error is MsalException ? "AuthenticationFailed" : "DiagnosticFailed";
    return 1;
}
finally
{
    if (store is not null && created is not null && !(bool)evidence["deleted"])
    {
        try
        {
            using var cleanup = await store.ReadExactAsync(created.ItemId, CancellationToken.None);
            if (!cleanup.Content.Span.SequenceEqual(content)) evidence["cleanupRequired"] = true;
            else _ = await store.DeleteExactAsync(cleanup.ItemId, cleanup.ETag, CancellationToken.None);
        }
        catch (CloudRecoveryException error) when (error.Failure == CloudRecoveryFailure.ObjectNotFound) { }
        catch (Exception) { evidence["cleanupRequired"] = true; }
    }
    created?.Dispose(); store?.Dispose();
    CryptographicOperations.ZeroMemory(random); CryptographicOperations.ZeroMemory(content);
    evidence["completedUtc"] = DateTimeOffset.UtcNow;
    await File.WriteAllTextAsync(args[4], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Sanitized evidence written. No account identifiers or tokens were recorded.");
}

static async Task InspectRootShape(string accessToken, Dictionary<string, object> evidence, CancellationToken token)
{
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive/special/approot");
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    evidence["appRootStatus"] = (int)response.StatusCode;
    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 32768) return;
    var bytes = new byte[32769];
    try
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        int length = 0, read;
        while (length < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(length), token)) != 0) length += read;
        if (length > 32768) { evidence["appRootOversize"] = true; return; }
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
        var root = document.RootElement;
        evidence["appRootObject"] = root.ValueKind == JsonValueKind.Object;
        if (root.ValueKind != JsonValueKind.Object) return;
        evidence["appRootHasStringId"] = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String;
        evidence["appRootHasFolder"] = root.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Object;
        evidence["appRootHasSpecialFolder"] = root.TryGetProperty("specialFolder", out var special) && special.ValueKind == JsonValueKind.Object;
        evidence["appRootSpecialNameMatches"] = special.ValueKind == JsonValueKind.Object &&
            special.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "approot";
        evidence["appRootSpecialNameMatchesIgnoringCase"] = special.ValueKind == JsonValueKind.Object &&
            special.TryGetProperty("name", out var alias) && alias.ValueKind == JsonValueKind.String &&
            string.Equals(alias.GetString(), "approot", StringComparison.OrdinalIgnoreCase);
    }
    finally { CryptographicOperations.ZeroMemory(bytes); }
}

internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern nint GetConsoleWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
}
