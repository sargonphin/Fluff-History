using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ApplicationAdapterTests
{
    [Fact]
    public async Task SetupInspectsMissingPlainInvalidAndRefusesOverwrite()
    {
        using var temp = new TemporaryDirectory(); var setup = new RepositorySetupService();
        Assert.Equal(RepositoryReadiness.Missing, (await setup.InspectAsync(temp.Child())).State);
        Assert.Equal(RepositoryReadiness.Ready, (await setup.CreateAsync(temp.Child())).State);
        var before = TemporaryDirectory.Snapshot(temp.Child());
        await Assert.ThrowsAsync<ApplicationOperationException>(() => setup.CreateAsync(temp.Child()));
        Assert.Equal(before.OrderBy(p => p.Key), TemporaryDirectory.Snapshot(temp.Child()).OrderBy(p => p.Key));
        File.WriteAllText(Path.Combine(temp.Child(), "state.json"), "damaged");
        Assert.Equal(RepositoryReadiness.Invalid, (await setup.InspectAsync(temp.Child())).State);
    }

    [Fact]
    public async Task WrongPasswordAndAuthenticatedStateDamageRemainDistinctAndBuffersAreCleared()
    {
        using var temp = new TemporaryDirectory(); using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password)) { }
        var setup = new RepositorySetupService(); var password = "synthetic-wrong".ToCharArray();
        Assert.Equal(RecoveryOutcome.WrongPassword, (await setup.RecoverAsync(temp.Child(), password)).Outcome);
        Assert.All(password, c => Assert.Equal('\0', c));
        File.WriteAllBytes(Path.Combine(temp.Child(), "state.json"), [1, 2, 3]);
        password = EncryptionTests.Password.ToCharArray();
        Assert.Equal(RecoveryOutcome.RepositoryInvalid, (await setup.RecoverAsync(temp.Child(), password)).Outcome);
        Assert.All(password, c => Assert.Equal('\0', c));
    }

    [WindowsFact]
    public async Task RecoveryEnrollsAndFreshLocalOpenIsVerifiedWithoutHistoryChanges()
    {
        using var temp = new TemporaryDirectory(); using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password)) { }
        var before = TemporaryDirectory.Snapshot(temp.Child()); var setup = new RepositorySetupService();
        Assert.Equal(RepositoryReadiness.RecoveryRequired, (await setup.InspectAsync(temp.Child())).State);
        var password = EncryptionTests.Password.ToCharArray(); var result = await setup.RecoverAsync(temp.Child(), password);
        Assert.Equal(new RecoveryResult(RecoveryOutcome.Success, true), result); Assert.All(password, c => Assert.Equal('\0', c));
        using var fresh = Repository.OpenWithWindowsUser(temp.Child()); using var reader = fresh.OpenReader(); Assert.Empty(reader.EnumerateSourceRoots());
        var after = TemporaryDirectory.Snapshot(temp.Child()); before.Remove("repository.json"); after.Remove("repository.json");
        Assert.Equal(before.OrderBy(p => p.Key), after.OrderBy(p => p.Key));
        Assert.Equal(RepositoryReadiness.Ready, (await setup.InspectAsync(temp.Child())).State);
    }

    [WindowsTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task UncertainEnrollmentIsNotNormalSuccessEvenWhenFreshOpenWorks(bool removeSlot)
    {
        using var temp = new TemporaryDirectory(); using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password)) { }
        var setup = new RepositorySetupService { BeforeEnrollment = repository => repository.EnrollmentObserved = stage =>
        {
            if (stage != EnrollmentStage.AfterReplace) return;
            if (removeSlot)
            {
                var path = Path.Combine(temp.Child(), "repository.json"); var bootstrap = RepositoryBootstrap.Read(temp.Child());
                File.WriteAllBytes(path, (bootstrap with { WindowsUserKey = null }).Bytes());
            }
            throw new IOException("synthetic confirmation interruption");
        } };
        var result = await setup.RecoverAsync(temp.Child(), EncryptionTests.Password.ToCharArray());
        Assert.Equal(RecoveryOutcome.EnrollmentUncertain, result.Outcome); Assert.Equal(!removeSlot, result.LocalOpenVerified);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelledCommandsClearOwnedPasswordBeforeAnyRepositoryWrite(bool create)
    {
        using var temp = new TemporaryDirectory(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var password = EncryptionTests.Password.ToCharArray(); var setup = new RepositorySetupService();
        if (create) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.CreateAsync(temp.Child(), password, cancel.Token));
        else Assert.Equal(RecoveryOutcome.Cancelled, (await setup.RecoverAsync(temp.Child(), password, cancel.Token)).Outcome);
        Assert.All(password, c => Assert.Equal('\0', c)); Assert.False(Directory.Exists(temp.Child()));
    }

    [WindowsFact]
    public async Task EncryptedCreationClearsPasswordAndBrowserOpensThroughLocalDpapi()
    {
        using var temp = new TemporaryDirectory(); var password = EncryptionTests.Password.ToCharArray();
        var result = await new RepositorySetupService().CreateAsync(temp.Child(), password);
        Assert.True(result.Encrypted); Assert.All(password, c => Assert.Equal('\0', c));
        await using var browser = await HistoryBrowserSession.OpenAsync(temp.Child()); Assert.Equal(result.RepositoryId, browser.State.RepositoryId);
    }

    [Fact]
    public async Task BrowserRecoveryRequiredOpenHasStableIdentity()
    {
        using var temp = new TemporaryDirectory(); using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password)) { }
        var error = await Assert.ThrowsAsync<ApplicationOperationException>(() => HistoryBrowserSession.OpenAsync(temp.Child()));
        Assert.Equal(ApplicationFailure.RecoveryRequired, error.Failure);
    }

    [Fact]
    public async Task BrowserPagesChildrenWithoutMaterializingTreeAndPreservesSpellingAndConflicts()
    {
        using var fixture = new Fixture(); fixture.Write("Alpha/one.txt", "one"); fixture.Write("Alpha/two.txt", "two");
        fixture.Write("Alpha-other.txt", "other"); fixture.Write("z.txt", "z"); fixture.Scan();
        // Intrinsic file/folder collision survives source topology changes.
        Directory.Delete(Path.Combine(fixture.Source, "Alpha"), true); fixture.Write("Alpha", "file"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        BrowserContinuation? cursor = null; var rows = new List<BrowserChild>();
        do
        {
            var page = await browser.GetChildrenAsync(new(fixture.Root), cursor, 1); Assert.True(page.Items.Length <= 1);
            rows.AddRange(page.Items); cursor = page.Continuation;
        } while (cursor is not null);
        Assert.Equal(4, rows.Count); Assert.Single(rows, r => r.Name == "Alpha" && r.IsFolder);
        Assert.True(rows.Single(r => r.Name == "Alpha" && !r.IsFolder).HasConflict);
        Assert.True(rows.Single(r => r.Name == "Alpha" && r.IsFolder).HasConflict);
        var nested = await browser.GetChildrenAsync(new(fixture.Root, "alpha")); Assert.Equal(2, nested.Items.Length);
        Assert.All(nested.Items, row => Assert.False(row.IsFolder));
        await Assert.ThrowsAsync<ApplicationOperationException>(() => browser.GetChildrenAsync(new(fixture.Root), new BrowserContinuation(), 257));
    }

    [Fact]
    public async Task CapturedAuthorityRefreshTimelinePagingAndM18ReuseArePreserved()
    {
        using var fixture = new Fixture(); fixture.Write("a.txt", "first"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath); var reader = browser.ReaderForTests;
        var original = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single().Version!;
        fixture.Write("a.txt", "second revision longer"); fixture.Scan();
        Assert.Equal(original.Length, (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single().Version!.Length);
        await browser.RefreshAsync(); Assert.NotSame(reader, browser.ReaderForTests);
        var first = await browser.GetTimelineAsync(new(fixture.Root), pageSize: 1); Assert.NotNull(first.Continuation);
        var retainedReader = browser.ReaderForTests; Assert.Equal(1, retainedReader.TimelineCacheStatistics.Builds);
        var second = await browser.GetTimelineAsync(new(fixture.Root), first.Continuation, 1);
        Assert.Same(retainedReader, browser.ReaderForTests); Assert.Equal(1, retainedReader.TimelineCacheStatistics.Hits);
        Assert.True(first.Items[0] > second.Items[0]); Assert.Null(second.Continuation);
        var current = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single().Version!;
        var path = await browser.GetPathHistoryAsync(current, pageSize: 1); Assert.NotNull(path.Continuation);
        Assert.Equal(original.Length, (await browser.GetPathHistoryAsync(current, path.Continuation, 1)).Items.Single().Length);
        await browser.RefreshAsync();
        Assert.Equal(ApplicationFailure.RefreshRequired, (await Assert.ThrowsAsync<ApplicationOperationException>(
            () => browser.GetTimelineAsync(new(fixture.Root), first.Continuation, 1))).Failure);
    }

    [Fact]
    public async Task AsOfSelectionUsesRecordedTimesAndExpiryRequiresExplicitRefresh()
    {
        using var fixture = new Fixture(); fixture.Write("a", "first"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var first = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single().Version!;
        fixture.Write("a", "later content"); fixture.Scan(); await browser.RefreshAsync();
        Assert.Equal(first.Length, (await browser.GetChildrenAsync(new(fixture.Root, AtUtc: first.StoredUtc))).Items.Single().Version!.Length);
        _ = await browser.GetTimelineAsync(new(fixture.Root));
        using (var repository = Repository.Open(fixture.RepositoryPath)) using (var writer = repository.OpenWriter()) writer.CompactMetadata();
        var error = await Assert.ThrowsAsync<ApplicationOperationException>(() => browser.GetTimelineAsync(new(fixture.Root)));
        Assert.Equal(ApplicationFailure.RefreshRequired, error.Failure);
        await browser.RefreshAsync(); Assert.NotEmpty((await browser.GetTimelineAsync(new(fixture.Root))).Items);
    }

    [Fact]
    public async Task ConcurrentQueriesSerializeOffCallerAndDisposalJoinsActiveQuery()
    {
        using var fixture = new Fixture(); fixture.Write("a", "content"); fixture.Scan();
        var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var invocations = 0; browser.QueryEntered = () => { Interlocked.Increment(ref invocations); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(20))); };
        var active = browser.GetChildrenAsync(new(fixture.Root)); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var queued = browser.GetTimelineAsync(new(fixture.Root)); var disposal = browser.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.Equal(1, invocations); release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await disposal; await browser.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => { _ = browser.GetChildrenAsync(new(fixture.Root)); });
        using var reopened = Repository.Open(fixture.RepositoryPath); using var writer = reopened.OpenWriter();
    }

    [Fact]
    public async Task CancellationBeforeQueryLeavesSessionReusableAndIdleDisposalWorks()
    {
        using var fixture = new Fixture(); fixture.Write("a", "content"); fixture.Scan();
        var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.GetChildrenAsync(new(fixture.Root), token: cancel.Token));
        Assert.Single((await browser.GetChildrenAsync(new(fixture.Root))).Items); await browser.DisposeAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HeadlessDisplayedRestorePreservesExactSelectionAndExplicitOriginalMapping(bool original)
    {
        using var fixture = new Fixture(); fixture.Write("Folder/a.txt", "content"); fixture.Write("Folder/b.txt", "more"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var folder = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single();
        var destination = fixture.Temp.Child("alternate"); var mapping = new SourceSelection(new(fixture.Root, "descriptive-never-use"), destination);
        var result = await new RestoreService().RestoreDisplayedAsync(fixture.RepositoryPath, folder.Selection, original ? null : destination,
            original ? mapping : null);
        Assert.Equal(2, result.Restored); Assert.Equal(2, result.ChecksumsVerified); Assert.True(result.SelectionComplete);
        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "Folder", "a.txt")));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task ExactRestorePolicyCancellationAndStaleCaptureAreExplicit()
    {
        using var fixture = new Fixture(); fixture.Write("a.txt", "content"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var version = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single().Version!;
        var service = new RestoreService(); var destination = fixture.Temp.Child("restored.txt");
        Assert.Equal(1, (await service.RestoreExactAsync(fixture.RepositoryPath, version, destination)).Restored);
        Assert.Equal(1, (await service.RestoreExactAsync(fixture.RepositoryPath, version, destination)).Failed);
        Assert.Equal(1, (await service.RestoreExactAsync(fixture.RepositoryPath, version, destination, ExistingTargetPolicy.SkipExisting)).Skipped);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RestoreExactAsync(fixture.RepositoryPath, version, destination, token: cancelled.Token));
        fixture.Write("b.txt", "changed"); fixture.Scan();
        Assert.Equal(ApplicationFailure.RefreshRequired, (await Assert.ThrowsAsync<ApplicationOperationException>(
            () => service.RestoreExactAsync(fixture.RepositoryPath, version, destination))).Failure);
        Assert.Equal("content", File.ReadAllText(destination));
    }

    [WindowsFact]
    public async Task CancellationAfterPasswordAuthenticationDoesNotEnroll()
    {
        using var temp = new TemporaryDirectory(); using (Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password)) { }
        var header = File.ReadAllBytes(Path.Combine(temp.Child(), "repository.json")); using var cancel = new CancellationTokenSource();
        var setup = new RepositorySetupService { BeforeEnrollment = _ => cancel.Cancel() };
        var password = EncryptionTests.Password.ToCharArray();
        Assert.Equal(RecoveryOutcome.Cancelled, (await setup.RecoverAsync(temp.Child(), password, cancel.Token)).Outcome);
        Assert.Equal(header, File.ReadAllBytes(Path.Combine(temp.Child(), "repository.json"))); Assert.All(password, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task RestoreCancellationDuringCopyDisposesOwnedRepositoryAndReportsPartialProgress()
    {
        using var fixture = new Fixture(); fixture.Write("Folder/a.txt", "a"); fixture.Write("Folder/b.txt", "b"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var folder = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single(); using var cancel = new CancellationTokenSource();
        var service = new RestoreService { ConfigureEngine = engine => engine.Observe = (stage, _) => { if (stage == RestoreStage.Installed) cancel.Cancel(); } };
        var result = await service.RestoreDisplayedAsync(fixture.RepositoryPath, folder.Selection, fixture.Temp.Child("restore"), token: cancel.Token);
        Assert.True(result.Cancelled); Assert.Equal(1, result.Restored); Assert.Equal(2, result.Selected);
        using var repository = Repository.Open(fixture.RepositoryPath); using var writer = repository.OpenWriter();
    }

    [Fact]
    public async Task RestoreProblemsAreBoundedAndDetached()
    {
        using var fixture = new Fixture(); for (var i = 0; i < 260; i++) fixture.Write($"Folder/{i:D3}.txt", "a"); fixture.Scan();
        await using var browser = await HistoryBrowserSession.OpenAsync(fixture.RepositoryPath);
        var folder = (await browser.GetChildrenAsync(new(fixture.Root))).Items.Single();
        var result = await new RestoreService().RestoreDisplayedAsync(fixture.RepositoryPath, folder.Selection, fixture.Source,
            options: new(FailurePolicy: RestoreFailurePolicy.ContinueIndependentFiles));
        Assert.Equal(260, result.Failed); Assert.Equal(256, result.Problems.Length); Assert.Equal(0, result.Restored);
    }

    internal sealed class Fixture : IDisposable
    {
        internal TemporaryDirectory Temp { get; } = new();
        internal Guid Root { get; } = Guid.NewGuid();
        internal string Source { get; }
        internal string RepositoryPath => Temp.Child();
        internal Fixture() { Source = Directory.CreateDirectory(Temp.Child("source")).FullName; using (Repository.Create(RepositoryPath)) { } }
        internal void Write(string name, string contents)
        { var path = Path.Combine(Source, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
        internal void Scan()
        { using var repository = Repository.Open(RepositoryPath); new FileHistoryEngine(repository).Scan([new(new(Root, Source), Source)]); }
        public void Dispose() => Temp.Dispose();
    }
}
