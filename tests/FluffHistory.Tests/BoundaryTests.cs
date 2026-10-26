using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class BoundaryTests
{
    [WindowsFact]
    public async Task CrossProcessReaderDefersPhysicalRetirementUntilHandleRelease()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var w = repo.OpenWriter()) FoundationTests.Add(repo, w);
        await using (var child = WriterProcess.Start("reader", temp.Child()))
        {
            await child.WaitUntilReadyAsync();
            using (var w = repo.OpenWriter()) { w.CompactMetadata(); Assert.True(w.CleanupRetiredMetadata().DeferredSegments > 0); }
            await child.KillAsync();
        }
        using (var w = repo.OpenWriter()) Assert.True(w.CleanupRetiredMetadata().DeletedSegments > 0);
    }
    [Fact]
    public void ReaderWithNoPinnedSegmentExplicitlyExpiresAfterRetirement()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var w = repo.OpenWriter(); FoundationTests.Add(repo, w);
        using var old = repo.OpenReader(); w.CompactMetadata(); Assert.True(w.CleanupRetiredMetadata().DeletedSegments > 0);
        Assert.Throws<ReaderGenerationExpiredException>(() => old.GetCurrent(new(FoundationTests.Root, "foo.txt")));
    }
    [Fact]
    public void SequenceOverflowDoesNotWriteMetadata()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        Authority prior; using (var r = repo.OpenReader()) prior = r.Captured;
        using (var append = new SegmentAppender(temp.Child(), repo.Identity, prior.Root.Epoch, prior))
        {
            var root = prior.Root with { Generation = long.MaxValue, Sequence = long.MaxValue, PreviousGeneration = long.MaxValue - 1, PreviousTransactionId = Guid.NewGuid(), TransactionId = Guid.NewGuid(), PreviousRootHash = prior.Address.Sha256 };
            var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            AuthorityFiles.WriteNew(Path.Combine(temp.Child(), "fixture.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, address))); AuthorityFiles.Replace(temp.Child(), "fixture.tmp");
        }
        var before = TemporaryDirectory.Snapshot(temp.Child());
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader()) Assert.Throws<OverflowException>(() => w.Apply(new(sourceRoots: [new(Guid.NewGuid(), "Source")]), r.Authority));
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
    }
    [Fact]
    public void ConflictingOrMalformedCandidateIsPreservedAndBlocksMutation()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var tx = Guid.NewGuid(); var path = Path.Combine(temp.Child(), $"state.{tx:N}.tmp"); File.WriteAllText(path, "evidence");
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader()) Assert.Throws<IOException>(() => w.Apply(new(), r.Authority));
        using (var w = repo.OpenWriter()) Assert.Throws<InvalidDataException>(() => w.Reconcile(tx)); Assert.Equal("evidence", File.ReadAllText(path));
    }
    [WindowsFact]
    public void ActualReplaceSharingFailureLeavesExactPredecessorAndReconcilesUnpublished()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var blocker = new FileStream(Path.Combine(temp.Child(), "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var w = repo.OpenWriter()) Assert.Throws<IOException>(() => FoundationTests.Add(repo, w));
        using (var r = repo.OpenReader()) Assert.Equal(0, r.Authority.Generation);
        var candidate = Assert.Single(Directory.GetFiles(temp.Child(), "state.*.tmp")); var id = Guid.ParseExact(Path.GetFileName(candidate).Split('.')[1], "N");
        using (var w = repo.OpenWriter()) Assert.Equal(PublicationOutcome.Unpublished, w.Reconcile(id));
    }
    [Fact]
    public void UncertainPostReplaceFailurePreservesPublishedCandidateIdentity()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); PublicationUncertainException error;
        using (var w = repo.OpenWriter())
        {
            w.Observe = s => { if (s == PublicationStage.AfterReplace) w.LoseHandleForTest(); };
            error = Assert.Throws<PublicationUncertainException>(() => FoundationTests.Add(repo, w));
        }
        using (var w = repo.OpenWriter()) Assert.Equal(PublicationOutcome.Published, w.Reconcile(error.IntendedAuthority.TransactionId));
        using var r = repo.OpenReader(); Assert.Single(r.EnumerateCurrent());
    }
    [Fact]
    public void FailureDuringForcedRotationNeverAdoptsUnpublishedVersion()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var w = repo.OpenWriter()) FoundationTests.Add(repo, w);
        using (var w = repo.OpenWriter()) { w.RotationBytes = 32768; w.Observe = s => { if (s == PublicationStage.MetadataFlushed) throw new IOException("stop"); }; Assert.Throws<IOException>(() => FoundationTests.Add(repo, w, "orphan")); }
        using (var r = repo.OpenReader()) { Assert.Single(r.EnumerateCurrent()); Assert.Empty(r.EnumerateVersions(new(FoundationTests.Root, "orphan"))); }
        using (var w = repo.OpenWriter())
        {
            // Candidate control now precedes destination installation; explicitly reconcile
            // the unpublished preparation before independent repair, as for any pending candidate.
            foreach (var id in w.EnumeratePendingPublications()) Assert.Equal(PublicationOutcome.Unpublished, w.Reconcile(id));
            w.RebuildIndexes();
        }
        using var fresh = repo.OpenReader(); Assert.Empty(fresh.EnumerateVersions(new(FoundationTests.Root, "orphan")));
    }
    [Fact]
    public void CompactionBaseCanBeCompactedAgainAndRetiredChainRemainsSafe()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var w = repo.OpenWriter();
        FoundationTests.Add(repo, w); FoundationTests.Add(repo, w);
        w.CompactMetadata(); FoundationTests.Add(repo, w);
        using (var r = repo.OpenReader()) w.Apply(new(currentRemovals: [new(FoundationTests.Root, "foo.txt")]), r.Authority);
        w.CompactMetadata(); w.RebuildIndexes(); w.CleanupRetiredMetadata();
        using var fresh = repo.OpenReader(); Assert.Empty(fresh.EnumerateCurrent()); Assert.Equal(3, fresh.EnumerateVersions(new(FoundationTests.Root, "foo.txt")).Count());
    }
}
