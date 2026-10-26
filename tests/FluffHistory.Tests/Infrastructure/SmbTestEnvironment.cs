using Xunit;

namespace FluffHistory.Tests.Infrastructure;

public sealed class SmbFactAttribute : FactAttribute
{
    public SmbFactAttribute(string rootSetting = "FLUFF_HISTORY_SMB_TEST_ROOT")
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(rootSetting)))
            Skip = $"Opt-in native Windows UNC integration: set {rootSetting} to an owned disposable test root.";
    }
}
public sealed class SmbTheoryAttribute : TheoryAttribute
{
    public SmbTheoryAttribute(string rootSetting = "FLUFF_HISTORY_SMB_TEST_ROOT")
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(rootSetting)))
            Skip = $"Opt-in native Windows UNC integration: set {rootSetting} to an owned disposable test root.";
    }
}
internal sealed class SmbTestEnvironment : IDisposable
{
    internal string Remote { get; }
    internal string Local { get; }
    internal SmbTestEnvironment(string? configuredRoot = null)
    {
        var root = configuredRoot ?? Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_TEST_ROOT") ?? throw new InvalidOperationException("SMB test root missing.");
        if (!root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException("SMB tests require an ordinary UNC test root.");
        var owned = "case-" + Guid.NewGuid().ToString("N");
        Remote = Path.Combine(Path.GetFullPath(root), owned);
        Local = Path.Combine(Path.GetTempPath(), "FluffHistory-M19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Remote); Directory.CreateDirectory(Local);
        File.WriteAllText(Path.Combine(Remote, ".m19-owned"), owned);
    }
    public void Dispose()
    {
        // Both exact generated roots are owned by this instance; never delete the configured share/root.
        var name = Path.GetFileName(Remote);
        if (!name.StartsWith("case-", StringComparison.Ordinal) || !Guid.TryParseExact(name[5..], "N", out _) ||
            File.ReadAllText(Path.Combine(Remote, ".m19-owned")) != name) throw new IOException("SMB cleanup ownership mismatch.");
        Directory.Delete(Remote, true); Directory.Delete(Local, true);
    }
}
