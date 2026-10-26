using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class EncryptionConversionFaultTests
{
    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ApplicationDefersExactRetirementWithoutRollingBackCompletedMode(bool encrypted)
    {
        using var temp = new TemporaryDirectory(); string oldMetadata;
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true))
        {
            EncryptionTests.Add(source); using var reader = source.OpenReader();
            oldMetadata = SegmentReader.SegmentPath(temp.Child(), reader.Captured.Address.SegmentId);
        }
        using var protector = new LegacyRecoveryFixture.Cleanup(); FileStream? held = null;
        try
        {
            var service = new RepositoryEncryptionService(new(), protector, temp.Root)
            { Options = EncryptionConversionTests.Options(temp.Root) with { Observed = stage =>
                { if (stage == EncryptionConversionStage.AfterMarkerRemoval) held = new(oldMetadata, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); } } };
            var result = await service.ChangeAsync(temp.Child(), encrypted, EncryptionTests.Password.ToCharArray());
            Assert.Equal(EncryptionChangeOutcome.Completed, result.Outcome); Assert.True(result.CleanupDeferred);
            Assert.True(File.Exists(oldMetadata)); Assert.Null(Repository.InspectEncryptionMigration(temp.Child()));
            using var complete = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
            using var current = complete.OpenReader(); Assert.Single(current.EnumerateCurrent());
            held!.Dispose(); held = null;
            using var writer = complete.OpenWriter(); Assert.Equal(0, writer.CleanupRetiredMetadata().DeferredSegments);
            Assert.False(File.Exists(oldMetadata));
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData(true, "content")] [InlineData(true, "length")]
    [InlineData(false, "header")] [InlineData(false, "chunk")]
    [InlineData(false, "final")] [InlineData(false, "length")]
    public void CorruptSourceCannotBeAcceptedByResume(bool encrypted, string damage)
    {
        using var temp = new TemporaryDirectory(); string payload;
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows()))
            payload = source.GetPayloadPath(EncryptionTests.Add(source, "chunks.bin", new byte[524289]).Identity.VersionId);
        var original = File.ReadAllBytes(payload); var bytes = original.ToArray();
        if (damage == "length") Array.Resize(ref bytes, bytes.Length - 1);
        else bytes[damage switch { "header" => 0, "chunk" => 262144, _ => bytes.Length - 1 }] ^= 1;
        File.WriteAllBytes(payload, bytes);
        Assert.ThrowsAny<Exception>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root)));
        Assert.Equal(bytes, File.ReadAllBytes(payload));
        File.WriteAllBytes(payload, original);
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CorruptTargetCandidatePreservesValidatedPayload(bool encrypted)
    {
        using var temp = new TemporaryDirectory(); string payload;
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows()))
            payload = source.GetPayloadPath(EncryptionTests.Add(source).Identity.VersionId);
        var original = File.ReadAllBytes(payload);
        var options = EncryptionConversionTests.Options(temp.Root) with { Observed = stage =>
        {
            if (stage != EncryptionConversionStage.PayloadFlushed) return;
            var candidate = Directory.GetFiles(Path.GetDirectoryName(payload)!, "*.migration-*.tmp").Single();
            var bytes = File.ReadAllBytes(candidate); bytes[^1] ^= 1; File.WriteAllBytes(candidate, bytes);
        } };
        Assert.ThrowsAny<Exception>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, options));
        Assert.Equal(original, File.ReadAllBytes(payload));
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
    }

    [Fact]
    public void DamagedPreparedTargetKeyCannotStartPayloadReplacement()
    {
        using var temp = new TemporaryDirectory(); string payload;
        using (var source = Repository.Create(temp.Child())) payload = source.GetPayloadPath(EncryptionTests.Add(source).Identity.VersionId);
        var original = File.ReadAllBytes(payload);
        Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password,
            EncryptionConversionTests.Options(temp.Root) with { Observed = stage => { if (stage == EncryptionConversionStage.MarkerInstalled) throw new IOException("Prepared target only."); } }));
        var control = EncryptionMigrationControl.Read(temp.Child());
        var target = RepositoryBootstrap.Parse(Convert.FromBase64String(control.TargetBootstrap));
        var wrapped = Convert.FromBase64String(target.Recovery!.WrappedKey); wrapped[^1] ^= 1;
        target = target with { Recovery = target.Recovery with { WrappedKey = Convert.ToBase64String(wrapped) } };
        (control with { TargetBootstrap = Convert.ToBase64String(target.Bytes()) }).Install(temp.Child(), false);
        Assert.ThrowsAny<Exception>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root)));
        Assert.Equal(original, File.ReadAllBytes(payload));
        control.Install(temp.Child(), false);
        Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
    }

    [Theory]
    [InlineData(true, "AfterPayloadReplace", 1)] [InlineData(false, "AfterPayloadReplace", 1)]
    [InlineData(true, "AfterPayloadReplace", 3)] [InlineData(false, "AfterPayloadReplace", 3)]
    [InlineData(true, "PayloadsCompleted", 1)] [InlineData(false, "PayloadsCompleted", 1)]
    [InlineData(true, "AfterStatePivot", 1)] [InlineData(false, "AfterStatePivot", 1)]
    [InlineData(true, "ReadyRecorded", 1)] [InlineData(false, "ReadyRecorded", 1)]
    [InlineData(true, "AfterBootstrapPivot", 1)] [InlineData(false, "AfterBootstrapPivot", 1)]
    public void CancellationOfPartialOrCommittedWorkResumesWithoutChangingHistory(bool encrypted, string boundary, int occurrence)
    {
        using var temp = new TemporaryDirectory(); string digest;
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows()))
        {
            for (var i = 0; i < 3; i++) EncryptionTests.Add(source, i + ".txt");
            using var reader = source.OpenReader(); digest = Repository.SemanticDigest(reader, default);
        }
        using var cancellation = new CancellationTokenSource(); var seen = 0;
        var options = EncryptionConversionTests.Options(temp.Root) with
        { Observed = stage => { if (stage.ToString() == boundary && ++seen == occurrence) cancellation.Cancel(); } };
        Assert.Throws<OperationCanceledException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, options, token: cancellation.Token));
        Assert.NotNull(Repository.InspectEncryptionMigration(temp.Child()));
        Assert.Throws<EncryptionMigrationRequiredException>(() => Repository.Discover(temp.Child()));
        var result = Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.Equal(digest, result.SemanticDigest); Assert.Equal(3, result.Versions);
        using var target = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
        using var after = target.OpenReader(); Assert.Equal(digest, Repository.SemanticDigest(after, default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompletedModeChangeExpiresRetainedContextAndWarmTimeline(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using var retained = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows());
        EncryptionTests.Add(retained); using var reader = retained.OpenReader();
        Assert.Single(reader.ReadCachedTimelinePage(new(), new(FoundationTests.Root)).Items);
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.Throws<ReaderGenerationExpiredException>(() => retained.OpenReader());
        Assert.Throws<ReaderGenerationExpiredException>(() => reader.ReadCachedTimelinePage(new(), new(FoundationTests.Root)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReadyProofTamperingCannotExposeAnUnverifiedTarget(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using (var source = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows())) EncryptionTests.Add(source);
        Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password,
            EncryptionConversionTests.Options(temp.Root) with { Observed = point => { if (point == EncryptionConversionStage.ReadyRecorded) throw new IOException("Ready but fenced."); } }));
        var path = Path.Combine(temp.Child(), EncryptionMigrationControl.FileName); var original = File.ReadAllBytes(path);
        var control = EncryptionMigrationControl.Read(temp.Child());
        (control with { VersionCount = control.VersionCount + 1 }).Install(temp.Child(), false); // Self-checksum alone cannot authorize completion.
        Assert.Throws<InvalidDataException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root)));
        Assert.Equal(control.FenceBytes(), File.ReadAllBytes(Path.Combine(temp.Child(), "repository.json")));
        File.WriteAllBytes(path, original);
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
    }

    [Fact]
    public void ExactFreeSpaceThresholdIncludesPerFileAllocationGrowth()
    {
        using var temp = new TemporaryDirectory(); using (var repository = Repository.Create(temp.Child()))
            for (var i = 0; i < 9; i++) EncryptionTests.Add(repository, i + ".txt", new byte[4090]);
        var options = EncryptionConversionTests.Options(temp.Root) with { CapacityProbe = _ => new(100_000_000, 0) };
        var required = Assert.Throws<EncryptionMigrationSpaceException>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, options)).RequiredFree;
        Assert.True(required >= 9 * 4096 + 5_000_000);
        Assert.Throws<EncryptionMigrationSpaceException>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password,
            options with { CapacityProbe = _ => new(100_000_000, required - 1) }));
        var result = Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password,
            options with { CapacityProbe = _ => new(100_000_000, required) }); Assert.True(result.Encrypted);
    }

    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public void PasswordResumeRepairsTargetWindowsSlotBeforeOrDuringPivot(bool partiallyPivoted)
    {
        using var temp = new TemporaryDirectory(); using (var source = Repository.Create(temp.Child())) EncryptionTests.Add(source);
        var stop = partiallyPivoted ? EncryptionConversionStage.BeforeReady : EncryptionConversionStage.TargetStatePrepared;
        Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password,
            EncryptionConversionTests.Options(temp.Root) with { Observed = point => { if (point == stop) throw new IOException("Controlled pause."); } }));
        var control = EncryptionMigrationControl.Read(temp.Child());
        var target = RepositoryBootstrap.Parse(Convert.FromBase64String(control.TargetBootstrap)) with { WindowsUserKey = "damaged synthetic local enrollment" };
        control = control with { TargetBootstrap = Convert.ToBase64String(target.Bytes()) }; control.Install(temp.Child(), false);
        Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        using var local = Repository.OpenWithWindowsUser(temp.Child()); using var reader = local.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [WindowsFact]
    public void ProductCanRemoveEncryptionWithoutPasswordAndRetiresExperimentalRecovery()
    {
        using var temp = new TemporaryDirectory(); using var protector = new LegacyRecoveryFixture.Cleanup();
        using (var source = Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true))
        { EncryptionTests.Add(source); LegacyRecoveryFixture.Seed(source, protector); }
        Repository.ConvertEncryption(temp.Child(), false, options: EncryptionConversionTests.Options(temp.Root), legacyRecoveryCleanup: protector);
        Assert.Empty(protector.Keys); Assert.False(File.Exists(Path.Combine(temp.Child(), ExperimentalRecoverySidecar.FileName)));
        using var plain = Repository.Open(temp.Child()); using var reader = plain.OpenReader(); Assert.Single(reader.EnumerateCurrent());
    }

    [Fact]
    public void ProvenReusableCopyIsDiscardedWithoutRemovingCommittedHistory()
    {
        using var fixture = new ReuseFixture();
        using (var writer = fixture.Repository.OpenWriter()) using (var draft = ReuseFixture.Ready(writer)) { }
        Assert.Single(fixture.Descriptors);
        var result = Repository.ConvertEncryption(fixture.Repository.DirectoryPath, true, EncryptionTests.Password,
            EncryptionConversionTests.Options(fixture.Temp.Root));
        Assert.Equal(0, result.Versions); Assert.Empty(fixture.Descriptors);
        using var target = Repository.OpenWithPassword(fixture.Repository.DirectoryPath, EncryptionTests.Password);
        using var reader = target.OpenReader(); Assert.Single(reader.EnumerateSourceRoots()); Assert.Empty(reader.EnumerateCurrent());
    }

    [Theory]
    [InlineData("MarkerCandidateFlushed", true)] [InlineData("PayloadWritten", true)]
    [InlineData("AfterPayloadReplace", true)] [InlineData("MetadataSegmentCreated", true)]
    [InlineData("MetadataFlushed", true)] [InlineData("AfterBootstrapPivot", true)]
    [InlineData("AfterStatePivot", true)] [InlineData("AfterMarkerRemoval", true)]
    [InlineData("MarkerCandidateFlushed", false)] [InlineData("PayloadWritten", false)]
    [InlineData("AfterPayloadReplace", false)] [InlineData("MetadataSegmentCreated", false)]
    [InlineData("MetadataFlushed", false)] [InlineData("AfterBootstrapPivot", false)]
    [InlineData("AfterStatePivot", false)] [InlineData("AfterMarkerRemoval", false)]
    [InlineData("AfterFence", true)] [InlineData("AfterFence", false)]
    [InlineData("BeforeReady", true)] [InlineData("BeforeReady", false)]
    [InlineData("ReadyRecorded", true)] [InlineData("ReadyRecorded", false)]
    public async Task KilledMaintenanceOwnerReconcilesExactPhysicalCombination(string point, bool encrypted)
    {
        using var temp = new TemporaryDirectory(); string digest;
        using (var repository = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows()))
        {
            EncryptionTests.Add(repository); EncryptionTests.Add(repository, "second.txt", "second version"u8.ToArray());
            using var reader = repository.OpenReader(); digest = Repository.SemanticDigest(reader, default);
        }
        await using (var child = WriterProcess.Start($"m252-convert-{(encrypted ? "encrypt" : "decrypt")}-{point}", temp.Child()))
        { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        if (point != "AfterMarkerRemoval")
            Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        Assert.Null(Repository.InspectEncryptionMigration(temp.Child()));
        using var target = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
        using var after = target.OpenReader(); Assert.Equal(digest, Repository.SemanticDigest(after, default));
        foreach (var current in after.EnumerateCurrent())
        { using var payload = after.OpenVersionPayload(current.Identity)!; payload.Content.CopyTo(Stream.Null); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PausedMigrationBlocksOrdinaryOwnersAndResumesAfterCapacityReturns(bool encrypted)
    {
        using var temp = new TemporaryDirectory(); string digest;
        using (var repository = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, OperatingSystem.IsWindows()))
        { EncryptionTests.Add(repository); using var reader = repository.OpenReader(); digest = Repository.SemanticDigest(reader, default); }
        var free = 90_000_000_000L;
        var options = EncryptionConversionTests.Options(temp.Root) with
        {
            CapacityProbe = _ => new(100_000_000_000, free),
            Observed = stage => { if (stage == EncryptionConversionStage.MarkerInstalled) free = 0; }
        };
        Assert.Throws<EncryptionMigrationSpaceException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, options));
        Assert.Throws<EncryptionMigrationRequiredException>(() => Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password));
        var inspection = await new RepositorySetupService().InspectAsync(temp.Child());
        Assert.Equal(RepositoryReadiness.EncryptionMigrationRequired, inspection.State);
        Assert.NotNull(inspection.EncryptionChange); Assert.Equal(encrypted, inspection.EncryptionChange.TargetEncrypted);
        var sourcePath = Directory.CreateDirectory(temp.Child("source")).FullName;
        var result = await new EngineBackupAttemptRunner().RunAsync(new(temp.Child(), [new(new(FoundationTests.Root, "source"), sourcePath)]), AttemptTrigger.Manual, null, default);
        Assert.Equal(BackupOutcome.EncryptionMigrationRequired, result.Outcome); Assert.False(result.TimelineChanged);
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
        using var complete = encrypted ? Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password) : Repository.Open(temp.Child());
        using var after = complete.OpenReader(); Assert.Equal(digest, Repository.SemanticDigest(after, default));
    }

    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public void OutstandingPayloadHandlePausesWithoutForcingDeletion(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using var repository = encrypted ? Repository.Create(temp.Child()) : Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password, true);
        var version = EncryptionTests.Add(repository); using var reader = repository.OpenReader();
        using (var held = reader.OpenVersionPayload(version.Identity)!)
        {
            var before = File.ReadAllBytes(repository.GetPayloadPath(version.Identity.VersionId));
            Assert.Throws<IOException>(() => Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root)));
            Assert.Equal(before, File.ReadAllBytes(repository.GetPayloadPath(version.Identity.VersionId)));
            Assert.Throws<EncryptionMigrationRequiredException>(() => reader.OpenVersionPayload(version.Identity));
            held.Content.CopyTo(Stream.Null); // An already-open original handle remains valid; it cannot be replaced through.
        }
        Repository.ConvertEncryption(temp.Child(), encrypted, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
    }

    [Theory]
    [InlineData("source")] [InlineData("candidate")] [InlineData("metadata")] [InlineData("marker")]
    public void CorruptionNeverOverwritesTheAffectedValidatedSource(string kind)
    {
        using var temp = new TemporaryDirectory(); string payload;
        using (var repository = Repository.Create(temp.Child())) payload = repository.GetPayloadPath(EncryptionTests.Add(repository).Identity.VersionId);
        var original = File.ReadAllBytes(payload);
        var options = EncryptionConversionTests.Options(temp.Root) with
        {
            Observed = stage =>
            {
                if (kind == "source" && stage == EncryptionConversionStage.BeforePayload) { var bytes = File.ReadAllBytes(payload); bytes[0] ^= 1; File.WriteAllBytes(payload, bytes); }
                if (kind == "candidate" && stage == EncryptionConversionStage.PayloadFlushed)
                { var file = Directory.GetFiles(Path.GetDirectoryName(payload)!, "*.migration-*.tmp").Single(); var bytes = File.ReadAllBytes(file); bytes[^1] ^= 1; File.WriteAllBytes(file, bytes); }
                if (kind == "metadata" && stage == EncryptionConversionStage.MetadataFlushed)
                {
                    var control = EncryptionMigrationControl.Read(temp.Child()); var file = SegmentReader.SegmentPath(temp.Child(), control.SegmentId(1));
                    var bytes = File.ReadAllBytes(file); bytes[^1] ^= 1; File.WriteAllBytes(file, bytes);
                }
                if (kind == "marker" && stage == EncryptionConversionStage.MarkerInstalled)
                    File.WriteAllText(Path.Combine(temp.Child(), EncryptionMigrationControl.FileName), "malformed marker");
            }
        };
        Assert.ThrowsAny<Exception>(() => Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, options));
        Assert.Throws<EncryptionMigrationRequiredException>(() => Repository.Discover(temp.Child()));
        if (kind is "candidate" or "marker") Assert.Equal(original, File.ReadAllBytes(payload));
        else if (kind == "source") Assert.NotEqual(original, File.ReadAllBytes(payload));
        else
        {
            Repository.ConvertEncryption(temp.Child(), true, EncryptionTests.Password, EncryptionConversionTests.Options(temp.Root));
            using var recovered = Repository.OpenWithPassword(temp.Child(), EncryptionTests.Password);
        }
    }
}
