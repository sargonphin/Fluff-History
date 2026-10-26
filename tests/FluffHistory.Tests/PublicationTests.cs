using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class PublicationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FaultBoundariesPreserveExactOldOrNewAuthority(int boundary)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); AuthorityIdentity previous;
        using (var r = repo.OpenReader()) previous = r.Authority;
        using (var w = repo.OpenWriter())
        {
            w.Observe = stage => { if ((int)stage == boundary) throw new IOException("Injected interruption"); };
            if (boundary == 3) Assert.True(FoundationTests.Add(repo, w).Changed);
            else Assert.Throws<IOException>(() => FoundationTests.Add(repo, w));
        }
        using (var r = repo.OpenReader()) Assert.Equal(boundary == 3 ? 1 : 0, r.Authority.MutationSequence);
        using (var w = repo.OpenWriter())
        {
            foreach (var name in Directory.GetFiles(temp.Child(), "state.*.tmp"))
            {
                var id = Guid.ParseExact(Path.GetFileName(name).Split('.')[1], "N");
                Assert.Equal(PublicationOutcome.Unpublished, w.Reconcile(id));
            }
            FoundationTests.Add(repo, w, "after.txt");
        }
        using var fresh = repo.OpenReader(); Assert.NotNull(fresh.GetCurrent(new(FoundationTests.Root, "after.txt"))); Assert.Equal(boundary == 3 ? 2 : 1, fresh.Authority.MutationSequence);
        Assert.Equal(0, previous.MutationSequence);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CancellationBeforeAndAfterReplacementIsUnambiguous(int boundary)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var cts = new CancellationTokenSource();
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader())
        {
            w.Observe = stage => { if ((int)stage == boundary) cts.Cancel(); };
            var batch = new MutationBatch(sourceRoots: [new(FoundationTests.Root, "Source")]);
            if (boundary == 3) Assert.True(w.Apply(batch, r.Authority, cts.Token).Changed);
            else Assert.Throws<OperationCanceledException>(() => w.Apply(batch, r.Authority, cts.Token));
        }
        using var fresh = repo.OpenReader(); Assert.Equal(boundary == 3 ? 1 : 0, fresh.Authority.MutationSequence);
    }
    [Fact]
    public void CapturedReaderNeverMixesGenerationsAndFreshReaderSeesReplacement()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var old = repo.OpenReader(); using var w = repo.OpenWriter();
        w.Observe = stage =>
        {
            using var fresh = repo.OpenReader(); Assert.Empty(old.EnumerateCurrent());
            Assert.Equal(stage == PublicationStage.AfterReplace ? 1 : 0, fresh.EnumerateCurrent().Count());
        };
        FoundationTests.Add(repo, w); Assert.Empty(old.EnumerateCurrent());
    }
    [Fact]
    public void StaleAuthorityAndLostHandleAbortWithoutTransparentReacquisition()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); AuthorityIdentity old;
        using (var r = repo.OpenReader()) old = r.Authority;
        using (var w = repo.OpenWriter()) { FoundationTests.Add(repo, w); Assert.Throws<InvalidOperationException>(() => w.Apply(new(), old)); }
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader())
        {
            w.Observe = stage => { if (stage == PublicationStage.BeforeReplace) w.LoseHandleForTest(); };
            Assert.Throws<ObjectDisposedException>(() => w.Apply(new(sourceRoots: [new(Guid.NewGuid(), "Source")]), r.Authority));
        }
        using var fresh = repo.OpenReader(); Assert.Equal(1, fresh.Authority.MutationSequence);
    }
    [Theory]
    [InlineData("before-replace", 0)]
    [InlineData("after-replace", 1)]
    public async Task ProcessDeathLeavesExactAuthorityAndReleasesPersistentLock(string mode, long sequence)
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        await using (var child = WriterProcess.Start(mode, temp.Child())) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        using (var r = Repository.Open(temp.Child()).OpenReader()) Assert.Equal(sequence, r.Authority.MutationSequence);
        using (var w = repo.OpenWriter()) Assert.True(File.Exists(Path.Combine(temp.Child(), "writer.lock")));
    }
    [WindowsFact]
    public void ReplaceFileSupportsAnOpenOldAuthorityHandle()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var path = Path.Combine(temp.Child(), "state.json"); var bytes = File.ReadAllBytes(path);
        using (var old = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var w = repo.OpenWriter())
        {
            FoundationTests.Add(repo, w); var captured = new byte[bytes.Length]; old.ReadExactly(captured); Assert.Equal(bytes, captured);
            using var fresh = repo.OpenReader(); Assert.Equal(1, fresh.Authority.MutationSequence);
        }
    }
    [WindowsFact]
    public void ReaderHandleDefersRetiredSegmentDeletion()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var w = repo.OpenWriter(); FoundationTests.Add(repo, w);
        using (var old = repo.OpenReader())
        {
            Assert.NotNull(old.GetCurrent(new(FoundationTests.Root, "foo.txt"))); w.CompactMetadata();
            Assert.True(w.CleanupRetiredMetadata().DeferredSegments > 0); Assert.NotNull(old.GetCurrent(new(FoundationTests.Root, "foo.txt")));
        }
        Assert.True(w.CleanupRetiredMetadata().DeletedSegments > 0);
    }
    [Fact]
    public void CompactionFailureCannotReplaceCurrentAuthorityAndBudgetsAreEnforced()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using (var w = repo.OpenWriter()) FoundationTests.Add(repo, w);
        using (var w = repo.OpenWriter()) Assert.Throws<IOException>(() => w.CompactMetadata(new(MaximumRecords: 1)));
        using (var r = repo.OpenReader()) { Assert.Equal(1, r.Authority.Generation); Assert.Single(r.EnumerateCurrent()); }
        using (var w = repo.OpenWriter()) { w.Observe = s => { if (s == PublicationStage.BeforeReplace) throw new IOException("stop"); }; Assert.Throws<IOException>(() => w.CompactMetadata()); }
        using var fresh = repo.OpenReader(); Assert.Equal(1, fresh.Authority.Generation); Assert.Single(fresh.EnumerateCurrent());
    }
}
