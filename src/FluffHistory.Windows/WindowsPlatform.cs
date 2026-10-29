using System.Reflection;

namespace FluffHistory.Windows;

/// <summary>Product capabilities, independent of the SDK's compile-time API surface.</summary>
public static class WindowsPlatform
{
    public static Version TechnicalMinimum { get; } = Minimum("WindowsTechnicalMinimum");
    public static Version ProductMinimum { get; } = Minimum("WindowsProductMinimum");
    public static bool SupportsExplorerContextCommand =>
        OperatingSystem.IsWindows() && SupportsExplorerContextCommandOn(Environment.OSVersion.Version);
    public static bool SupportsExplorerContextCommandOn(Version version) => version >= ProductMinimum;

    private static Version Minimum(string name) => Version.Parse(typeof(WindowsPlatform).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == name).Value!);
}
