using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic"), Trait("Category", "SmbIntegration")]
public sealed class SmbRuntimeMaintenanceTests(ITestOutputHelper output)
{
    [SmbFact]
    public void UncRuntimeAssessmentCompactionAndReaderDeferredCleanupUseLocalBoundedWorkspace()
    {
        using var f = new SmbTestEnvironment();
        using var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        EncryptionTests.Add(repo, "document.txt", [1, 2, 3]); AutomaticMetadataMaintenanceTests.Inflate(repo);
        var before = MetadataAssessmentTests.Snapshot(repo); var sequence = repo.ReadAuthority().Identity.MutationSequence;
        var held = repo.OpenReader(); Assert.NotNull(held.GetCurrent(new(FoundationTests.Root, "document.txt")));
        try
        {
            var source = Path.Combine(f.Local, "source"); Directory.CreateDirectory(source);
            var config = new BackupConfiguration(repo.DirectoryPath, [new(new(FoundationTests.Root, source), source)], new(WorkspaceRoot: f.Local));
            var policy = new AutomaticMetadataMaintenance();
            var result = policy.Run(repo, config, new() { Outcome = ScanOutcome.Completed }, new(MaintenanceState.NotNeeded), _ => { }, default);
            Assert.Equal(MetadataMaintenanceState.Compacted, result.State); Assert.True(result.Assessed); Assert.True(result.RetiredDeferred > 0);
            Assert.Equal(before, MetadataAssessmentTests.Snapshot(repo)); Assert.Equal(sequence, repo.ReadAuthority().Identity.MutationSequence);
            Assert.NotNull(held.GetCurrent(new(FoundationTests.Root, "document.txt"))); held.Dispose();
            var retried = policy.Run(repo, config, new() { Outcome = ScanOutcome.Completed }, new(MaintenanceState.NotNeeded), _ => { }, default);
            Assert.True(retried.RetiredDeleted > 0); Assert.Equal(0, retried.RetiredDeferred); Assert.False(retried.Assessed);
            Assert.Equal(before, MetadataAssessmentTests.Snapshot(repo));
            output.WriteLine($"UNC runtime: active={result.ActiveDecodedBytes}, reclaimable={result.ReclaimableDecodedBytes}, output={result.OutputBytes}, deferred={result.RetiredDeferred}, laterDeleted={retried.RetiredDeleted}; payload/full semantics unchanged.");
        }
        finally { held.Dispose(); }
    }
}
