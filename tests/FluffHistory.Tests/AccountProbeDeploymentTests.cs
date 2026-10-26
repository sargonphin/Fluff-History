using System.Diagnostics;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class AccountProbeDeploymentTests
{
    [Fact]
    public void ProbeLaunchUsesInputsDeployedBesideTheTestAssembly()
    {
        var start = MicrosoftAccountLiveTests.CreateProbeStartInfo("Inspect", "unused");
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Diagnostics", "Invoke-MicrosoftAccountProbe.ps1"), start.ArgumentList[4]);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardOutput && start.RedirectStandardError);
    }

    [Fact]
    public void MissingInputFailsWithoutFallingBackToAWorktree()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Directory.CreateDirectory(temporary.Child("Diagnostics")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Diagnostics", "Invoke-MicrosoftAccountProbe.ps1"), Path.Combine(directory, "Invoke-MicrosoftAccountProbe.ps1"));
        Assert.Throws<FileNotFoundException>(() => MicrosoftAccountLiveTests.CreateProbeStartInfo("Inspect", "unused", temporary.Root));
    }

    [WindowsTheory]
    [InlineData("bin/Release/net10.0")]
    [InlineData("redirected/custom-output")]
    public async Task DefaultAndRedirectedLayoutsLaunchTheProbeWithoutAccountAccess(string layout)
    {
        using var temporary = new TemporaryDirectory();
        var output = Path.Combine(temporary.Root, layout.Replace('/', Path.DirectorySeparatorChar));
        var inputs = Directory.CreateDirectory(Path.Combine(output, "Diagnostics")).FullName;
        foreach (var name in new[] { "Invoke-MicrosoftAccountProbe.ps1", "MicrosoftAccountProbeNative.cs" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Diagnostics", name), Path.Combine(inputs, name));
        // PowerShell rejects this mode during parameter binding, before loading native code,
        // inspecting the Windows profile or touching PasswordVault. A missing script is a distinct error.
        using var process = Process.Start(MicrosoftAccountLiveTests.CreateProbeStartInfo("Unsupported", temporary.Child("unused"), output))!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.NotEqual(0, process.ExitCode);
        Assert.Empty(await standardOutput);
        Assert.Contains("ParameterArgumentValidationError", await standardError);
    }
}
