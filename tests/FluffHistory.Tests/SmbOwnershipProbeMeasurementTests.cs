using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static FluffHistory.Tests.SmbNetworkMeasurementTests;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class SmbOwnershipProbeMeasurementTests(ITestOutputHelper output)
{
    [SmbFact]
    public void ReadOnlyProbeReachesServerRepeatedlyWithoutChangingPersistentLock()
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var path = Path.Combine(repo.DirectoryPath, "writer.lock");
        var time = File.GetLastWriteTimeUtc(path); var state = File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json"));
        using var handle = RepositoryWriterLock.Open(repo.DirectoryPath);
        var counters = SmbServerCounters.Capture();
        var metric = Measure(() => { for (var i = 0; i < 100; i++) RepositoryWriterLock.ProbeServerHandle(handle); });
        var protocol = SmbServerCounters.Difference(counters, SmbServerCounters.Capture());
        Assert.Equal(0, handle.Length); Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(repo.DirectoryPath, "state.json")));
        if (protocol is not null) Assert.True(protocol.GetValueOrDefault("smb2_getinfo_count") >= 100);
        new SmbNetworkMeasurementTests(output).Save("ownership-probe", new { Probe = "FileStreamInfo", Calls = 100, Metric = metric,
            Smb = protocol, LockBytes = handle.Length, LockLastWriteTimeUnchanged = true, AuthorityUnchanged = true });
    }
}
