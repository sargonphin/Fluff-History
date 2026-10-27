using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

/// <summary>Real Windows/PasswordVault diagnostics only; never a hosted account requirement.</summary>
public sealed class MicrosoftAccountLiveTests
{
    [MicrosoftAccountLiveFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void ProvisionedVaultCandidateSurvivesProcessRestartAndRequiresItsCredential()
    {
        using var temp = new TemporaryDirectory(); var directory = temp.Child("synthetic");
        using var created = Invoke("Protect", directory);
        try
        {
            Assert.True(created.RootElement.GetProperty("identity").GetProperty("internetIdentity").GetBoolean());
            Assert.Equal("MicrosoftAccount", created.RootElement.GetProperty("identity").GetProperty("provider").GetString());
            var cases = created.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            Assert.All(cases.Where(c => c.GetProperty("descriptor").GetString() == "LOCAL=user"), c => Assert.True(c.GetProperty("matches").GetBoolean()));
            Assert.True(cases.Single(c => c.GetProperty("phase").GetString() == "after-vault-add" &&
                c.GetProperty("descriptor").GetString() == "WEBCREDENTIALS=name,resource").GetProperty("matches").GetBoolean());
            // Different OS processes, same actual Windows profile; no plaintext key is passed between them.
            using var recovered = Invoke("Recover", directory);
            Assert.All(recovered.RootElement.GetProperty("cases").EnumerateArray(), c => Assert.True(c.GetProperty("matches").GetBoolean()));
        }
        finally { using var cleanup = Invoke("Cleanup", directory); }
        using var missing = Invoke("Recover", directory);
        Assert.False(missing.RootElement.GetProperty("ownSyntheticCredentialPresent").GetBoolean());
        Assert.All(missing.RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("descriptor").GetString() == "WEBCREDENTIALS=name,resource"),
            c => Assert.False(c.GetProperty("matches").GetBoolean()));
    }

    private static JsonDocument Invoke(string mode, string directory)
    {
        var start = CreateProbeStartInfo(mode, directory);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Silent synthetic account diagnostic exceeded 30 seconds."); }
        Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
        return JsonDocument.Parse(output.GetAwaiter().GetResult());
    }

    internal static ProcessStartInfo CreateProbeStartInfo(string mode, string directory, string? outputDirectory = null)
    {
        var inputs = Path.Combine(outputDirectory ?? AppContext.BaseDirectory, "Diagnostics");
        var script = Path.GetFullPath(Path.Combine(inputs, "Invoke-MicrosoftAccountProbe.ps1"));
        foreach (var file in new[] { script, Path.Combine(inputs, "MicrosoftAccountProbeNative.cs") })
            if (!File.Exists(file)) throw new FileNotFoundException("The account probe requires its copied diagnostic inputs.", file);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", mode, "-Directory", directory }) start.ArgumentList.Add(arg);
        return start;
    }
}

public sealed class MicrosoftAccountLiveFactAttribute : FactAttribute
{
    public MicrosoftAccountLiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FLUFF_HISTORY_ACCOUNT_DIAGNOSTICS") != "1")
            Skip = "Opt-in native live Microsoft-account/PasswordVault diagnostic; configure disposable MSA and set FLUFF_HISTORY_ACCOUNT_DIAGNOSTICS=1. Never required by hosted CI.";
    }
}
