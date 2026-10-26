using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class HistoryCleanupTests
{
    internal static readonly HistoryCleanupEvaluation Latest = new(HistoryCleanupMode.KeepOnlyMostRecent);
    internal static readonly HistoryCleanupEvaluation Month = new(HistoryCleanupMode.OlderThanOneMonth, RetentionTests.Evaluation);
    internal static HistoryCleanupPreview Preview(Repository repo, bool latest = true) => new HistoryCleanupEngine(repo).Preview(latest ? Latest : Month);

    [Theory]
    [InlineData(HistoryCleanupMode.OlderThanOneMonth, 30)] [InlineData(HistoryCleanupMode.OlderThanThreeMonths, 90)]
    [InlineData(HistoryCleanupMode.OlderThanSixMonths, 180)] [InlineData(HistoryCleanupMode.OlderThanNineMonths, 270)]
    [InlineData(HistoryCleanupMode.OlderThanOneYear, 365)] [InlineData(HistoryCleanupMode.OlderThanTwoYears, 730)]
    public void ExactElapsedDaysAndExplicitUtc(HistoryCleanupMode mode, int days)
    {
        var evaluation = new HistoryCleanupEvaluation(mode, RetentionTests.Evaluation);
        Assert.Equal(RetentionTests.Evaluation.AddDays(-days), evaluation.CutoffUtc);
        Assert.Equal(RetentionTests.Evaluation, evaluation.EvaluationUtc);
        Assert.Throws<ArgumentException>(() => new HistoryCleanupEvaluation(mode));
        Assert.Throws<ArgumentException>(() => new HistoryCleanupEvaluation(mode, default));
        Assert.Throws<ArgumentException>(() => new HistoryCleanupEvaluation(mode, RetentionTests.Evaluation.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryCleanupEvaluation(mode, DateTimeOffset.MinValue.AddTicks(1)));
        Assert.Equal(DateTimeOffset.MinValue, new HistoryCleanupEvaluation(mode, DateTimeOffset.MinValue.AddDays(days)).CutoffUtc);
        Assert.Equal(DateTimeOffset.MaxValue.AddDays(-days), new HistoryCleanupEvaluation(mode, DateTimeOffset.MaxValue).CutoffUtc);
    }

    [Fact]
    public void SeparateClosedMenuAndNoMeaninglessClockForLatest()
    {
        Assert.Equal(7, Enum.GetValues<HistoryCleanupMode>().Length); Assert.Equal(8, Enum.GetValues<RetentionPolicy>().Length);
        Assert.Null(Latest.EvaluationUtc); Assert.Null(Latest.CutoffUtc);
        Assert.Throws<ArgumentException>(() => new HistoryCleanupEvaluation(HistoryCleanupMode.KeepOnlyMostRecent, RetentionTests.Evaluation));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryCleanupEvaluation((HistoryCleanupMode)999, RetentionTests.Evaluation));
        Assert.Throws<ArgumentException>(() => new HistoryCleanupEvaluation((HistoryCleanupMode)999));
    }

    [Fact]
    public void StrictAgeTicksFutureAndRollback()
    {
        using var f = new HistoryFixture(); var cutoff = Month.CutoffUtc!.Value;
        var before = RetentionTests.Add(f.Repository, "before", cutoff.AddTicks(-1));
        var equal = RetentionTests.Add(f.Repository, "equal", cutoff);
        var after = RetentionTests.Add(f.Repository, "after", cutoff.AddTicks(1));
        var future = RetentionTests.Add(f.Repository, "future", RetentionTests.Evaluation.AddYears(1));
        RetentionTests.RemoveCurrent(f.Repository, before, equal, after, future);
        var rollback = RetentionTests.Add(f.Repository, "before", cutoff.AddDays(-100)); RetentionTests.RemoveCurrent(f.Repository, rollback);
        var engine = new HistoryCleanupEngine(f.Repository); var preview = engine.Preview(Month);
        Assert.Equal(5, preview.Examined); Assert.Equal(4, preview.PathsExamined); Assert.Equal(2, preview.Eligible);
        Assert.Equal(1, preview.AffectedPaths); Assert.Equal(2, preview.EligibleLogicalBytes);
        Assert.Equal(2, engine.Execute(preview).Removed);
        using var reader = f.Repository.OpenReader();
        foreach (var v in new[] { before, rollback }) Assert.Null(reader.GetVersion(v.Identity));
        foreach (var v in new[] { equal, after, future }) Assert.NotNull(reader.GetVersion(v.Identity));
    }

    [Fact]
    public void NormativeCurrentAndDeletedExamplesUnprotectionAndMissingSource()
    {
        using var f = new HistoryFixture(); var repo = f.Repository; var evaluation = RetentionTests.Evaluation;
        var a = RetentionTests.Add(repo, "current", evaluation.AddDays(-480)); var b = RetentionTests.Add(repo, "current", evaluation.AddDays(-390));
        var da = RetentionTests.Add(repo, "deleted", evaluation.AddDays(-480)); var db = RetentionTests.Add(repo, "deleted", evaluation.AddDays(-390));
        var ya = RetentionTests.Add(repo, "young-deleted", evaluation.AddDays(-480)); var yb = RetentionTests.Add(repo, "young-deleted", evaluation.AddDays(-330));
        var ancientOld = RetentionTests.Add(repo, "ancient", evaluation.AddDays(-900)); var ancient = RetentionTests.Add(repo, "ancient", evaluation.AddDays(-990));
        var root = Guid.NewGuid(); var unprotected = RetentionTests.Add(repo, "unprotected", evaluation.AddDays(-800), root);
        RetentionTests.RemoveCurrent(repo, db, yb, unprotected);
        using (var writer = repo.OpenWriter()) using (var reader = repo.OpenReader())
            writer.Apply(new(sourceRoots: [new(root, "missing", false)]), reader.Authority);
        using (var reader = repo.OpenReader()) Assert.NotNull(reader.GetVersion(db.Identity)); // Source deletion itself prunes nothing.
        var missing = Path.Combine(f.Temp.Root, "missing-source");
        var scan = new FileHistoryEngine(repo).Scan([new SourceSelection(new(FoundationTests.Root, missing), missing)]);
        Assert.True(scan.SourceSkips > 0); Assert.Equal(0, scan.Removals);
        var engine = new HistoryCleanupEngine(repo); var preview = engine.Preview(new(HistoryCleanupMode.OlderThanOneYear, evaluation));
        Assert.Equal(2, preview.Protected); Assert.Equal(6, preview.Eligible); Assert.Equal(5, preview.AffectedPaths);
        Assert.Equal(6, engine.Execute(preview).Removed);
        using var fresh = repo.OpenReader();
        foreach (var v in new[] { a, da, db, ya, ancientOld, unprotected }) Assert.Null(fresh.GetVersion(v.Identity));
        foreach (var v in new[] { b, yb, ancient }) Assert.NotNull(fresh.GetVersion(v.Identity));
        Assert.Empty(fresh.EnumerateVersions(db.Identity.Path)); Assert.Equal(b, fresh.GetCurrent(b.Identity.Path));
    }

    [Fact]
    public void LatestIsPathLocalPublicationOrderAcrossRootsRenameDeletedUnprotectedAndTimestamps()
    {
        using var f = new HistoryFixture(); var all = new List<StoredVersion>(); var keep = new List<StoredVersion>();
        foreach (var root in new[] { FoundationTests.Root, Guid.NewGuid() })
        {
            foreach (var name in new[] { "current", "deleted", "old-name", "new-name", "single-deleted", "unprotected" })
            {
                var count = name == "single-deleted" ? 1 : 3;
                for (var i = 0; i < count; i++) all.Add(f.Add(name, day: i == 2 ? -100 : 7, root: root));
                keep.Add(all[^1]); if (name is not "current" and not "new-name") RetentionTests.RemoveCurrent(f.Repository, all[^1]);
            }
        }
        var unprotectedRoot = Guid.NewGuid();
        for (var i = 0; i < 3; i++) all.Add(f.Add("unprotected-root", day: -i, root: unprotectedRoot));
        keep.Add(all[^1]); RetentionTests.RemoveCurrent(f.Repository, all[^1]);
        using (var writer = f.Repository.OpenWriter()) using (var captured = f.Repository.OpenReader())
            writer.Apply(new(sourceRoots: [new(unprotectedRoot, "missing", false)]), captured.Authority);
        var engine = new HistoryCleanupEngine(f.Repository); var preview = engine.Preview(Latest);
        Assert.Equal(35, preview.Examined); Assert.Equal(13, preview.PathsExamined); Assert.Equal(4, preview.Protected);
        Assert.Equal(22, preview.Eligible); Assert.Equal(11, preview.AffectedPaths);
        Assert.Equal(22, engine.Execute(preview).Removed);
        using var reader = f.Repository.OpenReader(); Assert.Equal(keep.Select(v => v.Identity).OrderBy(i => i.Path), reader.EnumerateRetentionVersions().Select(v => v.Identity));
        foreach (var v in keep) Assert.Single(reader.EnumerateVersions(v.Identity.Path));
        Assert.Equal(0, engine.Preview(Latest).Eligible);
    }

    [Fact]
    public void SameSequenceUsesOrdinalDespiteReverseStoredMtimeCreationAndUuid()
    {
        using var f = new HistoryFixture(); var first = f.Add(); var path = first.Identity.Path;
        var older = new StoredVersion(first.Identity with { Ordinal = 1, VersionId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") },
            new(FoundationTests.Root, "a", 5, FoundationTests.Time.AddDays(10), FoundationTests.Time.AddDays(10), FileAttributes.Archive), first.StoredUtc.AddDays(90), first.Checksum);
        var latest = new StoredVersion(first.Identity with { Ordinal = 2, VersionId = Guid.Parse("00000000-0000-0000-0000-000000000001") }, first.Metadata, first.StoredUtc.AddDays(-90), first.Checksum);
        RetentionProjectionTests.Corrupt(f.Repository, (reader, append) =>
        {
            var a = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, reader.Captured.Root.Journal, Version: older)));
            var b = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, a, Version: latest)));
            var upsert = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, b, Path: path, VersionAddress: b)));
            byte[] Read(FrameAddress address) => append.ReadNew(address) ?? reader.Segments.Read(address);
            return reader.Captured.Root with { Journal = upsert,
                Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(reader.Captured.Root.Versions, [(PathKeys.Version(older.Identity), a), (PathKeys.Version(latest.Identity), b)]),
                Current = new OrderedIndex(IndexKind.Current, Read, append).Update(null, [(PathKeys.Current(path), b)]) };
        });
        foreach (var v in new[] { older, latest }) { var p = f.Repository.GetPayloadPath(v.Identity.VersionId); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "hello"); }
        var engine = new HistoryCleanupEngine(f.Repository); Assert.Equal(2, engine.Execute(engine.Preview(Latest)).Removed);
        using var fresh = f.Repository.OpenReader(); Assert.Equal(latest, Assert.Single(fresh.EnumerateVersions(path)));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void NonLatestCurrentFailsCompletePreviewAndDoesNotRepairOrDelete(bool latestMode)
    {
        using var f = new HistoryFixture(); var old = f.Add(); var latest = f.Add(day: -100);
        RetentionProjectionTests.Corrupt(f.Repository, (reader, append) => reader.Captured.Root with {
            Current = new OrderedIndex(IndexKind.Current, a => append.ReadNew(a) ?? reader.Segments.Read(a), append).Update(null,
                [(PathKeys.Current(old.Identity.Path), reader.VersionIndex.Find(reader.Captured.Root.Versions, PathKeys.Version(old.Identity))!)]) });
        var before = RetentionTests.Inventory(f.Repository); var engine = new HistoryCleanupEngine(f.Repository);
        var evaluation = latestMode ? Latest : Month;
        Assert.Throws<InvalidDataException>(() => engine.Preview(evaluation));
        using var capture = f.Repository.OpenReader(); Assert.Throws<InvalidDataException>(() => engine.GetCandidates(capture, evaluation));
        var limited = new HistoryCleanupEngine(f.Repository, new(MaximumExaminedVersions: 1));
        var page = limited.GetCandidates(capture, evaluation); Assert.True(page.BudgetExceeded); Assert.NotNull(page.Continuation);
        Assert.Throws<InvalidDataException>(() => limited.GetCandidates(capture, evaluation, cursor: page.Continuation));
        Assert.Equal(before, RetentionTests.Inventory(f.Repository)); Assert.Equal(old, capture.GetCurrent(old.Identity.Path)); Assert.NotNull(capture.GetVersion(latest.Identity));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void PreviewIsReadOnlyNoWriterPayloadSourceOrWorkspaceAndCancellationIsIncomplete(bool latest)
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1); var inventory = RetentionTests.Inventory(f.Repository);
        using var writer = f.Repository.OpenWriter(); using var locked = new FileStream(f.Repository.GetPayloadPath(old.Identity.VersionId), FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = f.Repository.OpenComparisonReader(f.Temp.Child("unused")); var engine = new HistoryCleanupEngine(f.Repository); var evaluation = latest ? Latest : Month;
        var preview = engine.Preview(reader, evaluation); Assert.True(preview.Complete); Assert.Equal(1, preview.Eligible); Assert.Null(reader.Workspace!.CreatedDirectory);
        var cancelled = engine.Preview(reader, evaluation, new(true)); Assert.True(cancelled.Cancelled); Assert.False(cancelled.Complete); Assert.Null(cancelled.Reads);
        Assert.Equal(inventory, RetentionTests.Inventory(f.Repository));
    }

    [Theory]
    [InlineData(true, 1)] [InlineData(true, 3)] [InlineData(true, 128)] [InlineData(false, 1)] [InlineData(false, 3)]
    public void BoundedPagesContinueWithinLongPathAndAcrossSingletons(bool latest, int pageSize)
    {
        using var f = new HistoryFixture(); f.Add("a-single"); var expected = Enumerable.Range(0, 21).Select(i => f.Add("same", day: i)).ToArray();
        f.Add("same", day: -100); f.Add("z-single"); f.Remove("z-single"); var evaluation = latest ? Latest : Month;
        using var reader = f.Repository.OpenReader(); var engine = new HistoryCleanupEngine(f.Repository); var items = new List<VersionIdentity>(); HistoryCleanupCursor? cursor = null;
        do
        {
            var page = engine.GetCandidates(reader, evaluation, pageSize, cursor); items.AddRange(page.Items.Select(v => v.Identity)); cursor = page.Continuation;
            Assert.InRange(page.Items.Count, 0, pageSize); Assert.InRange(page.ChargedCandidateBytes, 0, 4 * 1024 * 1024); Assert.Equal(cursor is null, page.Complete);
        } while (cursor is not null);
        Assert.Equal(expected.Select(v => v.Identity).Concat(latest ? [] : reader.EnumerateVersions(new(FoundationTests.Root, "z-single")).Select(v => v.Identity)), items);
    }

    [Fact]
    public void OneVersionWorkPagesCarryPendingStateAndMakeProgressWithoutLosingCandidates()
    {
        using var f = new HistoryFixture(); var versions = Enumerable.Range(0, 7).Select(i => f.Add(day: i)).ToArray();
        var engine = new HistoryCleanupEngine(f.Repository, new(MaximumExaminedVersions: 1)); using var reader = f.Repository.OpenReader();
        HistoryCleanupCursor? cursor = null; var ids = new List<VersionIdentity>(); int pages = 0;
        do { var page = engine.GetCandidates(reader, Latest, 2, cursor); Assert.InRange(page.Examined, 0, 1); ids.AddRange(page.Items.Select(v => v.Identity)); cursor = page.Continuation; Assert.True(++pages < 20); } while (cursor is not null);
        Assert.Equal(versions[..^1].Select(v => v.Identity), ids);
        var incomplete = engine.Preview(Latest); Assert.True(incomplete.BudgetExceeded); Assert.False(incomplete.Complete);
        Assert.Throws<InvalidOperationException>(() => engine.Execute(incomplete));
    }

    [Fact]
    public void CursorRejectsEveryBindingChangeAndForeignReader()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 4; i++) f.Add(day: i);
        var engine = new HistoryCleanupEngine(f.Repository); using var reader = f.Repository.OpenReader(); var cursor = engine.GetCandidates(reader, Month, 1).Continuation!;
        Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(reader, Latest, 1, cursor));
        Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(reader, new(HistoryCleanupMode.OlderThanOneMonth, RetentionTests.Evaluation.AddTicks(1)), 1, cursor));
        Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(reader, Month, 2, cursor));
        Assert.Throws<InvalidOperationException>(() => new HistoryCleanupEngine(f.Repository, new(MaximumCandidateBytes: 8512)).GetCandidates(reader, Month, 1, cursor));
        Assert.Throws<InvalidOperationException>(() => new HistoryCleanupEngine(f.Repository, new(MaximumExaminedVersions: 100)).GetCandidates(reader, Month, 1, cursor));
        f.Add(day: 5); using var fresh = f.Repository.OpenReader(); Assert.Throws<InvalidOperationException>(() => engine.GetCandidates(fresh, Month, 1, cursor));
        using var other = new HistoryFixture(); using var foreign = other.Repository.OpenReader(); Assert.Throws<ArgumentException>(() => engine.GetCandidates(foreign, Month, 1, cursor));
    }

    [Fact]
    public void CandidateByteBoundsAndEmptyLatestAcrossManySingletons()
    {
        using var f = new HistoryFixture(); var longPath = string.Join('/', Enumerable.Repeat(new string('a', 200), 9));
        var expected = Enumerable.Range(0, 4).Select(i => f.Add(longPath, day: i)).ToArray(); f.Add(longPath, day: 5);
        var engine = new HistoryCleanupEngine(f.Repository, new(MaximumCandidateBytes: 8512)); using var reader = f.Repository.OpenReader(); HistoryCleanupCursor? cursor = null; int count = 0;
        do { var page = engine.GetCandidates(reader, Latest, 128, cursor); Assert.InRange(page.Items.Count, 0, 1); Assert.InRange(page.ChargedCandidateBytes, 0, 8512); count += page.Items.Count; cursor = page.Continuation; } while (cursor is not null);
        Assert.Equal(expected.Length, count); var result = engine.Execute(engine.Preview(Latest)); Assert.Equal(4, result.BatchesCommitted); Assert.Equal(1, result.MaximumBatchCount);
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); MetadataDiagnosticFixture.Populate(repo, 129);
        new HistoryCleanupEngine(repo).Execute(Preview(repo)); Assert.Equal(0, Preview(repo).Eligible);
    }

    [Fact]
    public void ThousandsOfPublicationsAtOnePathUseOnlyPendingLookaheadAcrossPages()
    {
        using var f = new HistoryFixture(); var first = f.Add();
        RetentionProjectionTests.Corrupt(f.Repository, (reader, append) =>
        {
            var changes = new List<(byte[], FrameAddress?)>(); var journal = reader.Captured.Root.Journal; FrameAddress? last = null;
            for (var ordinal = 1; ordinal < 2048; ordinal++)
            {
                var version = new StoredVersion(first.Identity with { Ordinal = ordinal, VersionId = Guid.NewGuid() }, first.Metadata,
                    first.StoredUtc.AddTicks(-ordinal), first.Checksum);
                last = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, journal, Version: version)));
                journal = last; changes.Add((PathKeys.Version(version.Identity), last));
            }
            journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, journal, Path: first.Identity.Path, VersionAddress: last)));
            byte[] Read(FrameAddress address) => append.ReadNew(address) ?? reader.Segments.Read(address);
            return reader.Captured.Root with { Journal = journal,
                Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(reader.Captured.Root.Versions, changes),
                Current = new OrderedIndex(IndexKind.Current, Read, append).Update(null, [(PathKeys.Current(first.Identity.Path), last)]) };
        });
        var engine = new HistoryCleanupEngine(f.Repository); var preview = engine.Preview(Latest);
        Assert.Equal(2048, preview.Examined); Assert.Equal(2047, preview.Eligible); Assert.Equal(1, preview.PathsExamined); Assert.Equal(1, preview.Protected);
        using var reader = f.Repository.OpenReader(); HistoryCleanupCursor? cursor = null; int next = 0;
        do
        {
            var page = engine.GetCandidates(reader, Latest, 127, cursor);
            foreach (var candidate in page.Items) Assert.Equal(next++, candidate.Identity.Ordinal);
            Assert.InRange(page.Items.Count, 0, 127); cursor = page.Continuation;
        } while (cursor is not null);
        Assert.Equal(2047, next);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ReaderCancellationDuringAndBeforeScanNeverProducesCompletePreview(bool latest)
    {
        using var f = new HistoryFixture(); f.Add(); f.Add(day: 1); var evaluation = latest ? Latest : Month; var engine = new HistoryCleanupEngine(f.Repository);
        using (var cts = new CancellationTokenSource()) using (var reader = f.Repository.OpenReader())
        {
            reader.RetentionWindowCompleted = cts.Cancel;
            var preview = engine.Preview(reader, evaluation, cts.Token); Assert.True(preview.Cancelled); Assert.False(preview.Complete);
            Assert.Throws<InvalidOperationException>(() => engine.Execute(preview));
        }
        using var opening = new CancellationTokenSource(); using var capture = f.Repository.OpenComparisonReader(cancellationToken: opening.Token); opening.Cancel();
        var cancelled = engine.Preview(capture, evaluation); Assert.True(cancelled.Cancelled); Assert.Null(cancelled.Reads);
        Assert.True(engine.GetCandidates(capture, evaluation).Cancelled);
    }
}
