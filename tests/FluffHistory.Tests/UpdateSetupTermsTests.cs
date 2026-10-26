using FluffHistory.Windows;
using Xunit;

namespace FluffHistory.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class UpdateSetupTermsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyManualUpdateMayShowSetupAgreement(bool manual)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var arguments = WindowsUpdateHost.SetupArguments(manual, nonce);
        Assert.Equal(!manual, arguments.Contains("/VERYSILENT"));
        Assert.Equal(!manual, arguments.Contains("/SUPPRESSMSGBOXES"));
        Assert.Contains("/NORESTART", arguments);
        Assert.Contains("/UPDATEHANDOFF=" + nonce, arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("ACCEPT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidHandoffCannotBecomeSetupArguments() =>
        Assert.Throws<ArgumentException>(() => WindowsUpdateHost.SetupArguments(false, "invalid /ACCEPTDISTRIBUTABLETERMS=yes"));
}
