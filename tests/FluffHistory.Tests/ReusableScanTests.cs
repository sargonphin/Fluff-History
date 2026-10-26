using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

internal static class ReuseScanFixture
{
    internal static void Cache(LiveCapacityFixture f, string name = "a", string? content = null)
    {
        if (content is not null) f.File(name, content);
        var physical = Path.Combine(f.Source, name); var file = new FileInfo(physical);
        using var writer = f.Repository.OpenWriter(); using var draft = writer.CreatePayloadDraft();
        using var input = System.IO.File.OpenRead(physical); var buffer = new byte[128 * 1024]; var hash = new XxHash64(); int read;
        while ((read = input.Read(buffer)) > 0) { hash.Append(buffer.AsSpan(0, read)); draft.Write(buffer.AsSpan(0, read)); }
        draft.CompleteCopy(file.Length);
        Assert.True(writer.MarkPayloadReusable(draft, new(FoundationTests.Root, name), file.LastWriteTimeUtc, new(Convert.ToHexString(hash.GetCurrentHash()))));
    }
    internal static string[] Descriptors(LiveCapacityFixture f) => Directory.GetFiles(f.Repository.DirectoryPath, "*.ready", SearchOption.AllDirectories);
    internal static FileHistoryEngine Engine(LiveCapacityFixture f, SourceAccess? access = null, ScanOptions? options = null) =>
        new(f.Repository, options ?? new(), access ?? f.Access, f.Probe, new AllocationProbe());
}

