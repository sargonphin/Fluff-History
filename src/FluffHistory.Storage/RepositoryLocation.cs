namespace FluffHistory.Storage;

public enum PathNetworkDependency { Unknown, Local, Network }

/// <summary>A non-mutating location hint using the same Windows path/drive classification as Storage.
/// Does not open a repository or establish reachability, absence, identity, or independent filesystems.</summary>
public static class RepositoryLocation
{
    public static PathNetworkDependency ClassifyNetworkDependency(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return PathNetworkDependency.Unknown;
            return WindowsFileSystem.IsNetwork(path) ? PathNetworkDependency.Network : PathNetworkDependency.Local;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { return PathNetworkDependency.Unknown; }
    }
}
