using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic"), Trait("Category", "SmbFault")]
public sealed class SmbWriterReconnectTests(ITestOutputHelper output)
{
    [SmbFaultTheory]
    [InlineData(true)]
    public async Task RevokedServerHandleCannotPublishUnderAnotherAliasWriter(bool revoke)
        => await RunProbe("none", requireStopped: revoke);

    [SmbFaultTheory]
    [InlineData("flush")] [InlineData("lock")] [InlineData("read")]
    [InlineData("streams")] [InlineData("streams-prewarm")]
    public async Task MeasureServerOwnershipProbe(string probe) => await RunProbe(probe, requireStopped: false);

    private async Task RunProbe(string probe, bool requireStopped)
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var aliasRoot = Environment.GetEnvironmentVariable("FLUFF_HISTORY_SMB_ALIAS_ROOT")
            ?? throw new InvalidOperationException("Configure a second dedicated share alias of the exact same test backing directory.");
        var aliasPath = Path.Combine(aliasRoot, Path.GetFileName(f.Remote), "repository");
        var alias = Repository.Open(aliasPath); Assert.Equal(repo.Identity, alias.Identity);
        var before = repo.ReadAuthority().Identity;
        await using var child = WriterProcess.Start("m19-held-prepared-" + probe, repo.DirectoryPath); await child.WaitUntilReadyAsync();
        Assert.ThrowsAny<IOException>(() => alias.OpenWriter(f.Local));
        // Main test share only; alias share remains connected. No global Samba restart.
        var share = SmbNetworkMeasurementTests.Remote.TrimStart('\\').Split('\\')[1];
        Assert.Matches("^[A-Za-z0-9_-]+$", share); // Explicit diagnostic shell argument; no arbitrary command text.
        SmbServerCounters.Command($"sudo -n smbcontrol all close-share '{share}'");
        RepositoryWriter? next = null;
        for (var attempt = 0; attempt < 20 && next is null; attempt++)
        {
            try { next = alias.OpenWriter(f.Local); }
            catch (IOException) { await Task.Delay(100); }
        }
        if (next is null)
        {
            // The server/client retained the old exclusion; no second writer was admitted.
            child.Continue(); var retainedExit = await child.ExitCodeAsync();
            Assert.True(retainedExit is 0 or 10);
            new SmbNetworkMeasurementTests(output).Save("writer-reconnect-" + probe, new { Probe = probe,
                NewAliasWriterAdmitted = false, OldExclusionRetained = true, OldExit = retainedExit,
                Note = "No revoked-handle/dual-owner outcome was observed on this server configuration." });
            output.WriteLine("SMB_WRITER_RECONNECT old exclusion retained, old exit=" + retainedExit); return;
        }
        using (next)
        {
            child.Continue(); var exit = await child.ExitCodeAsync();
            var actual = alias.ReadAuthority().Identity;
            var result = new { Probe = probe, NewAliasWriterAdmitted = true, OldExit = exit, Before = before, Actual = actual, Output = child.ReadOutput() };
            new SmbNetworkMeasurementTests(output).Save("writer-reconnect-" + probe, result);
            if (requireStopped) { Assert.Equal(10, exit); Assert.Equal(before, actual); }
            foreach (var transaction in next.EnumeratePendingPublications().ToArray())
                Assert.Equal(actual == before ? PublicationOutcome.Unpublished : PublicationOutcome.Published, next.Reconcile(transaction));
        }
    }
}
