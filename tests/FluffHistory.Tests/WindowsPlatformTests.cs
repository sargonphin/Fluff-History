using FluffHistory.Windows;
using Xunit;

namespace FluffHistory.Tests;

public sealed class WindowsPlatformTests
{
    [Theory]
    [InlineData("10.0.19041.0", false)]
    [InlineData("10.0.19044.6456", false)]
    [InlineData("10.0.19045.0", true)]
    [InlineData("10.0.19045.6456", true)]
    [InlineData("10.0.22000.0", true)]
    public void ClassicVerbIsAvailableOnBothSupportedOsFamilies(string version, bool available) =>
        Assert.Equal(available, WindowsPlatform.SupportsExplorerContextCommandOn(Version.Parse(version)));

    [Fact]
    public void SdkContractsAndTestedProductHaveDistinctMinimums()
    {
        Assert.Equal(new Version(10, 0, 19041, 0), WindowsPlatform.TechnicalMinimum);
        Assert.Equal(new Version(10, 0, 19045, 0), WindowsPlatform.ProductMinimum);
    }
}
