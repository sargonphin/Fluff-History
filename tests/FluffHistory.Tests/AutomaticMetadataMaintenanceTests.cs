using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class AutomaticMetadataMaintenanceTests
{
    private static readonly MaintenanceSummary NoRetention = new(MaintenanceState.NotNeeded);
    private static ScanResult Complete() => new() { Outcome = ScanOutcome.Completed };
    private static BackupConfiguration Config(TemporaryDirectory temp) => new(temp.Child(),
        [new(new SourceRootConfiguration(FoundationTests.Root, temp.Child("source")), temp.Child("source"))],
        new(WorkspaceRoot: temp.Child("work")));

    [Fact]
    public void TinyActuallyDirtyRepositorySkipsFullAssessmentAndCompaction()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        Inflate(repo, 12);
        using (var writer = repo.OpenWriter())
        {
            var actual = writer.AssessMetadata(); Assert.True(actual.DirtyRatio > .30);
            Assert.True(actual.ReclaimableBytes < MetadataMaintenancePolicy.MinimumReclaimableBytes);
        }
        var before = repo.ReadAuthority().Identity;
        var result = new AutomaticMetadataMaintenance { Headroom = (_, _) => throw new InvalidOperationException("Tiny repositories must not reach expensive admission") }
            .Run(repo, Config(temp), Complete(), NoRetention, _ => { }, default);
        Assert.Equal(MetadataMaintenanceState.BelowFloor, result.State); Assert.False(result.Assessed);
        Assert.Equal(before, repo.ReadAuthority().Identity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MeaningfulDirtyMetadataCompactsExactlyAndLaterCleanupConvergesAfterReaderCloses(bool encrypted)
    {
        using var temp = new TemporaryDirectory(); using var repo = encrypted ? Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password) : Repository.Create(temp.Child());
        EncryptionTests.Add(repo, "foo.txt", [42]);
        Inflate(repo);
        var before = MetadataAssessmentTests.Snapshot(repo);
        var sequence = repo.ReadAuthority().Identity.MutationSequence;
        var held = repo.OpenReader(); Assert.NotNull(held.GetCurrent(new(FoundationTests.Root, "foo.txt")));
        var phases = new List<BackupPhase>(); var policy = new AutomaticMetadataMaintenance { Headroom = (_, _) => true };
        var result = policy.Run(repo, Config(temp), Complete(), NoRetention, phases.Add, default);
        Assert.Equal(MetadataMaintenanceState.Compacted, result.State); Assert.True(result.Assessed); Assert.True(result.DirtyRatio >= .30);
        Assert.True(result.ReclaimableDecodedBytes >= MetadataMaintenancePolicy.MinimumReclaimableBytes);
        if (OperatingSystem.IsWindows()) Assert.True(result.RetiredDeferred > 0); else Assert.True(result.RetiredDeleted > 0);
        Assert.Contains(BackupPhase.CompactingMetadata, phases);
        Assert.Equal(before, MetadataAssessmentTests.Snapshot(repo)); Assert.Equal(sequence, repo.ReadAuthority().Identity.MutationSequence);
        Assert.NotNull(held.GetCurrent(new(FoundationTests.Root, "foo.txt"))); held.Dispose();
        var second = policy.Run(repo, Config(temp), Complete(), NoRetention, phases.Add, default);
        Assert.NotEqual(MetadataMaintenanceState.Compacted, second.State); Assert.False(second.Assessed);
        if (OperatingSystem.IsWindows()) Assert.True(second.RetiredDeleted > 0);
        Assert.Equal(0, second.RetiredDeferred);
        Assert.Equal(before, MetadataAssessmentTests.Snapshot(repo));
    }

    [Theory]
    [InlineData("headroom")] [InlineData("assessment-budget")] [InlineData("cancel")]
    public void OptionalPrerequisiteFailureDoesNotPublishCompaction(string failure)
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        Inflate(repo); var before = repo.ReadAuthority().Identity;
        var policy = new AutomaticMetadataMaintenance { Headroom = (_, _) => failure != "headroom",
            Limits = failure == "assessment-budget" ? new(MaximumRecords: 1) : new() };
        if (failure == "cancel") Assert.Throws<OperationCanceledException>(() => policy.Run(repo, Config(temp), Complete(), NoRetention, _ => { }, new(true)));
        else
        {
            // A one-record assessment limit would also reject the two-segment cheap inventory.
            // Use the output limit to stop read-only counting without weakening the prefilter.
            if (failure == "assessment-budget") policy = new() { Headroom = (_, _) => true, Limits = new(MaximumOutputBytes: 1) };
            var result = policy.Run(repo, Config(temp), Complete(), NoRetention, _ => { }, default);
            Assert.Equal(failure == "headroom" ? MetadataMaintenanceState.HeadroomUnavailable : MetadataMaintenanceState.AssessmentUnavailable, result.State);
        }
        Assert.Equal(before, repo.ReadAuthority().Identity);
    }

    [Fact]
    public void CompletedSourceProblemsPermitMaintenanceButUnsafeScanAndRetentionStatesDoNot()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        var policy = new AutomaticMetadataMaintenance { Headroom = (_, _) => true };
        using (repo.OpenWriter())
        {
            foreach (var scan in new[] { new ScanResult { Outcome = ScanOutcome.ControlError, BatchesCommitted = 1 },
                new ScanResult { Outcome = ScanOutcome.Cancelled, Cancelled = true },
                new ScanResult { Outcome = ScanOutcome.Completed, ReconciliationMayBeRequired = true } })
                Assert.Equal(MetadataMaintenanceState.NotSafe, policy.Run(repo, Config(temp), scan, NoRetention, _ => { }, default).State);
            Assert.Equal(MetadataMaintenanceState.NotSafe, policy.Run(repo, Config(temp), Complete(), new(MaintenanceState.NeedsAttention), _ => { }, default).State);
        }
        Inflate(repo); var problems = Complete(); problems.Skip(FoundationTests.Root, "locked.txt", "Source unavailable");
        Assert.False(problems.Partial);
        Assert.Equal(MetadataMaintenanceState.Compacted, policy.Run(repo, Config(temp), problems, NoRetention, _ => { }, default).State);
    }

    [Fact]
    public void UnresolvedCandidatesRemainAHardBoundary()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        var before = repo.ReadAuthority().Identity;
        File.WriteAllText(Path.Combine(repo.DirectoryPath, "state.unresolved.tmp"), "keep");
        Assert.Throws<IOException>(() => new AutomaticMetadataMaintenance().Run(repo, Config(temp), Complete(), NoRetention, _ => { }, default));
        Assert.Equal(before, repo.ReadAuthority().Identity); Assert.Equal("keep", File.ReadAllText(Path.Combine(repo.DirectoryPath, "state.unresolved.tmp")));
    }

    [Fact]
    public void HeadroomSkipCanRetryAfterBoundedInMemoryCadenceWithoutAssessingEveryTick()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) FoundationTests.Add(repo, writer);
        Inflate(repo); var enough = false; var probes = 0;
        var policy = new AutomaticMetadataMaintenance { Headroom = (_, _) => { probes++; return enough; } };
        Assert.Equal(MetadataMaintenanceState.HeadroomUnavailable, policy.Run(repo, Config(temp), Complete(), NoRetention, _ => { }, default).State);
        for (var i = 0; i < 11; i++) Assert.Equal(MetadataMaintenanceState.Throttled, policy.Run(repo, Config(temp), Complete(), NoRetention, _ => { }, default).State);
        Assert.Equal(1, probes); enough = true;
        Assert.Equal(MetadataMaintenanceState.Compacted, policy.Run(repo, Config(temp), Complete(), NoRetention, _ => { }, default).State);
        Assert.Equal(3, probes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualRunnerKeepsCompactionOnlyWorkOutOfLogicalTimelineRevision(bool publicationFault)
    {
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(temp.Child("source")).FullName;
        File.WriteAllText(Path.Combine(source, "document.txt"), "source bytes");
        using var repo = Repository.Create(temp.Child());
        var config = Config(temp);
        var runner = new EngineBackupAttemptRunner { MetadataMaintenance = new() { Headroom = (writer, limits) =>
        {
            if (publicationFault && limits.MaximumOutputBytes > 1) writer.Observe = stage =>
            { if (stage == PublicationStage.BeforeReplace) throw new IOException("Controlled compaction publication failure"); };
            return true;
        } } };
        Assert.Equal(BackupOutcome.Completed, (await runner.RunAsync(config, AttemptTrigger.Manual, null, default)).Outcome);
        Inflate(repo); var before = MetadataAssessmentTests.Snapshot(repo);
        var phases = new List<BackupPhase>();
        var result = await runner.RunAsync(config, AttemptTrigger.Manual, progress => phases.Add(progress.Phase), default);
        Assert.Equal(publicationFault ? BackupOutcome.ScanControlFailure : BackupOutcome.Completed, result.Outcome);
        Assert.False(result.TimelineChanged); Assert.Equal(0, result.Scan!.BatchesCommitted); Assert.Equal(0, result.Scan.VersionsCreated);
        Assert.Contains(BackupPhase.CompactingMetadata, phases); Assert.DoesNotContain(BackupPhase.Encrypting, phases);
        Assert.DoesNotContain(BackupPhase.Decrypting, phases); Assert.DoesNotContain(BackupPhase.CompressingMetadata, phases);
        Assert.Equal(before, MetadataAssessmentTests.Snapshot(repo));
        if (!publicationFault)
        {
            Assert.Equal(MetadataMaintenanceState.Compacted, result.MetadataMaintenance!.State);
            var repeated = await runner.RunAsync(config, AttemptTrigger.Manual, null, default);
            Assert.False(repeated.TimelineChanged); Assert.False(repeated.MetadataMaintenance!.Assessed);
        }
    }

    // Controlled obsolete-page population, no padding or invented payload histories: duplicate
    // valid immutable index frames below a newly committed frontier, keeping all live roots.
    // The normal 64 MiB geometry and actual Format-4 appender are retained.
    internal static void Inflate(Repository repo, int pages = 4300)
    {
        using var writer = repo.OpenWriter(); using var reader = repo.OpenReader();
        var prior = reader.Captured;
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, prior.Root.Epoch, prior, crypto: repo.Crypto);
        var page = reader.Read(prior.Root.Sources!);
        for (var i = 0; i < pages; i++) append.Append(FrameKind.Page, page);
        var root = prior.Root with { Generation = prior.Root.Generation + 1, TransactionId = Guid.NewGuid(),
            PreviousGeneration = prior.Root.Generation, PreviousTransactionId = prior.Root.TransactionId,
            PreviousRootHash = prior.Address.Sha256 };
        var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
        AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, address), repo.Crypto));
        AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
    }
}
