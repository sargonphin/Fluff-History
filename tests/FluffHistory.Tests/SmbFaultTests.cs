using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class SmbFaultTheoryAttribute : TheoryAttribute
{
    public SmbFaultTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_FAULT_TESTS") != "1")
            Skip = "Disruptive isolated SMB transport tests require FLUFF_HISTORY_SMB_FAULT_TESTS=1 and SSH administration.";
    }
}

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic"), Trait("Category", "SmbFault")]
public sealed class SmbFaultTests(ITestOutputHelper output)
{
    [SmbFaultTheory]
    [InlineData("root-enumeration", false)] [InlineData("child-enumeration", false)] [InlineData("observation", false)]
    [InlineData("source-read", false)] [InlineData("payload-write", false)] [InlineData("metadata-transfer", false)]
    [InlineData("before-replace", false)] [InlineData("after-replace", false)] [InlineData("restore", false)] [InlineData("restore-destination", false)]
    [InlineData("before-replace", true)] [InlineData("after-replace", true)] [InlineData("source-read", true)] [InlineData("restore-destination", true)]
    public async Task RealTcp445InterruptionHasWatchdogAndFreshAuthorityReconciliation(string mode, bool reconnectWhileAlive)
    {
        using var f = new SmbTestEnvironment();
        var remoteSource = mode is "root-enumeration" or "child-enumeration" or "observation" or "source-read";
        var home = remoteSource || mode == "restore-destination" ? f.Local : f.Remote;
        var source = Path.Combine(remoteSource ? f.Remote : f.Local, "source");
        Directory.CreateDirectory(Path.Combine(source, "child"));
        File.WriteAllBytes(Path.Combine(source, "child", "foo.txt"), new byte[2 * 1024 * 1024]);
        var repo = Repository.Create(Path.Combine(home, "repository"));
        SourceSelection[] selection = [new(new(FoundationTests.Root, source), source)];
        new FileHistoryEngine(repo, new(WorkspaceRoot: Path.Combine(f.Local, "work"))).Scan(selection);
        if (mode == "metadata-transfer")
        {
            // A real full active segment exceeds the 128KiB copy window, so the pause
            // occurs inside a transfer, with more source bytes still required.
            using var writer = repo.OpenWriter(f.Local);
            for (var i = 0; i < 12; i++)
                writer.Apply(new(sourceRoots: [new(FoundationTests.Root, source + new string('x', 8000) + i)]), repo.ReadAuthority().Identity);
        }
        else File.WriteAllBytes(Path.Combine(source, "child", "foo.txt"), new byte[3 * 1024 * 1024]);
        var before = repo.ReadAuthority().Identity;
        var restored = Path.Combine(mode == "restore-destination" ? f.Remote : f.Local, "restored.txt");
        File.WriteAllText(Path.Combine(home, "fault.json"), JsonSerializer.Serialize(new SmbFaultChild.Configuration(source,
            Path.Combine(f.Local, "fault-work"), restored)));
        var blocked = false; var killed = false; int? exit = null; string? childOutput = null;
        long blockedPackets = 0;
        var server = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_SERVER_IP") ?? throw new InvalidOperationException("Set isolated SMB server IP.");
        var client = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_CLIENT_IP") ?? throw new InvalidOperationException("Set isolated SMB client IP.");
        Assert.True(System.Net.IPAddress.TryParse(server, out _)); Assert.True(System.Net.IPAddress.TryParse(client, out _));
        // Shell commands contain only parsed IP literals and an internally generated table.
        var table = "m19_" + Guid.NewGuid().ToString("N");
        await using var process = WriterProcess.Start("m19-fault-" + mode, repo.DirectoryPath);
        try
        {
            await process.WaitUntilReadyAsync();
            // A dead parent cannot strand a fault: server-side expiry is armed BEFORE blocking.
            SmbServerCounters.Command($"sudo -n sh -c 'nohup sh -c \"sleep 35; nft delete table inet {table} 2>/dev/null\" </dev/null >/tmp/{table}.log 2>&1 &' ");
            SmbServerCounters.Command($"sudo -n nft add table inet {table}; sudo -n nft add chain inet {table} input '{{ type filter hook input priority -10; policy accept; }}'; sudo -n nft add rule inet {table} input ip saddr {client} tcp dport 445 counter reject with tcp reset");
            blocked = true;
            SmbServerCounters.Command($"sudo -n ss -K dst {client} sport = :445");
            process.Continue();
            try { exit = await process.ExitCodeAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException) { killed = true; }
            finally
            {
                var counters = SmbServerCounters.Command($"sudo -n nft list table inet {table}");
                output.WriteLine("SMB_FAULT_COUNTER " + counters);
                var count = System.Text.RegularExpressions.Regex.Match(counters, @"counter packets (\d+) bytes");
                if (count.Success) blockedPackets = long.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                SmbServerCounters.Command($"sudo -n nft delete table inet {table}"); blocked = false;
            }
            if (killed && reconnectWhileAlive)
            {
                try { exit = await process.ExitCodeAsync().WaitAsync(TimeSpan.FromSeconds(15)); killed = false; }
                catch (TimeoutException) { }
            }
            if (killed) await process.KillAsync();
            childOutput = process.ReadOutput();
        }
        finally
        {
            if (blocked) SmbServerCounters.Command($"sudo -n nft delete table inet {table}");
        }
        var fresh = Repository.Open(repo.DirectoryPath); var actual = fresh.ReadAuthority().Identity;
        var readyBeforeReconciliation = Directory.GetFiles(repo.DirectoryPath, "*.ready", SearchOption.AllDirectories).Length;
        var boundBeforeReconciliation = Directory.GetFiles(repo.DirectoryPath, "*.bound", SearchOption.AllDirectories).Length;
        using var owner = fresh.OpenWriter(Path.Combine(f.Local, "reconcile-work"));
        var outcomes = new List<string>();
        foreach (var transaction in owner.EnumeratePendingPublications().ToArray()) outcomes.Add(owner.Reconcile(transaction).ToString());
        if (actual != before) outcomes.Add(owner.Reconcile(actual.TransactionId).ToString());
        if (mode == "after-replace") Assert.NotEqual(before, actual);
        else if (!reconnectWhileAlive) Assert.Equal(before, actual);
        if (remoteSource) { using var reader = fresh.OpenReader(); Assert.Single(reader.EnumerateCurrent()); }
        var readyAfterReconciliation = Directory.GetFiles(repo.DirectoryPath, "*.ready", SearchOption.AllDirectories).Length;
        if (mode is "before-replace" or "after-replace")
        {
            Assert.Equal(0, readyBeforeReconciliation);
            if (!reconnectWhileAlive) Assert.Equal(1, boundBeforeReconciliation);
            Assert.Equal(actual == before ? 1 : 0, readyAfterReconciliation);
        }
        if (mode.StartsWith("restore", StringComparison.Ordinal) && File.Exists(restored))
        { Assert.Equal(2 * 1024 * 1024, new FileInfo(restored).Length); Assert.All(File.ReadAllBytes(restored), b => Assert.Equal(0, b)); }
        Assert.True(blockedPackets > 0, "The scoped rule must observe actual rejected TCP-445 packets, not only be installed.");
        var result = new { Mode = mode, ReconnectWhileAlive = reconnectWhileAlive, BlockedPackets = blockedPackets, WatchdogKilled = killed, Exit = exit, Before = before, Actual = actual, Reconciliation = outcomes, Child = childOutput,
            ReadyBeforeReconciliation = readyBeforeReconciliation, BoundBeforeReconciliation = boundBeforeReconciliation,
            ReadyAfterReconciliation = readyAfterReconciliation, RestoreFinalExists = File.Exists(restored),
            RestoreTemporaryFiles = Directory.GetFiles(Path.GetDirectoryName(restored)!, ".fluff-restore-*.tmp").Length };
        output.WriteLine("SMB_FAULT " + JsonSerializer.Serialize(result));
        new SmbNetworkMeasurementTests(output).Save("fault-" + mode + (reconnectWhileAlive ? "-reconnect" : ""), result);
    }
}
