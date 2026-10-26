using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using Xunit;

namespace FluffHistory.Tests;

public sealed class DirectoryReplacementTests
{
    [Fact]
    public void PositivelyObservedExcludedDirectoryRetiresExactCurrentFileThroughApplicationProjection()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private", "old file"); f.Write("Other/keep.txt", "keep");
        var settings = AppSettings.Empty with { RepositoryPath = f.RepositoryPath,
            Sources = [SourceMapping.FromConfiguration(new(f.Root, f.Source), f.Source)] };
        using var repository = Repository.Open(f.RepositoryPath);
        var engine = new FileHistoryEngine(repository) { Clock = () => FoundationTests.Time };
        engine.Scan(settings.ToBackupConfiguration().Sources);
        var path = new HistoryPath(f.Root, "Private");
        using var before = repository.OpenReader(); var original = before.GetCurrent(path)!.Identity;
        File.Delete(Path.Combine(f.Source, "Private")); f.Write("Private/child.txt", "not traversed");
        settings = settings with { ExcludedFolders = [Path.Combine(f.Source, "Private")] };
        var result = engine.Scan(settings.ToBackupConfiguration().Sources);
        Assert.Equal(ScanOutcome.Completed, result.Outcome); Assert.Equal(1, result.Removals);
        Assert.Equal(2, result.DirectoriesEnumerated); // Root and Other only: excluded Private was not traversed.
        Assert.Equal(0, result.VersionsCreated); Assert.Equal(0, result.PayloadBytesWritten);
        using var after = repository.OpenReader();
        Assert.Null(after.GetCurrent(path)); Assert.Equal(original, Assert.Single(after.EnumerateVersions(path)).Identity);
        Assert.Null(after.GetCurrent(new(f.Root, "Private/child.txt"))); Assert.Empty(after.EnumerateVersions(new(f.Root, "Private/child.txt")));
        Assert.NotNull(after.GetCurrent(new(f.Root, "Other/keep.txt")));
        var retention = new RetentionEngine(repository);
        Assert.Equal(0, retention.Preview(RetentionPolicy.Forever, FoundationTests.Time.AddDays(100)).Eligible);
        Assert.Equal(1, retention.Preview(RetentionPolicy.OneMonth, FoundationTests.Time.AddDays(100)).Eligible);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EmptyDirectoryThenEligibleDescendantPreservesBothHistoricalStructures(bool excluded)
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private", "old"); f.Write("keep.txt", "keep");
        var settings = Settings(f); using var repository = Repository.Open(f.RepositoryPath);
        var engine = new FileHistoryEngine(repository); engine.Scan(settings.ToBackupConfiguration().Sources);
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var old = Assert.Single((await browser.GetPathHistoryAsync(f.Root, "Private")).Items);
        File.Delete(Path.Combine(f.Source, "Private")); Directory.CreateDirectory(Path.Combine(f.Source, "Private"));
        if (excluded) settings = settings with { ExcludedFolders = [Path.Combine(f.Source, "Private")] };
        var empty = engine.Scan(settings.ToBackupConfiguration().Sources);
        Assert.Equal(ScanOutcome.Completed, empty.Outcome); Assert.Equal(1, empty.Removals);
        Assert.Equal(0, empty.VersionsCreated); Assert.Equal(0, empty.PayloadBytesWritten);
        await browser.RefreshAsync();
        var emptyRows = (await browser.GetChildrenAsync(new(f.Root))).Items;
        Assert.DoesNotContain(emptyRows, c => c.IsFolder);
        Assert.False(Assert.Single((await browser.GetPathHistoryAsync(f.Root, "Private")).Items).IsCurrent);
        f.Write("Private/child.txt", "child");
        settings = settings with { ExcludedFolders = [] };
        var resumed = engine.Scan(settings.ToBackupConfiguration().Sources);
        Assert.Equal(1, resumed.VersionsCreated); Assert.Equal(0, resumed.Reactivations);
        Assert.Equal(f.Root, Assert.Single(settings.Sources).SourceRootId);
        await browser.RefreshAsync();
        var rows = (await browser.GetChildrenAsync(new(f.Root))).Items;
        var pair = rows.Where(c => c.Name == "Private").ToArray();
        Assert.Equal(2, pair.Length); Assert.All(pair, c => Assert.True(c.HasConflict));
        var file = Assert.Single(pair, c => !c.IsFolder); var folder = Assert.Single(pair, c => c.IsFolder);
        Assert.False(file.Version!.IsCurrent); Assert.Equal(old.Identity, file.Version.Identity);
        var child = Assert.Single((await browser.GetChildrenAsync(new(f.Root, "Private"))).Items);
        Assert.True(child.Version!.IsCurrent);
        var restore = new RestoreService();
        Assert.Equal(ApplicationFailure.RestoreStructureConflict, (await Assert.ThrowsAsync<ApplicationOperationException>(() => restore.RestoreDisplayedAsync(
            f.RepositoryPath, browser.CreateSelection(pair), f.Temp.Child("combined")))).Failure);
        Assert.False(Directory.Exists(f.Temp.Child("combined")));
        Assert.Equal(1, (await restore.RestoreDisplayedAsync(f.RepositoryPath, file.Selection, f.Temp.Child("file-only"))).Restored);
        Assert.Equal(1, (await restore.RestoreDisplayedAsync(f.RepositoryPath, folder.Selection, f.Temp.Child("folder-only"))).Restored);
        Assert.Equal(1, (await restore.RestoreDisplayedAsync(f.RepositoryPath, Assert.Single(rows, c => c.Name == "keep.txt").Selection,
            f.Temp.Child("unrelated"))).Restored);
    }

    [Fact]
    public void SavedDirectoryExclusionDoesNotExcludeAFutureExactRegularFile()
    {
        using var f = new ApplicationAdapterTests.Fixture(); Directory.CreateDirectory(Path.Combine(f.Source, "Private"));
        var settings = Settings(f) with { ExcludedFolders = [Path.Combine(f.Source, "Private")] };
        using var repository = Repository.Open(f.RepositoryPath); var engine = new FileHistoryEngine(repository);
        engine.Scan(settings.ToBackupConfiguration().Sources);
        Directory.Delete(Path.Combine(f.Source, "Private")); f.Write("Private", "regular file");
        Assert.Equal(1, engine.Scan(settings.ToBackupConfiguration().Sources).VersionsCreated);
        using var reader = repository.OpenReader(); Assert.NotNull(reader.GetCurrent(new(f.Root, "Private")));
    }

    [Fact]
    public async Task DirectoryBecomesRegularFileRetiresOnlyPriorDescendantMembership()
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Foo/child.txt", "child"); f.Write("keep.txt", "keep"); f.Scan();
        Directory.Delete(Path.Combine(f.Source, "Foo"), true); f.Write("Foo", "file");
        using var repository = Repository.Open(f.RepositoryPath);
        var result = new FileHistoryEngine(repository).Scan(Settings(f).ToBackupConfiguration().Sources);
        Assert.Equal(1, result.Removals); Assert.Equal(1, result.VersionsCreated);
        using var reader = repository.OpenReader(); Assert.Null(reader.GetCurrent(new(f.Root, "Foo/child.txt")));
        Assert.Single(reader.EnumerateVersions(new(f.Root, "Foo/child.txt")));
        Assert.NotNull(reader.GetCurrent(new(f.Root, "Foo"))); Assert.NotNull(reader.GetCurrent(new(f.Root, "keep.txt")));
        await using var browser = await HistoryBrowserSession.OpenAsync(f.RepositoryPath);
        var pair = (await browser.GetChildrenAsync(new(f.Root))).Items.Where(c => c.Name == "Foo").ToArray();
        Assert.Equal(2, pair.Length); Assert.All(pair, c => Assert.True(c.HasConflict));
    }

    [Theory]
    [InlineData("denied")] [InlineData("failed")] [InlineData("reparse")] [InlineData("encrypted")] [InlineData("device")]
    public void UntrustedObservationNeverRetiresExactCurrent(string failure)
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private", "old"); f.Scan();
        File.Delete(Path.Combine(f.Source, "Private")); Directory.CreateDirectory(Path.Combine(f.Source, "Private"));
        var access = new ControlledSource(Path.Combine(f.Source, "Private"), failure);
        using var repository = Repository.Open(f.RepositoryPath);
        var settings = Settings(f) with { ExcludedFolders = [Path.Combine(f.Source, "Private")] };
        var result = new FileHistoryEngine(repository, new(), access).Scan(settings.ToBackupConfiguration().Sources);
        Assert.Equal(0, result.Removals); Assert.Equal(1, result.SourceSkips);
        using var reader = repository.OpenReader(); Assert.NotNull(reader.GetCurrent(new(f.Root, "Private")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CancellationReportsOnlyCommittedRemovalAndRetryDoesNotDuplicate(bool afterCommit)
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private", "old");
        var settings = Settings(f) with { ExcludedFolders = [Path.Combine(f.Source, "Private")] };
        using var repository = Repository.Open(f.RepositoryPath);
        new FileHistoryEngine(repository).Scan(settings.ToBackupConfiguration().Sources);
        File.Delete(Path.Combine(f.Source, "Private")); Directory.CreateDirectory(Path.Combine(f.Source, "Private"));
        using var cancel = new CancellationTokenSource();
        var access = new ControlledSource(Path.Combine(f.Source, "Private"), "ordinary", afterCommit ? null : cancel.Cancel);
        var engine = new FileHistoryEngine(repository, new(BatchSize: 1), access);
        if (afterCommit) engine.BatchCommitted = result => { if (result.Removals != 0) cancel.Cancel(); };
        var cancelled = engine.Scan(settings.ToBackupConfiguration().Sources, cancel.Token);
        Assert.True(cancelled.Cancelled); Assert.Equal(afterCommit ? 1 : 0, cancelled.Removals);
        using (var reader = repository.OpenReader()) Assert.Equal(!afterCommit, reader.GetCurrent(new(f.Root, "Private")) is not null);
        Assert.Equal(afterCommit ? 0 : 1, new FileHistoryEngine(repository).Scan(settings.ToBackupConfiguration().Sources).Removals);
        Assert.Equal(0, new FileHistoryEngine(repository).Scan(settings.ToBackupConfiguration().Sources).Removals);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SafetyBarrierAvoidsObservationWhileFailedTraversalRetainsPositiveExactTypeEvidence(bool safetyBarrier)
    {
        using var f = new ApplicationAdapterTests.Fixture(); f.Write("Private", "old"); f.Write("Other/keep.txt", "keep"); f.Scan();
        File.Delete(Path.Combine(f.Source, "Private")); Directory.CreateDirectory(Path.Combine(f.Source, "Private"));
        var observed = 0;
        var access = new ControlledSource(Path.Combine(f.Source, "Private"), "enumeration", () => observed++);
        using var repository = Repository.Open(f.RepositoryPath);
        var selection = Assert.Single(Settings(f).ToBackupConfiguration().Sources);
        if (safetyBarrier) selection = selection with { SafetyExcludedDirectories = ["Private"] };
        var result = new FileHistoryEngine(repository, new(), access).Scan([selection]);
        Assert.Equal(safetyBarrier ? 0 : 1, result.Removals);
        if (safetyBarrier) Assert.Equal(0, observed); else Assert.True(observed > 0);
        using var reader = repository.OpenReader(); Assert.Equal(safetyBarrier, reader.GetCurrent(new(f.Root, "Private")) is not null);
        Assert.NotNull(reader.GetCurrent(new(f.Root, "Other/keep.txt"))); Assert.Single(reader.EnumerateVersions(new(f.Root, "Private")));
    }

    private static AppSettings Settings(ApplicationAdapterTests.Fixture f) => AppSettings.Empty with
    { RepositoryPath = f.RepositoryPath, Sources = [SourceMapping.FromConfiguration(new(f.Root, f.Source), f.Source)] };

    private sealed class ControlledSource(string exact, string failure, Action? observed = null) : SourceAccess
    {
        internal override IEnumerable<string> EnumerateNames(string directory)
            => directory == exact && failure == "enumeration" ? throw new IOException("Controlled failed traversal.") : base.EnumerateNames(directory);
        internal override SourceObservation Observe(string path)
        {
            if (path != exact) return base.Observe(path);
            if (failure == "denied") throw new UnauthorizedAccessException("Controlled denial.");
            if (failure == "failed") throw new IOException("Controlled observation failure.");
            var observation = base.Observe(path); observed?.Invoke();
            var attributes = failure switch { "reparse" => FileAttributes.ReparsePoint, "encrypted" => FileAttributes.Encrypted,
                "device" => FileAttributes.Device, _ => (FileAttributes)0 };
            return observation with { Attributes = observation.Attributes | attributes };
        }
    }
}