public sealed class ReusableScanTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CancellationAfterStableReadyThenRestartAvoidsSourceAndUsesFreshMetadataAndStoredTime(bool encrypted)
    {
        using var f = new LiveCapacityFixture(encrypted); f.File(); using var cts = new CancellationTokenSource(); var first = ReuseScanFixture.Engine(f);
        first.ConfigureWriter = w => w.ReusableObserve = (stage, _) => { if (stage == ReusablePayloadStage.Ready) cts.Cancel(); };
        var cancelled = f.Scan(first, cts.Token);
        Assert.True(cancelled.Cancelled); Assert.Equal(0, cancelled.VersionsCreated); Assert.Equal(1, cancelled.Reuse.FreshCopiesMarkedReady);
        Assert.Equal(1, cancelled.Reuse.ReadyDraftsPreserved); Assert.Single(ReuseScanFixture.Descriptors(f));
        using (var reader = f.Repository.OpenReader()) Assert.Null(reader.GetNewestVersion(new(FoundationTests.Root, "a")));
        f.Access.BeforeOpen = _ => throw new InvalidOperationException("Reuse must not open source content.");
        var creation = FoundationTests.Time.AddYears(1);
        f.Access.Metadata = (_, o) => o.Directory ? o : o with { CreationUtc = creation, Attributes = FileAttributes.ReadOnly };
        var engine = ReuseScanFixture.Engine(f); engine.Clock = () => FoundationTests.Time.AddYears(2);
        var reused = f.Scan(engine);
        Assert.Equal(1, reused.Reuse.Lookups); Assert.Equal(1, reused.Reuse.Hits); Assert.Equal(0, reused.Reuse.Misses);
        Assert.Equal(5, reused.Reuse.LocalValidationBytes); Assert.Equal(5, reused.Reuse.ReusedPayloadBytes); Assert.Equal(5, reused.SourceContentBytesAvoided);
        Assert.Equal(0, reused.SourceBytesRead); Assert.Equal(0, reused.ContentStreamsOpened); Assert.Equal(0, reused.PayloadBytesWritten);
        Assert.Equal(0, reused.Reuse.AdoptionPayloadCopyBytes); Assert.Equal(1, reused.Reuse.Adoptions); Assert.Equal(1, reused.VersionsCreated);
        using var fresh = f.Repository.OpenReader(); var version = fresh.GetCurrent(new(FoundationTests.Root, "a"))!;
        Assert.Equal(creation, version.Metadata.CreationTimeUtc); Assert.Equal(FileAttributes.ReadOnly, version.Metadata.Attributes);
        Assert.Equal(FoundationTests.Time.AddYears(2), version.StoredUtc); Assert.Empty(ReuseScanFixture.Descriptors(f));
        var unchanged = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, unchanged.Unchanged); Assert.Equal(0, unchanged.Reuse.Lookups);
    }
    [Theory]
    [InlineData("path")] [InlineData("mtime")] [InlineData("length")] [InlineData("corrupt")] [InlineData("descriptor")] [InlineData("conflict")] [InlineData("none")]
    public void MissesAndBadOptionalStateFallBackToOrdinaryOnePassCopy(string fault)
    {
        using var f = new LiveCapacityFixture(); var physical = f.File();
        if (fault != "none") ReuseScanFixture.Cache(f);
        switch (fault)
        {
            case "path": System.IO.File.Move(physical, Path.Combine(f.Source, "other")); break;
            case "mtime": System.IO.File.SetLastWriteTimeUtc(physical, FoundationTests.Time.AddDays(1).UtcDateTime); break;
            case "length": System.IO.File.WriteAllText(physical, "longer"); break;
            case "corrupt": System.IO.File.WriteAllText(Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories).Single(), "jello"); break;
            case "descriptor": System.IO.File.WriteAllText(ReuseScanFixture.Descriptors(f).Single(), "broken"); break;
            case "conflict":
                ReuseScanFixture.Cache(f, content: "jello"); f.File(content: "hello"); break;
        }
        var result = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, result.VersionsCreated); Assert.Equal(1, result.ContentStreamsOpened);
        Assert.Equal(result.SourceBytesRead, result.PayloadBytesWritten); Assert.Equal(0, result.SourceSkips); Assert.Equal(0, result.Reuse.Adoptions);
        Assert.Equal(1, result.Reuse.Misses); Assert.Equal(fault == "conflict" ? 1 : 0, result.Reuse.Conflicts);
    }
    [Fact]
    public void CurrentAndSurvivingHistoryHavePriorityOverCache()
    {
        using var f = new LiveCapacityFixture(); f.Seed(); ReuseScanFixture.Cache(f);
        var unchanged = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, unchanged.Unchanged); Assert.Equal(0, unchanged.Reuse.Lookups);
        using (var writer = f.Repository.OpenWriter()) using (var reader = f.Repository.OpenReader())
            writer.Apply(new(currentRemovals: [new(FoundationTests.Root, "a")]), reader.Authority);
        var reactivated = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, reactivated.Reactivations); Assert.Equal(0, reactivated.Reuse.Lookups);
        Assert.Equal(0, reactivated.VersionsCreated); Assert.Equal(0, reactivated.ContentStreamsOpened);
    }
    [Fact]
    public void SourceMutationAfterValidationRetriesWithoutPublishingStaleCopy()
    {
        using var f = new LiveCapacityFixture(); var physical = f.File(); ReuseScanFixture.Cache(f); var changed = false;
        f.Access.BeforeObserve = p =>
        {
            if (p == physical && !changed && f.Access.Streams == 0)
            {
                // The first observation is for the file decision; the second follows local validation.
                if (++_observations == 2) { changed = true; f.File(content: "different", day: 1); }
            }
        };
        var result = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, result.RetryAttempts); Assert.Equal(0, result.Reuse.Adoptions);
        Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(1, result.VersionsCreated); Assert.Equal(0, result.SourceSkips);
        Assert.True(changed); Assert.Single(ReuseScanFixture.Descriptors(f)); // Old exact copy remains discardable, never history.
        using var reader = f.Repository.OpenReader(); Assert.Equal(9, reader.GetCurrent(new(FoundationTests.Root, "a"))!.Metadata.Length);
    }
    private int _observations;
    [Fact]
    public void PartialCancellationNeverCreatesReadyAndNextScanRecopies()
    {
        using var f = new LiveCapacityFixture(); f.File(content: new string('a', 300_000)); using var cts = new CancellationTokenSource();
        var engine = ReuseScanFixture.Engine(f); engine.ConfigureWriter = w => w.PayloadObserve = (stage, _) => { if (stage == PayloadStage.BeforeWrite) cts.Cancel(); };
        var result = f.Scan(engine, cts.Token); Assert.True(result.Cancelled); Assert.Equal(0, result.Reuse.FreshCopiesMarkedReady);
        Assert.Empty(ReuseScanFixture.Descriptors(f)); var next = f.Scan(ReuseScanFixture.Engine(f));
        Assert.Equal(1, next.ContentStreamsOpened); Assert.Equal(0, next.Reuse.Adoptions); Assert.Equal(300_000, next.SourceBytesRead);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublicationFailureRequiresExactReconciliationThenUsesHistoryOrReusableCopy(bool afterReplace)
    {
        using var f = new LiveCapacityFixture(); f.File(); var engine = ReuseScanFixture.Engine(f);
        engine.ConfigureWriter = w => w.Observe = stage =>
        {
            if (stage == (afterReplace ? PublicationStage.AfterReplace : PublicationStage.BeforeMetadataInstall))
            { if (afterReplace) w.LoseHandleForTest(); else throw new IOException("definite unpublished failure"); }
        };
        Assert.ThrowsAny<IOException>(() => f.Scan(engine)); Assert.Empty(ReuseScanFixture.Descriptors(f));
        using (var writer = f.Repository.OpenWriter())
        {
            using var reader = f.Repository.OpenReader();
            var tx = afterReplace ? reader.Authority.TransactionId : Assert.Single(writer.EnumeratePendingPublications());
            Assert.Equal(afterReplace ? PublicationOutcome.Published : PublicationOutcome.Unpublished, writer.Reconcile(tx));
        }
        f.Access.BeforeOpen = _ => throw new InvalidOperationException("Restart must not reread source content.");
        var next = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(afterReplace ? 1 : 0, next.Unchanged);
        Assert.Equal(afterReplace ? 0 : 1, next.Reuse.Adoptions); Assert.Equal(0, next.ContentStreamsOpened);
    }
    [Theory]
    [InlineData(RetentionPolicy.UntilSaturation)] [InlineData(RetentionPolicy.Forever)] [InlineData(RetentionPolicy.OneMonth)]
    public void DisposableCacheIsEvictedBeforeAnyHistoryAndActualCapacityIsReprobed(RetentionPolicy policy)
    {
        using var f = new LiveCapacityFixture(); f.Seed(); f.Seed(content: "newer", day: 1); f.File("cache"); ReuseScanFixture.Cache(f, "cache");
        System.IO.File.Delete(Path.Combine(f.Source, "cache")); f.File("b"); f.Probe.Free = 0;
        var engine = f.Engine(policy); engine.ConfigureWriter = w => w.ReusableObserve = (stage, _) => { if (stage == ReusablePayloadStage.Evicted) f.Probe.Free = 900_000_000; };
        var result = f.Scan(engine); Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(1, result.Reuse.CapacityEvictions);
        Assert.Equal(1, result.Reuse.PayloadFilesDeleted); Assert.Equal(5, result.Reuse.DeletedLogicalBytes);
        Assert.Equal(0, result.Capacity.HistoryScans); Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(1, result.VersionsCreated);
        Assert.Equal(1, result.WriterAcquisitions); using var reader = f.Repository.OpenReader(); Assert.Equal(2, reader.EnumerateVersions(new(FoundationTests.Root, "a")).Count());
    }
    [Theory]
    [InlineData(RetentionPolicy.Forever)] [InlineData(RetentionPolicy.OneYear)]
    public void InsufficientAfterCacheEvictionPreservesAllHistory(RetentionPolicy policy)
    {
        using var f = new LiveCapacityFixture(); f.Seed(); f.Seed(content: "newer", day: 1); f.File("b"); ReuseScanFixture.Cache(f, "b"); f.Probe.Free = 0;
        var result = f.Scan(f.Engine(policy)); Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome);
        Assert.Equal(1, result.Reuse.CapacityEvictions); Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(0, result.ContentStreamsOpened);
        Assert.Equal(0, result.Capacity.HistoryScans); using var reader = f.Repository.OpenReader(); Assert.Equal(2, reader.EnumerateVersions(new(FoundationTests.Root, "a")).Count());
    }
    [Fact]
    public void ReusedDataIsZeroFutureAllocationButPublicationStillRequiresHeadroom()
    {
        using var f = new LiveCapacityFixture(); f.File(content: new string('a', 8 * 1024 * 1024)); ReuseScanFixture.Cache(f);
        long publication;
        using (var writer = f.Repository.OpenWriter(null, streamMetadataReads: true)) publication = writer.EstimatePublicationAllocation(new(Versions: 1), 4096).TotalBytes;
        f.Probe.Free = publication;
        var result = f.Scan(f.Engine(RetentionPolicy.Forever)); Assert.Equal(ScanOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Capacity.MaximumPayloadAllocation); Assert.Equal(publication, result.Capacity.MaximumPublicationAllocation);
        Assert.Equal(8 * 1024 * 1024, result.SourceContentBytesAvoided); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(0, result.Reuse.CapacityEvictions);
    }
    [Fact]
    public void CachePressureCanFallThroughToExistingHistoryRetentionAfterActualReprobe()
    {
        using var f = new LiveCapacityFixture(); f.Seed(); f.Seed(content: "newer", day: 1); f.File("cache"); ReuseScanFixture.Cache(f, "cache");
        System.IO.File.Delete(Path.Combine(f.Source, "cache")); f.Probe.Free = 0; var events = new List<string>();
        var engine = f.Engine(); engine.ConfigureWriter = w =>
        {
            w.ReusableObserve = (stage, _) => { if (stage == ReusablePayloadStage.Evicted) events.Add("cache"); };
            w.PayloadCleanupObserve = (stage, _) => { if (stage == PayloadCleanupStage.Deleted) { events.Add("history"); f.Probe.Free = 900_000_000; } };
        };
        var result = f.Scan(engine); Assert.Equal(new[] { "cache", "history" }, events); Assert.Equal(1, result.Capacity.VersionsRemoved); Assert.Equal(1, result.Capacity.HistoryScans);
    }
    [Fact]
    public void LargeReusableFilePublishesAloneWithBoundedValidationAndNoDuplicateDataAllocation()
    {
        using var f = new LiveCapacityFixture(); var physical = f.File();
        using (var stream = new FileStream(physical, FileMode.Create, FileAccess.Write)) stream.SetLength(70L * 1024 * 1024);
        System.IO.File.SetLastWriteTimeUtc(physical, FoundationTests.Time.UtcDateTime); ReuseScanFixture.Cache(f);
        f.File("b"); var result = f.Scan(ReuseScanFixture.Engine(f));
        Assert.Equal(70L * 1024 * 1024, result.Reuse.ReusedPayloadBytes); Assert.Equal(70L * 1024 * 1024, result.Reuse.LocalValidationBytes);
        Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(5, result.PayloadBytesWritten); Assert.Equal(0, result.Reuse.AdoptionPayloadCopyBytes);
        Assert.Equal(2, result.BatchesCommitted); Assert.Equal(1, result.PeakPendingPayloadCount); Assert.Equal(70L * 1024 * 1024, result.PeakPendingPayloadBytes);
    }
    [Fact]
    public void ByteFlushPostPublicationRetentionReobservesBeforeSelectingOrOpeningNextFile()
    {
        using var f = new LiveCapacityFixture(); f.Seed("retired", current: false); System.IO.File.Delete(Path.Combine(f.Source, "retired"));
        f.File("a", "aaa"); f.File("b", "bbb"); var engine = f.Engine(byteTarget: 5);
        engine.BatchCommitted = r => { if (r.VersionsCreated == 1) f.Probe.Free = 0; };
        engine.ConfigureWriter = w => w.PayloadCleanupObserve = (stage, _) =>
        { if (stage == PayloadCleanupStage.Deleted) { f.File("b", "fresh", day: 1); f.Probe.Free = 900_000_000; } };
        var result = f.Scan(engine); Assert.Equal(2, result.VersionsCreated); Assert.Equal(2, result.ContentStreamsOpened);
        Assert.Equal(0, result.RetryAttempts); Assert.Equal(8, result.SourceBytesRead); Assert.Equal(1, result.Capacity.VersionsRemoved);
        using var reader = f.Repository.OpenReader(); Assert.Equal(5, reader.GetCurrent(new(FoundationTests.Root, "b"))!.Metadata.Length);
    }
    [Fact]
    public void AdoptedPendingCopyCannotAuthorizeHistoryRetentionWhenFinalSpaceFalls()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed(current: false); f.File(content: "changed", day: 1); ReuseScanFixture.Cache(f);
        var engine = f.Engine(); engine.ConfigureWriter = w => w.ReusableObserve = (stage, _) => { if (stage == ReusablePayloadStage.Adopted) f.Probe.Free = 0; };
        var result = f.Scan(engine); Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome); Assert.Equal(1, result.Reuse.Adoptions);
        Assert.Equal(0, result.Capacity.HistoryScans); Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(0, result.ContentStreamsOpened);
        Assert.Equal(0, result.VersionsCreated); Assert.Equal(1, result.Reuse.ReadyDraftsPreserved);
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity)); Assert.Null(reader.GetCurrent(new(FoundationTests.Root, "a")));
    }
    [Fact]
    public void OptionalEvidenceCorruptedBetweenLocalValidationAndAdoptionFallsBackToSource()
    {
        using var f = new LiveCapacityFixture(); var physical = f.File(); ReuseScanFixture.Cache(f); int observes = 0;
        f.Access.BeforeObserve = p =>
        { if (p == physical && ++observes == 2) System.IO.File.WriteAllText(ReuseScanFixture.Descriptors(f).Single(), "bad optional evidence"); };
        var result = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(1, result.VersionsCreated);
        Assert.Equal(0, result.SourceSkips); Assert.Equal(0, result.Reuse.Adoptions); Assert.True(result.Reuse.Rejected > 0);
    }
    [Fact]
    public void UnrelatedAuthorityAdvancementConservativelyInvalidatesReadyProofWithoutTouchingArtifact()
    {
        using var f = new LiveCapacityFixture(); f.File(); ReuseScanFixture.Cache(f);
        var artifact = Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories).Single();
        using (var writer = f.Repository.OpenWriter()) using (var reader = f.Repository.OpenReader())
            writer.Apply(new(sourceRoots: [new(Guid.NewGuid(), "unrelated")]), reader.Authority);
        var result = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(0, result.Reuse.Adoptions); Assert.Equal(1, result.ContentStreamsOpened);
        Assert.True(System.IO.File.Exists(artifact)); Assert.Equal(1, result.VersionsCreated);
    }
    [WindowsFact]
    public void DeletedCachePayloadIsReprobedEvenWhenDescriptorCleanupIsSharingDenied()
    {
        using var f = new LiveCapacityFixture(); f.Seed(); f.Seed(content: "newer", day: 1); f.File("cache"); ReuseScanFixture.Cache(f, "cache");
        System.IO.File.Delete(Path.Combine(f.Source, "cache")); f.File("b"); f.Probe.Free = 0;
        var artifact = Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories).Single();
        using var hold = new FileStream(ReuseScanFixture.Descriptors(f).Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
        f.Probe.Before = (_, _) => { if (!System.IO.File.Exists(artifact)) f.Probe.Free = 900_000_000; };
        var result = f.Scan(f.Engine()); Assert.Equal(ScanOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Reuse.CapacityEvictions); Assert.Equal(1, result.Reuse.PayloadFilesDeleted); Assert.True(result.Reuse.CleanupDeferred > 0);
        Assert.Equal(0, result.Capacity.HistoryScans); Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.False(System.IO.File.Exists(artifact));
    }
    [Fact]
    public void OptionalCacheCreationFailureDoesNotPreventOrdinarySuccessfulBackup()
    {
        using var f = new LiveCapacityFixture(); f.File(); System.IO.File.WriteAllText(Path.Combine(f.Repository.DirectoryPath, "cache"), "foreign optional topology");
        var result = f.Scan(ReuseScanFixture.Engine(f)); Assert.Equal(1, result.VersionsCreated); Assert.Equal(0, result.Reuse.FreshCopiesMarkedReady);
        Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(0, result.SourceSkips);
    }
    [Theory]
    [InlineData("reusable-b-partial")] [InlineData("reusable-b-ready")] [InlineData("reusable-b-revoked")]
    [InlineData("reusable-b-prepared")] [InlineData("reusable-b-installed")] [InlineData("reusable-b-published")]
    public async Task IntegratedProcessDeathRecoversOnlyProvenCompletedUnpublishedCopies(string boundary)
    {
        using var f = new LiveCapacityFixture(); f.File("foo.txt");
        await using (var child = WriterProcess.Start(boundary, f.Repository.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        using (var writer = f.Repository.OpenWriter())
        {
            using var reader = f.Repository.OpenReader();
            foreach (var tx in writer.EnumeratePendingPublications().ToArray()) Assert.Equal(PublicationOutcome.Unpublished, writer.Reconcile(tx));
            if (boundary == "reusable-b-published") Assert.Equal(PublicationOutcome.Published, writer.Reconcile(reader.Authority.TransactionId));
        }
        var result = f.Scan(ReuseScanFixture.Engine(f));
        Assert.Equal(boundary is "reusable-b-ready" or "reusable-b-prepared" or "reusable-b-installed" ? 1 : 0, result.Reuse.Adoptions);
        Assert.Equal(boundary is "reusable-b-partial" or "reusable-b-revoked" ? 1 : 0, result.ContentStreamsOpened);
        Assert.Equal(boundary == "reusable-b-published" ? 1 : 0, result.Unchanged);
        using var fresh = f.Repository.OpenReader(); Assert.Single(fresh.EnumerateVersions(ReuseFixture.PathKey));
    }
}
