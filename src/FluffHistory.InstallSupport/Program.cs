using FluffHistory.Application;
using FluffHistory.Windows;

if (!OperatingSystem.IsWindows()) return 1;
try
{
    if (args is ["--apply-update", var release, var nonce])
        return await WindowsUpdateHost.ApplyAsync(release, nonce, System.Reflection.Assembly.GetExecutingAssembly());
    if (args is ["--validate-state", var directory])
    {
        if (!Directory.Exists(directory)) return 0;
        var settings = await new AppSettingsStore(Path.Combine(directory, "settings-v2.json")).LoadAsync();
        var preferences = await new ApplicationPreferencesStore(Path.Combine(directory, "preferences-v1.json")).LoadAsync();
        return settings.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable ||
            preferences.State is SettingsLoadState.Corrupt or SettingsLoadState.Unavailable ? 2 : 0;
    }
    if (args is ["--stop-agent"]) return await new WindowsAgentClient().ShutdownForUpdateAsync() ? 0 : 2;
    if (args is ["--agent-ready", var version])
    {
        var reply = await new WindowsAgentClient().ConnectOrStartAsync();
        return reply.State.ProductVersion == version && !reply.State.ShuttingDown ? 0 : 2;
    }
    return 1;
}
catch (Exception) { return 2; }
