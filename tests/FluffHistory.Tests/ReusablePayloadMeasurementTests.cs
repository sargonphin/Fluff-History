using System.Diagnostics;
using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class ReusablePayloadMeasurementTests(ITestOutputHelper output)
{
    [MetadataDiagnosticTheory]
    [Trait("Category", "MetadataDiagnostic")]
    [InlineData("cancelled-ready")] [InlineData("exact-hit")] [InlineData("source-changed")]
    [InlineData("corrupt-local")] [InlineData("installed-unpublished")]
    [InlineData("reconcile-unpublished")] [InlineData("reconcile-published")]
    [InlineData("cache-before-history")] [InlineData("forever-cache-blocked")] [InlineData("large-reusable")]
    public void RealPayloadLifecycleAndCapacity(string scenario)
    {
        using var f = new LiveCapacityFixture(); f.File(); ScanResult? cancelled = null; ReusablePayloadStatistics? transaction = null, transactionAttempt = null;
        CacheInventory? beforeReconciliation = null;
        if (scenario is "cache-before-history" or "forever-cache-blocked")
        {
            f.Seed(); f.Seed(content: "newer", day: 1); f.File("cache"); ReuseScanFixture.Cache(f, "cache");
            System.IO.File.Delete(Path.Combine(f.Source, "cache")); f.File("b"); f.Probe.Free = 0;
        }
        else if (scenario == "large-reusable")
        {
            var physical = Path.Combine(f.Source, "a");
            using (var stream = new FileStream(physical, FileMode.Create, FileAccess.Write)) stream.SetLength(96L * 1024 * 1024);
            System.IO.File.SetLastWriteTimeUtc(physical, FoundationTests.Time.UtcDateTime); ReuseScanFixture.Cache(f);
        }
        else
        {
            using var cts = new CancellationTokenSource(); var first = ReuseScanFixture.Engine(f);
            first.ConfigureWriter = w => w.ReusableObserve = (stage, _) => { if (stage == ReusablePayloadStage.Ready) cts.Cancel(); };
            cancelled = f.Scan(first, cts.Token); Assert.True(cancelled.Cancelled); Assert.Equal(0, cancelled.VersionsCreated);
            if (scenario == "source-changed") f.File(content: "new content", day: 1);
            if (scenario == "corrupt-local") System.IO.File.WriteAllText(Directory.GetFiles(f.Repository.DirectoryPath, "*.part", SearchOption.AllDirectories).Single(), "jello");
            if (scenario is "installed-unpublished" or "reconcile-unpublished" or "reconcile-published")
            {
                Guid tx;
                using (var writer = f.Repository.OpenWriter())
                {
                    using var candidate = writer.FindReusablePayload(new(FoundationTests.Root, "a"), 5, FoundationTests.Time);
                    Assert.NotNull(candidate); using var draft = writer.AdoptReusablePayload(candidate);
                    var physical = new FileInfo(Path.Combine(f.Source, "a")); using var reader = f.Repository.OpenReader();
                    var version = draft.PrepareVersion(new(FoundationTests.Root, "a", 5, physical.CreationTimeUtc, physical.LastWriteTimeUtc, FileAttributes.Archive),
                        FoundationTests.Time, candidate.Checksum);
                    using var publication = writer.PrepareMutation(new([version]), reader.Authority); tx = publication.IntendedAuthority.TransactionId; draft.Install();
                    if (scenario != "installed-unpublished")
                    {
                        writer.Observe = stage =>
                        {
                            if (scenario == "reconcile-unpublished" && stage == PublicationStage.BeforeMetadataInstall) throw new IOException("injected unpublished");
                            if (scenario == "reconcile-published" && stage == PublicationStage.AfterReplace) writer.LoseHandleForTest();
                        };
                        Assert.ThrowsAny<IOException>(() => publication.Commit());
                    }
                    transaction = writer.ReusableStatistics;
                }
                transactionAttempt = transaction; beforeReconciliation = Inventory(f.Repository);
                if (scenario != "installed-unpublished")
                {
                    using var writer = f.Repository.OpenWriter();
                    Assert.Equal(scenario == "reconcile-published" ? PublicationOutcome.Published : PublicationOutcome.Unpublished, writer.Reconcile(tx));
                    transaction = writer.ReusableStatistics;
                }
            }
        }
        var before = Inventory(f.Repository); using var capture = f.Repository.OpenReader();
        var engine = scenario is "cache-before-history" or "forever-cache-blocked" ? f.Engine(scenario == "cache-before-history" ? RetentionPolicy.UntilSaturation : RetentionPolicy.Forever) : ReuseScanFixture.Engine(f);
        if (scenario == "cache-before-history") engine.ConfigureWriter = w => w.ReusableObserve = (stage, _) => { if (stage == ReusablePayloadStage.Evicted) f.Probe.Free = 900_000_000; };
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var result = f.Scan(engine); watch.Stop();
        var allocation = GC.GetTotalAllocatedBytes(true) - allocated; var live = GC.GetTotalMemory(false); var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Assert.Equal(0, result.Reuse.AdoptionPayloadCopyBytes); Assert.Equal(1, result.WriterAcquisitions);
        if (scenario is "cancelled-ready" or "exact-hit" or "installed-unpublished" or "reconcile-unpublished" or "large-reusable")
        { Assert.Equal(1, result.Reuse.Adoptions); Assert.Equal(0, result.ContentStreamsOpened); Assert.Equal(result.Reuse.ReusedPayloadBytes, result.SourceContentBytesAvoided); }
        if (scenario is "source-changed" or "corrupt-local") { Assert.Equal(1, result.ContentStreamsOpened); Assert.Equal(0, result.SourceSkips); }
        if (scenario == "reconcile-published") { Assert.Equal(1, result.Unchanged); Assert.Equal(0, result.Reuse.Lookups); }
        if (scenario is "cache-before-history" or "forever-cache-blocked")
        { Assert.Equal(1, result.Reuse.CapacityEvictions); Assert.Equal(0, result.Capacity.VersionsRemoved); Assert.Equal(0, result.Capacity.HistoryScans); }
        if (scenario == "forever-cache-blocked") Assert.Equal(ScanOutcome.CapacityBlocked, result.Outcome);
        else Assert.Equal(ScanOutcome.Completed, result.Outcome);
        output.WriteLine("M17_JSON " + JsonSerializer.Serialize(new
        {
            Scenario = scenario, Model = "real native Windows source/payload I/O; capacity cases inject C/F and4096-byte separate-workspace geometry; no SMB/quota claim",
            Before = before, After = Inventory(f.Repository), CancelledAttempt = cancelled,
            TransactionAttempt = transactionAttempt, BeforeExplicitReconciliation = beforeReconciliation, ReconciliationOrDiscard = transaction,
            Scan = result, AuthorityGenerations = result.Authority.Generation - capture.Authority.Generation,
            CumulativeAllocatedBytes = allocation, ManagedLiveSampleBytes = live, ProcessLifetimePeakBytes = peak, ElapsedMilliseconds = watch.ElapsedMilliseconds,
            HarnessInventoriesScanCache = true, AdoptionPayloadContentBytesCopied = result.Reuse.AdoptionPayloadCopyBytes
        }));
    }
    [MetadataDiagnosticFact]
    [Trait("Category", "MetadataDiagnostic")]
    public void FiveThousandReadyEntriesLookupOneExactBucket()
    {
        const int count = 5000; using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter();
        for (var i = 0; i < count; i++) ReuseFixture.Ready(writer, [42], new(FoundationTests.Root, $"cache/{i:D5}")).Dispose();
        var inventory = Inventory(f.Repository); Assert.Equal(count, inventory.Ready);
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        using var candidate = writer.FindReusablePayload(new(FoundationTests.Root, "cache/04321"), 1, FoundationTests.Time); Assert.NotNull(candidate);
        using var draft = writer.AdoptReusablePayload(candidate); watch.Stop();
        Assert.Equal(1, writer.ReusableStatistics.BucketEntriesExamined); Assert.Equal(0, writer.ReusableStatistics.GlobalEntriesExamined);
        Assert.Equal(1, writer.ReusableStatistics.LocalValidationBytes); Assert.Equal(0, writer.ReusableStatistics.AdoptionPayloadCopyBytes);
        output.WriteLine("M17_JSON " + JsonSerializer.Serialize(new
        {
            Scenario = "cache-scale5000", Before = inventory, Reuse = writer.ReusableStatistics,
            SourceContentStreamsOpened = 0, SourceContentBytesRead = 0, SourceContentBytesAvoided = (long?)null,
            AdoptionPayloadContentBytesCopied = 0, WriterAcquisitions = 1, AuthorityGenerations = 0,
            CumulativeLookupAndAdoptionAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, ManagedLiveSampleBytes = GC.GetTotalMemory(false),
            ProcessLifetimePeakBytes = Process.GetCurrentProcess().PeakWorkingSet64, ElapsedMilliseconds = watch.ElapsedMilliseconds,
            HarnessInventoriesScanCache = true, Model = "5000 real one-byte payloads and Ready descriptors; only one exact bucket is queried; no source scan or publication"
        }));
    }
    internal sealed record CacheInventory(int Ready, int Bound, long DescriptorBytes);
    internal static CacheInventory Inventory(Repository repository)
    {
        var cache = Path.Combine(repository.DirectoryPath, "cache"); if (!Directory.Exists(cache)) return new(0, 0, 0);
        var ready = Directory.GetFiles(cache, "*.ready", SearchOption.AllDirectories); var bound = Directory.GetFiles(cache, "*.bound", SearchOption.AllDirectories);
        return new(ready.Length, bound.Length, ready.Concat(bound).Sum(p => new FileInfo(p).Length));
    }
}
