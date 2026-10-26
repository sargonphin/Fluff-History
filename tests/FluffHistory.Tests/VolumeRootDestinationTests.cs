using FluffHistory.Application;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class VolumeRootDestinationTests
{
    public static TheoryData<string, bool> Artifacts => new()
    {
        { "System Volume Information", true }, { "$RECYCLE.BIN", true }, { ".fseventsd", true }, { ".Spotlight-V100", true },
        { ".Trashes", true }, { ".TemporaryItems", true }, { ".DocumentRevisions-V100", true }, { "lost+found", true },
        { ".Trash", true }, { ".Trash-0", true }, { ".Trash-1000", true }, { ".DS_Store", false },
        { ".metadata_never_index", false }, { ".metadata_never_index_unless_rootfs", false }
    };

    [Theory]
    [MemberData(nameof(Artifacts))]
    public void ExactArtifactsAreIgnoredOnlyAtRootsAndWithExpectedEntryType(string name, bool directory)
    {
        var entry = new RepositoryDestinationPolicy.Entry(name, directory);
        Assert.False(RepositoryDestinationPolicy.HasUnrelatedEntries([entry], true));
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries([entry], false));
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries([entry with { Reparse = true }], true));
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries([entry with { Directory = !directory }], true));
    }

    [Theory]
    [InlineData("System Volume Information - old")]
    [InlineData("$RECYCLE.BIN.backup")]
    [InlineData(".Trash-not-a-number")]
    [InlineData(".Trash-")]
    [InlineData(".Trash-١٢")]
    [InlineData(".Trash-1000.backup")]
    [InlineData("lost+found-copy")]
    [InlineData("._document")]
    [InlineData(".git")]
    [InlineData("Windows")]
    [InlineData("Photos")]
    public void NearMissesHiddenNamesAndOrdinaryContentRemainUnrelated(string name) =>
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries([new(name, true)], true));

    [Fact]
    public void ArtifactCombinationsDoNotHideOneOrdinaryFileOrDirectory()
    {
        var entries = Artifacts.Select(row => new RepositoryDestinationPolicy.Entry((string)row[0], (bool)row[1])).ToArray();
        Assert.False(RepositoryDestinationPolicy.HasUnrelatedEntries(entries, true));
        Assert.False(RepositoryDestinationPolicy.HasUnrelatedEntries([], false));
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries(entries.Append(new("file.txt", false)), true));
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries(entries.Append(new("Photos", true)), true));
        Assert.True(RepositoryDestinationPolicy.HasUnrelatedEntries(entries.Append(new("writer.lock", false)), true));
        Assert.False(RepositoryDestinationPolicy.HasUnrelatedEntries(entries.Append(new("writer.lock", false)), true, true));
    }

    [Fact]
    public async Task OrdinaryDirectoriesKeepTheirExistingEmptyUnrelatedAndDiscoveryRulesWithoutTouchingArtifacts()
    {
        using var temp = new TemporaryDirectory(); var setup = new RepositorySetupService();
        var path = Directory.CreateDirectory(temp.Child("destination")).FullName;
        Assert.False(RepositoryDestinationPolicy.IsLocalVolumeRoot(path));
        Assert.Equal(BackupLocationKind.Empty, (await setup.InspectLocationAsync(path)).Kind);
        var artifact = Directory.CreateDirectory(Path.Combine(path, "System Volume Information")).FullName;
        File.WriteAllText(Path.Combine(artifact, "preserve"), "OS-owned stand-in");
        var before = TemporaryDirectory.Snapshot(path);
        Assert.Equal(BackupLocationKind.Unrelated, (await setup.InspectLocationAsync(path)).Kind);
        Assert.Throws<IOException>(() => Repository.Create(path));
        Assert.Equal(before.OrderBy(p => p.Key), TemporaryDirectory.Snapshot(path).OrderBy(p => p.Key));
        using var repo = Repository.Create(temp.Child("repository"));
        Directory.CreateDirectory(Path.Combine(repo.DirectoryPath, ".fseventsd"));
        Assert.Equal(BackupLocationKind.Existing, (await setup.InspectLocationAsync(repo.DirectoryPath)).Kind);
        Assert.Equal(BackupLocationKind.Unavailable, (await setup.InspectLocationAsync(temp.Child("absent"))).Kind);
    }

    [WindowsFact]
    public void LocalDriveRootsAreRecognizedButUncRootsAreNotRelaxed()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(RepositoryDestinationPolicy.IsLocalVolumeRoot(root));
        Assert.False(RepositoryDestinationPolicy.IsLocalVolumeRoot(@"\\server\share\"));
    }
}
