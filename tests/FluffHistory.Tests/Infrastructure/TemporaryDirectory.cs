namespace FluffHistory.Tests.Infrastructure;

internal sealed class TemporaryDirectory : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("FluffHistory.Tests-").FullName;

    internal string Child(string name = "repository") => Path.Combine(Root, name);

    internal static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(directory, path),
            path => Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)),
            StringComparer.Ordinal);

    public void Dispose() => DeleteWithSharingViolationRetry(
        () => Directory.Delete(Root, recursive: true), OperatingSystem.IsWindows());

    // Cleanup only: never retry repository ownership assertions. Windows can briefly reject
    // deletion after process/handle teardown. Retry only ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION,
    // at most five times (250 ms total backoff); persistent locks and other errors still fail.
    internal static void DeleteWithSharingViolationRetry(Action delete, bool isWindows)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (IOException exception) when (
                isWindows && (exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) && attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
        }
    }
}
