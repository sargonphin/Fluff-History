using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace FluffHistory.Tests.Infrastructure;

[CollectionDefinition("SMB diagnostics", DisableParallelization = true)]
public sealed class SmbDiagnosticCollection;

// Optional diagnostic administration only. Production uses ordinary filesystem APIs.
internal static class SmbServerCounters
{
    internal static Dictionary<string, long>? Capture()
    {
        var target = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_SSH_TARGET");
        if (string.IsNullOrWhiteSpace(target)) return null;
        // Samba batches profile updates; settle outside the operation's measured interval.
        Thread.Sleep(1200);
        var text = Command("sudo -n smbstatus --profile");
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var pair = line.Split(':', 2);
            if (pair.Length == 2 && long.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                result[pair[0].Trim()] = value;
        }
        Assert.Contains("smb2_read_count", result.Keys); return result;
    }
    internal static Dictionary<string, long>? Difference(Dictionary<string, long>? before, Dictionary<string, long>? after)
    {
        if (before is null || after is null) return null;
        return after.Where(p => p.Key.StartsWith("smb2_", StringComparison.Ordinal) || p.Key == "request_count")
            .ToDictionary(p => p.Key, p => p.Value - before.GetValueOrDefault(p.Key), StringComparer.Ordinal);
    }
    internal static string Command(string command)
    {
        var target = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_SSH_TARGET") ?? throw new InvalidOperationException("SSH diagnostic target missing.");
        var start = new ProcessStartInfo("ssh.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", target, command }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("SSH diagnostic command timed out."); }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult()); return text.GetAwaiter().GetResult();
    }
}
