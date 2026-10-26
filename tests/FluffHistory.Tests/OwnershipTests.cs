using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class OwnershipTests
{
    [Fact]
    public async Task ChildWriterExcludesAnotherProcessAndReleasesTheSamePersistentLock()
    {
        using var temp = new TemporaryDirectory();
        var repository = Repository.Create(temp.Child());
        var lockPath = Path.Combine(temp.Child(), "writer.lock");
        var stamp = File.GetCreationTimeUtc(lockPath);
        await using (var owner = WriterProcess.Start("hold-writer", temp.Child()))
        {
            await owner.WaitUntilReadyAsync();
            Assert.False(owner.OwnerHasExited);
            Assert.Throws<IOException>(() => repository.OpenWriter());
            using (var reader = repository.OpenReader()) Assert.Empty(reader.EnumerateCurrent());
            await using (var contender = WriterProcess.Start("try-writer", temp.Child()))
                Assert.Equal(10, await contender.ExitCodeAsync());
        }
        await using (var next = WriterProcess.Start("try-writer", temp.Child()))
            Assert.Equal(0, await next.ExitCodeAsync());
        Assert.True(File.Exists(lockPath));
        Assert.Equal(stamp, File.GetCreationTimeUtc(lockPath));
    }

    [Fact]
    public void RepositoryPathCannotTraverseASymbolicLink()
    {
        using var temp = new TemporaryDirectory();
        Repository.Create(temp.Child());
        var link = temp.Child("alias");
        Directory.CreateSymbolicLink(link, temp.Child());
        try
        {
            Assert.Throws<IOException>(() => Repository.Open(link));
            Assert.Throws<IOException>(() => Repository.Create(Path.Combine(link, "nested")));
        }
        finally { Directory.Delete(link); }
    }
}
