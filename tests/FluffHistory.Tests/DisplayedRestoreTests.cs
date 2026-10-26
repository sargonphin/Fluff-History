using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class DisplayedRestoreTests
{
    [Fact]
    public void ExactPlannerRestoresChosenIdentityAcrossSameTimestampTiesAndPreservesDisplayedValidation()
    {
        using var f = new HistoryFixture(); var old = f.Add("Folder/a", "old", 2); var newer = f.Add("Folder/a", "new", 2);
        var planner = new DisplayedRestorePlanner(f.Repository); using var reader = f.Repository.OpenReader();
        var output = f.Temp.Child("out");
        using var exact = planner.PlanExact(reader.Authority, old.Identity, InteractiveRestoreDestination.AnotherFolder, output);
        Assert.Equal(old.Identity, Assert.Single(exact.Files).Version.Identity); Assert.Equal(Path.Combine(output, "a"), exact.Files[0].DestinationPath);
        Assert.Throws<ArgumentException>(() => planner.Plan(Request(f.Repository, output, [new DisplayedRestoreFile(old.Identity)], "Folder", new(old.StoredUtc))));
        using var original = planner.PlanExact(reader.Authority, old.Identity, InteractiveRestoreDestination.OriginalLocation,
            originalSource: new(new(FoundationTests.Root, "old-descriptive-path"), f.Temp.Child("source")));
        Assert.Equal(Path.Combine(f.Temp.Child("source"), "FOLDER", "a"), original.Files[0].DestinationPath);
        Assert.Throws<ArgumentException>(() => planner.PlanExact(reader.Authority, old.Identity, InteractiveRestoreDestination.OriginalLocation,
            originalSource: new(new(Guid.NewGuid(), "wrong"), f.Temp.Child("source"))));
        f.Add("unrelated");
        Assert.Throws<ArgumentException>(() => planner.PlanExact(reader.Authority, old.Identity, InteractiveRestoreDestination.AnotherFolder, output));
        Assert.False(Directory.Exists(output)); Assert.NotEqual(old.Identity, newer.Identity);
    }
    internal static DisplayedRestoreRequest Request(Repository repo, string destination, IReadOnlyList<DisplayedRestoreItem> items,
        string folder = "", HistoryView? view = null)
    {
        using var r = repo.OpenReader();
        return new(r.Authority, new(FoundationTests.Root, folder), view ?? new(), items, InteractiveRestoreDestination.AnotherFolder, destination);
    }

    [Fact]
    public void DisplayedFilesAndFoldersKeepOneTimeViewAndStripOnlyDisplayedPrefix()
    {
        using var f = new HistoryFixture(); var a = f.Add("Documents/Report.txt", "old", 2);
        f.Add("Documents/Report.txt", "new", 5); var b = f.Add("Documents/Work/Nested/B.txt", "old-b", 1);
        f.Add("Documents/Work/Nested/B.txt", "rolled-latest", 0); f.Add("Documents/Work/too-new", day: 9);
        f.Add("Documents2/unrelated"); var destination = f.Temp.Child("out");
        var request = Request(f.Repository, destination, [new DisplayedRestoreFile(a.Identity), new DisplayedRestoreFolder("Work")], "documents", new(HistoryFixture.Time.AddDays(2)));
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(request);
        Assert.Equal(new[] { a.Identity, b.Identity }, plan.Files.Select(v => v.Version.Identity));
        Assert.Equal(Path.Combine(destination, "Report.txt"), plan.Files[0].DestinationPath);
        Assert.Equal(Path.Combine(destination, "Work", "Nested", "B.txt"), plan.Files[1].DestinationPath);
        Assert.False(Directory.Exists(destination)); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        // Original mapping places the displayed folder under its explicit current root.
        var original = request with { DestinationMode = InteractiveRestoreDestination.OriginalLocation, DestinationDirectory = null,
            OriginalSource = new(new(FoundationTests.Root, "obsolete-descriptive-root"), f.Temp.Child("source")) };
        using var inPlace = new DisplayedRestorePlanner(f.Repository).Plan(original);
        Assert.Equal(Path.Combine(f.Temp.Child("source"), "DOCUMENTS", "Report.txt"), inPlace.Files[0].DestinationPath);
    }

    [Theory]
    [InlineData("old-file")] [InlineData("grandchild")] [InlineData("wrong-root")] [InlineData("wrong-uuid")]
    [InlineData("grandfolder")] [InlineData("missing-folder")] [InlineData("wrong-view")] [InlineData("stale")]
    public void InvalidOrStaleDisplayedSelectionsAreRejected(string kind)
    {
        using var f = new HistoryFixture(); var old = f.Add("Docs/a", day: 1); var newest = f.Add("Docs/a", day: 5);
        var nested = f.Add("Docs/Sub/b"); var dest = f.Temp.Child("out");
        DisplayedRestoreItem item = kind switch
        {
            "old-file" or "wrong-view" => new DisplayedRestoreFile(old.Identity),
            "grandchild" => new DisplayedRestoreFile(nested.Identity),
            "wrong-root" => new DisplayedRestoreFile(newest.Identity with { Path = new(Guid.NewGuid(), "Docs/a") }),
            "wrong-uuid" => new DisplayedRestoreFile(newest.Identity with { VersionId = Guid.NewGuid() }),
            "grandfolder" => new DisplayedRestoreFolder("Sub/grandchild"),
            "missing-folder" => new DisplayedRestoreFolder("absent"),
            _ => new DisplayedRestoreFile(newest.Identity)
        };
        var request = Request(f.Repository, dest, [item], "Docs", kind == "wrong-view" ? new(HistoryFixture.Time) : null);
        if (kind == "stale") f.Add("more");
        Assert.Throws<ArgumentException>(() => new DisplayedRestorePlanner(f.Repository).Plan(request));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void IntrinsicConflictsApplyOnlyToExpandedSelectedMaterializationAndDuplicateSelectionsCoalesce()
    {
        using var f = new HistoryFixture(); var file = f.Add("Foo"); var child = f.Add("Foo/Bar.txt");
        var destination = f.Temp.Child("out"); var planner = new DisplayedRestorePlanner(f.Repository);
        var both = Request(f.Repository, destination, [new DisplayedRestoreFile(file.Identity), new DisplayedRestoreFolder("Foo")]);
        Assert.Throws<RestoreStructureConflictException>(() => planner.Plan(both)); Assert.False(Directory.Exists(destination));
        using var onlyFile = planner.Plan(both with { Items = [new DisplayedRestoreFile(file.Identity), new DisplayedRestoreFile(file.Identity)] });
        Assert.Equal(file.Identity, Assert.Single(onlyFile.Files).Version.Identity);
        using var onlyFolder = planner.Plan(both with { Items = [new DisplayedRestoreFolder("Foo"), new DisplayedRestoreFolder("foo")] });
        Assert.Equal(child.Identity, Assert.Single(onlyFolder.Files).Version.Identity);
    }

    [Fact]
    public void PlanBoundsCancellationMappingAndDestinationStructureFailBeforeCreatingDirectories()
    {
        using var f = new HistoryFixture(); f.Add("Folder/a"); f.Add("Folder/b"); var output = f.Temp.Child("out");
        var request = Request(f.Repository, output, [new DisplayedRestoreFolder("Folder")]); var planner = new DisplayedRestorePlanner(f.Repository);
        Assert.Throws<InvalidOperationException>(() => planner.Plan(request, new(1)));
        Assert.Throws<InvalidOperationException>(() => planner.Plan(request, new(MaximumChargedBytes: 1024)));
        Assert.Throws<OperationCanceledException>(() => planner.Plan(request, cancellationToken: new(true)));
        Assert.Throws<ArgumentException>(() => planner.Plan(request with { DestinationDirectory = f.Repository.DirectoryPath }));
        Assert.Throws<ArgumentException>(() => planner.Plan(request with { DestinationMode = InteractiveRestoreDestination.OriginalLocation, DestinationDirectory = null }));
        Assert.False(Directory.Exists(output)); Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "Folder"), "parent file");
        Assert.Throws<IOException>(() => planner.Plan(request)); Assert.Equal("parent file", File.ReadAllText(Path.Combine(output, "Folder")));
    }

    [Fact]
    public void RequiredReparseParentIsRejected()
    {
        using var f = new HistoryFixture(); f.Add("Folder/a"); var output = f.Temp.Child("out"); var outside = f.Temp.Child("outside");
        Directory.CreateDirectory(output); Directory.CreateDirectory(outside); Directory.CreateSymbolicLink(Path.Combine(output, "Folder"), outside);
        Assert.Throws<IOException>(() => new DisplayedRestorePlanner(f.Repository).Plan(Request(f.Repository, output, [new DisplayedRestoreFolder("Folder")])));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }
}
