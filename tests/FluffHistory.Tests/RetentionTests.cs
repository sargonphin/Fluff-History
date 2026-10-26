using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class RetentionTests
{
    internal static readonly DateTimeOffset Evaluation = FoundationTests.Time.AddDays(1000);
    internal static StoredVersion Add(Repository repo, string name, DateTimeOffset stored, Guid? root = null)
    {
        using var writer = repo.OpenWriter(); using var reader = repo.OpenReader();
        var id = root ?? FoundationTests.Root;
        var version = writer.PrepareVersion(new(id, name, 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive), stored, new("D24EC4F1A98C6E5B"));
        var payload = writer.GetPreparedPayloadPath(version); Directory.CreateDirectory(Path.GetDirectoryName(payload)!); File.WriteAllBytes(payload, [42]);
        writer.Apply(new([version], sourceRoots: [new(id, "does-not-exist")]), reader.Authority);
        using var fresh = repo.OpenReader(); return fresh.GetCurrent(version.Path)!;
    }
    internal static void RemoveCurrent(Repository repo, params StoredVersion[] versions)
    {
        using var writer = repo.OpenWriter(); using var reader = repo.OpenReader();
        writer.Apply(new(currentRemovals: versions.Select(v => v.Identity.Path)), reader.Authority);
    }
    internal static RetentionPreview Preview(Repository repo) => new RetentionEngine(repo).Preview(RetentionPolicy.OneMonth, Evaluation);
    internal static Dictionary<string, (long, DateTime)> Inventory(Repository repo) => Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));

    [Theory]
    [InlineData(RetentionPolicy.OneMonth, 30)] [InlineData(RetentionPolicy.ThreeMonths, 90)]
    [InlineData(RetentionPolicy.SixMonths, 180)] [InlineData(RetentionPolicy.NineMonths, 270)]
    [InlineData(RetentionPolicy.OneYear, 365)] [InlineData(RetentionPolicy.TwoYears, 730)]
    public void ExactlyElapsedDays(RetentionPolicy policy, int days)
    { Assert.Equal(Evaluation.Ticks - days * TimeSpan.TicksPerDay, new RetentionEvaluation(policy, Evaluation).CutoffUtc!.Value.Ticks); }

    [Fact]
    public void UnsupportedInvalidNonUtcAndUnrepresentableCutoffsAreRejected()
    {
        Assert.Equal(8, Enum.GetValues<RetentionPolicy>().Length);
        Assert.Throws<NotSupportedException>(() => new RetentionEvaluation(RetentionPolicy.UntilSaturation, Evaluation));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionEvaluation((RetentionPolicy)999, Evaluation));
        Assert.Throws<ArgumentException>(() => new RetentionEvaluation(RetentionPolicy.OneMonth, default));
        Assert.Throws<ArgumentException>(() => new RetentionEvaluation(RetentionPolicy.OneMonth, Evaluation.ToOffset(TimeSpan.FromHours(2))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionEvaluation(RetentionPolicy.OneMonth, DateTimeOffset.MinValue.AddTicks(1)));
        Assert.Equal(DateTimeOffset.MinValue, new RetentionEvaluation(RetentionPolicy.OneMonth, DateTimeOffset.MinValue.AddDays(30)).CutoffUtc);
        Assert.Equal(DateTimeOffset.MaxValue.AddDays(-730), new RetentionEvaluation(RetentionPolicy.TwoYears, DateTimeOffset.MaxValue).CutoffUtc);
    }

    [Fact]
    public void StrictTickBoundaryFutureClockRollbackAndAncientCurrent()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var cutoff = Evaluation.AddDays(-30);
        var before = Add(repo, "old", cutoff.AddTicks(-1));
        var equal = Add(repo, "equal", cutoff); var after = Add(repo, "after", cutoff.AddTicks(1)); var future = Add(repo, "future", Evaluation.AddYears(1));
        var current = Add(repo, "protected", cutoff.AddYears(-1));
        RemoveCurrent(repo, before, equal, after, future);
        var rollback = Add(repo, "old", cutoff.AddDays(-5)); RemoveCurrent(repo, rollback);
        var preview = Preview(repo); Assert.True(preview.Complete); Assert.Equal(6, preview.Examined); Assert.Equal(1, preview.Protected); Assert.Equal(2, preview.Eligible);
        var result = new RetentionEngine(repo).Execute(preview); Assert.True(result.Complete); Assert.Equal(2, result.Removed);
        using var reader = repo.OpenReader(); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
        Assert.Null(reader.GetVersion(before.Identity)); Assert.Null(reader.GetVersion(rollback.Identity));
        Assert.NotNull(reader.GetVersion(equal.Identity)); Assert.NotNull(reader.GetVersion(after.Identity)); Assert.NotNull(reader.GetVersion(future.Identity));
    }

    [Fact]
    public void SupersededDeletedRenamedAndUnprotectedHistoriesExpireWithoutReadingLiveSource()
    {
        using var f = new HistoryFixture(); var repo = f.Repository;
        var old = f.Add("old-name"); var older = f.Add("old-name", day: -3); var current = f.Add("old-name", day: -100);
        var deleted = f.Add("deleted"); f.Remove("deleted");
        var renamed = f.Add("renamed-away"); f.Remove("renamed-away"); var newName = f.Add("renamed-to", day: -100);
        var unprotectedRoot = Guid.NewGuid(); var unprotected = f.Add("unprotected", root: unprotectedRoot);
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader())
            writer.Apply(new(currentRemovals: [unprotected.Identity.Path], sourceRoots: [new(unprotectedRoot, "missing", false)]), reader.Authority);
        var preview = Preview(repo); Assert.Equal(2, preview.Protected); Assert.Equal(5, preview.Eligible);
        var result = new RetentionEngine(repo).Execute(preview); Assert.Equal(5, result.Removed);
        using var fresh = repo.OpenReader();
        foreach (var version in new[] { old, older, deleted, renamed, unprotected }) Assert.Null(fresh.GetVersion(version.Identity));
        Assert.Equal(current, fresh.GetCurrent(current.Identity.Path)); Assert.Equal(newName, fresh.GetCurrent(newName.Identity.Path));
    }

    [Fact]
    public void PreviewRequiresNoWriterPayloadAccessWorkspaceOrRepositoryWrites()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        var inventory = Inventory(f.Repository); var state = File.ReadAllBytes(Path.Combine(f.Repository.DirectoryPath, "state.json"));
        using var heldWriter = f.Repository.OpenWriter();
        using var locked = new FileStream(f.Repository.GetPayloadPath(old.Identity.VersionId), FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = f.Repository.OpenComparisonReader(f.Temp.Child("never-created"));
        var preview = new RetentionEngine(f.Repository).Preview(reader, new(RetentionPolicy.OneMonth, Evaluation));
        Assert.True(preview.Complete); Assert.Equal(1, preview.Eligible); Assert.Null(reader.Workspace!.CreatedDirectory);
        var cancelled = new RetentionEngine(f.Repository).Preview(reader, preview.Evaluation, new(true));
        Assert.True(cancelled.Cancelled); Assert.Null(cancelled.Reads); Assert.Equal(0, cancelled.Examined);
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(f.Repository.DirectoryPath, "state.json"))); Assert.Equal(inventory, Inventory(f.Repository));
    }

    [Fact]
    public void ForeverDoesNotScanMutateOrCleanPendingObligations()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        using (var writer = f.Repository.OpenWriter()) using (var reader = f.Repository.OpenReader()) writer.RemoveVersions([old.Identity], reader.Authority, false);
        var before = Inventory(f.Repository); var engine = new RetentionEngine(f.Repository);
        var preview = engine.Preview(RetentionPolicy.Forever, Evaluation); Assert.True(preview.Complete); Assert.Null(preview.Reads); Assert.Equal(0, preview.Examined);
        using var captured = f.Repository.OpenReader(); var page = engine.GetCandidates(captured, preview.Evaluation);
        Assert.True(page.Complete); Assert.Empty(page.Items);
        var result = engine.Execute(preview); Assert.True(result.Complete); Assert.Equal(0, result.AgeScans); Assert.Equal(0, result.Removed); Assert.Equal(0, result.Cleanup.Inspected);
        Assert.True(File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId))); Assert.Equal(before, Inventory(f.Repository));
        Assert.Throws<NotSupportedException>(() => engine.Preview(RetentionPolicy.UntilSaturation, Evaluation));
    }

    [Fact]
    public void CancelledAndBudgetLimitedPreviewsCannotExecuteAndPagesReportIncompleteWork()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var engine = new RetentionEngine(f.Repository, new(MaximumExaminedVersions: 1));
        var limited = engine.Preview(RetentionPolicy.OneMonth, Evaluation); Assert.False(limited.Complete); Assert.True(limited.BudgetExceeded); Assert.Equal(1, limited.Examined);
        Assert.Throws<InvalidOperationException>(() => engine.Execute(limited));
        var cancelled = engine.Preview(RetentionPolicy.OneMonth, Evaluation, new(true)); Assert.True(cancelled.Cancelled); Assert.False(cancelled.Complete);
        Assert.Throws<InvalidOperationException>(() => engine.Execute(cancelled));
        using var reader = f.Repository.OpenReader(); var page = engine.GetCandidates(reader, limited.Evaluation);
        Assert.True(page.BudgetExceeded); Assert.False(page.Complete); Assert.NotNull(page.Continuation);
        Assert.True(engine.GetCandidates(reader, limited.Evaluation, cancellationToken: new(true)).Cancelled);
    }

    [Fact]
    public void ComparisonReadersOpeningCancellationAlsoProducesIncompletePreviewAndPage()
    {
        using var f = new HistoryFixture(); f.Add(); using var cancellation = new CancellationTokenSource();
        using var reader = f.Repository.OpenComparisonReader(cancellationToken: cancellation.Token); cancellation.Cancel();
        var engine = new RetentionEngine(f.Repository); var evaluation = new RetentionEvaluation(RetentionPolicy.OneMonth, Evaluation);
        var preview = engine.Preview(reader, evaluation); Assert.True(preview.Cancelled); Assert.False(preview.Complete);
        var page = engine.GetCandidates(reader, evaluation); Assert.True(page.Cancelled); Assert.False(page.Complete);
        Assert.Null(reader.Workspace!.CreatedDirectory);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(128)]
    public void CandidatePagesResumeWithinOneLongPathAndAcrossEmptyIneligiblePaths(int size)
    {
        using var f = new HistoryFixture(); var expected = Enumerable.Range(0, 9).Select(i => f.Add("same", day: i)).ToArray();
        f.Add("same", day: -100); f.Add("a-ineligible", day: 1100); var deleted = f.Add("z-deleted"); f.Remove("z-deleted");
        var engine = new RetentionEngine(f.Repository); var preview = Preview(f.Repository); using var reader = f.Repository.OpenReader();
        var identities = new List<VersionIdentity>(); RetentionCursor? cursor = null;
        do
        {
            var page = engine.GetCandidates(reader, preview.Evaluation, size, cursor); identities.AddRange(page.Items.Select(v => v.Identity));
            Assert.InRange(page.Items.Count, 0, size); Assert.InRange(page.ChargedCandidateBytes, 0, 4 * 1024 * 1024); cursor = page.Continuation;
            Assert.Equal(cursor is null, page.Complete);
        } while (cursor is not null);
        Assert.Equal(expected.Select(v => v.Identity).Append(deleted.Identity), identities);
    }

    [Fact]
    public void CursorRejectsChangedAuthorityPolicyInstantPageBoundsAndOtherRepository()
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); f.Add(day: 2);
        var engine = new RetentionEngine(f.Repository); using var reader = f.Repository.OpenReader(); var evaluation = new RetentionEvaluation(RetentionPolicy.OneMonth, Evaluation);
        var cursor = engine.GetCandidates(reader, evaluation, 1).Continuation!;
        Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(reader, new(RetentionPolicy.ThreeMonths, Evaluation), 1, cursor));
        Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(reader, new(RetentionPolicy.OneMonth, Evaluation.AddTicks(1)), 1, cursor));
        Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(reader, evaluation, 2, cursor));
        Assert.Throws<InvalidOperationException>(() => new RetentionEngine(f.Repository, new(MaximumCandidateBytes: 8512)).GetCandidates(reader, evaluation, 1, cursor));
        f.Add(day: 3); using var changed = f.Repository.OpenReader(); Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(changed, evaluation, 1, cursor));
        using var other = new HistoryFixture(); using var foreign = other.Repository.OpenReader(); Assert.Throws<ArgumentException>(() => engine.GetCandidates(foreign, evaluation, 1, cursor));
    }

    [Theory]
    [InlineData("backup")] [InlineData("membership")] [InlineData("configuration")] [InlineData("rebuild")] [InlineData("compaction")] [InlineData("retention")]
    public void AnyAuthorityChangeMakesPreviewStaleBeforePreparationOrCleanup(string change)
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1); var engine = new RetentionEngine(f.Repository); var preview = Preview(f.Repository);
        if (change == "backup") f.Add("new");
        else if (change == "membership") f.Remove("a");
        else if (change == "retention") engine.Execute(preview);
        else using (var writer = f.Repository.OpenWriter())
        {
            if (change == "rebuild") writer.RebuildIndexes();
            else if (change == "compaction") writer.CompactMetadata();
            else using (var reader = f.Repository.OpenReader()) writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "changed-description")]), reader.Authority);
        }
        var before = Inventory(f.Repository); Assert.Throws<StaleRetentionPreviewException>(() => engine.Execute(preview));
        Assert.Equal(before, Inventory(f.Repository));
        if (change != "retention") Assert.True(File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId)));
    }

    [Fact]
    public void ExecutionRegeneratesTruthUsesOneScanOwnAuthoritiesAndExactBatchBounds()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 8; i++) f.Add(day: i);
        var engine = new RetentionEngine(f.Repository, new(BatchSize: 2)); var preview = Preview(f.Repository);
        preview.Eligible = 999; preview.EligibleLogicalBytes = long.MaxValue; preview.Examined = 0; // Friend-only forgery of untrusted diagnostic totals.
        var authorities = new List<AuthorityIdentity>(); engine.BatchCommitted = result => authorities.Add(result.FinalConfirmedAuthority);
        var result = engine.Execute(preview); Assert.True(result.Complete); Assert.Equal(8, result.Examined); Assert.Equal(1, result.Protected);
        Assert.Equal(7, result.Eligible); Assert.Equal(7, result.Removed); Assert.Equal(4, result.BatchesCommitted); Assert.Equal(1, result.AgeScans);
        Assert.Equal(8, result.Reads!.VersionsExamined); Assert.Equal(1, result.Reads.CurrentEntriesExamined); Assert.Equal(2, result.MaximumBatchCount);
        Assert.Equal(7 * 5, result.RemovedLogicalBytes); Assert.Equal(7, result.Cleanup.Deleted);
        Assert.Equal(Enumerable.Range(1, 4).Select(i => preview.Authority.Generation + i), authorities.Select(a => a.Generation));
        Assert.Equal(preview.Authority, result.StartingAuthority); Assert.Equal(authorities[^1], result.FinalConfirmedAuthority);
        Assert.True(result.VersionPagesWritten > 0); Assert.True(result.MetadataBytesAppended > 0);
    }

    [Fact]
    public void CancellationAfterBatchReportsProgressAndFreshPreviewFinishesSurvivors()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 6; i++) f.Add(day: i);
        using var cancellation = new CancellationTokenSource(); var engine = new RetentionEngine(f.Repository, new(BatchSize: 2));
        engine.BatchCommitted = _ => cancellation.Cancel(); var result = engine.Execute(Preview(f.Repository), cancellation.Token);
        Assert.True(result.Cancelled); Assert.True(result.Partial); Assert.Equal(2, result.Removed); Assert.Equal(1, result.BatchesCommitted);
        engine.BatchCommitted = null; var preview = Preview(f.Repository); Assert.Equal(3, preview.Eligible);
        var rest = engine.Execute(preview); Assert.True(rest.Complete); Assert.Equal(3, rest.Removed);
    }

    [Fact]
    public void ExecutionBudgetStopsLaterBatchesAndDoesNotFlushAnIncompletePendingBatch()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 7; i++) f.Add(day: i);
        var engine = new RetentionEngine(f.Repository, new(BatchSize: 2, MaximumExaminedVersions: 3));
        var result = engine.Execute(Preview(f.Repository)); Assert.True(result.BudgetExceeded); Assert.True(result.Partial);
        Assert.Equal(3, result.Examined); Assert.Equal(3, result.Eligible); Assert.Equal(2, result.Removed); Assert.Equal(1, result.BatchesCommitted);
        Assert.Equal(4, Preview(f.Repository).Eligible);
    }

    [Fact]
    public void FailedMissingLiveSourceScanPreservesAncientCurrentDuringRetention()
    {
        using var f = new HistoryFixture(); var current = f.Add(day: -100);
        var missing = Path.Combine(f.Temp.Root, "missing-source");
        var scan = new FileHistoryEngine(f.Repository).Scan([new SourceSelection(new(FoundationTests.Root, missing), missing)]);
        Assert.True(scan.SourceSkips > 0); Assert.Equal(0, scan.Removals);
        var preview = Preview(f.Repository); Assert.Equal(1, preview.Protected); Assert.Equal(0, preview.Eligible);
        var result = new RetentionEngine(f.Repository).Execute(preview); Assert.Equal(0, result.Removed);
        using var reader = f.Repository.OpenReader(); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
    }

    [Fact]
    public void PublicCandidateAndBatchBoundsRejectUnsafeRequests()
    {
        using var f = new HistoryFixture(); var evaluation = new RetentionEvaluation(RetentionPolicy.OneMonth, Evaluation);
        foreach (var options in new[] { new RetentionOptions(BatchSize: 0), new RetentionOptions(BatchSize: 4097),
            new RetentionOptions(MaximumCandidateBytes: 8511), new RetentionOptions(MaximumCandidateBytes: 4 * 1024 * 1024 + 1), new RetentionOptions(MaximumExaminedVersions: 0) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionEngine(f.Repository, options));
        using var reader = f.Repository.OpenReader(); var engine = new RetentionEngine(f.Repository);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.GetCandidates(reader, evaluation, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.GetCandidates(reader, evaluation, 4097));
        var before = Inventory(f.Repository); var preview = engine.Preview(reader, evaluation); Assert.True(preview.Complete); Assert.Equal(0, preview.Examined);
        Assert.True(engine.GetCandidates(reader, evaluation).Complete); Assert.True(engine.Execute(preview).Complete); Assert.Equal(before, Inventory(f.Repository));
    }

    [Fact]
    public void TimelineSupportSurvivesUntilItsFinalVersionExpiresAndMaintenanceNeverResurrects()
    {
        using var f = new HistoryFixture(); var old = f.Add("a", day: 0); var protectedAtSameTime = f.Add("b", day: 0); f.Add("a", day: 1);
        var engine = new RetentionEngine(f.Repository); engine.Execute(Preview(f.Repository));
        using (var reader = f.Repository.OpenReader()) { Assert.Null(reader.GetVersion(old.Identity)); Assert.Contains(FoundationTests.Time, reader.GetTimelinePage(new(FoundationTests.Root)).Items); }
        f.Remove("b"); engine.Execute(Preview(f.Repository));
        using (var writer = f.Repository.OpenWriter()) { writer.RebuildIndexes(); writer.CompactMetadata(); writer.CleanupRetiredMetadata(); writer.RebuildIndexes(); }
        using var fresh = f.Repository.OpenReader(); var times = fresh.GetTimelinePage(new(FoundationTests.Root)).Items;
        Assert.DoesNotContain(FoundationTests.Time, times); Assert.DoesNotContain(Evaluation, times); Assert.Single(times);
        Assert.Null(fresh.GetVersion(protectedAtSameTime.Identity)); Assert.Equal(4, RepositoryIdentity.FormatVersion); Assert.Equal(5, Enum.GetValues<SemanticKind>().Length);
    }

    [WindowsFact]
    public void HeldPayloadDefersCleanupAfterConfirmedInvisibilityAndIndependentRestartCleansIt()
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        using (var reader = f.Repository.OpenReader()) using (var held = reader.OpenVersionPayload(old.Identity))
        {
            var result = new RetentionEngine(f.Repository).Execute(Preview(f.Repository)); Assert.Equal(1, result.Removed); Assert.Equal(1, result.Cleanup.Deferred);
            using var fresh = f.Repository.OpenReader(); Assert.Null(fresh.GetVersion(old.Identity)); Assert.True(File.Exists(f.Repository.GetPayloadPath(old.Identity.VersionId)));
        }
        Assert.Equal(0, Preview(f.Repository).Eligible);
        using var writer = f.Repository.OpenWriter(); Assert.Equal(1, writer.CleanupRetiredPayloads().Result.Deleted);
    }
}
