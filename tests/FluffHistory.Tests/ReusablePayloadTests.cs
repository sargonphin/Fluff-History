using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

internal sealed class ReuseFixture : IDisposable
{
    internal readonly TemporaryDirectory Temp = new();
    internal Repository Repository { get; }
    internal static HistoryPath PathKey => new(FoundationTests.Root, "foo.txt");
    internal static XxHash64Checksum Hash(byte[] bytes) => new(Convert.ToHexString(XxHash64.Hash(bytes)));
    internal static LogicalFileMetadata Metadata(long length = 5) => new(FoundationTests.Root, "foo.txt", length, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive);
    internal ReuseFixture(bool encrypted = false)
    {
        Repository = encrypted ? Repository.CreateEncrypted(Temp.Child(), EncryptionTests.Password) : Repository.Create(Temp.Child()); using var writer = Repository.OpenWriter(); using var reader = Repository.OpenReader();
        writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
    }
    internal static PayloadDraft Ready(RepositoryWriter writer, byte[]? bytes = null, HistoryPath? path = null)
    {
        bytes ??= "hello"u8.ToArray(); var draft = writer.CreatePayloadDraft(); draft.Write(bytes); draft.CompleteCopy(bytes.Length);
        Assert.True(writer.MarkPayloadReusable(draft, path ?? PathKey, FoundationTests.Time, Hash(bytes))); return draft;
    }
    internal static PreparedMutation Prepare(Repository repository, RepositoryWriter writer, PayloadDraft draft)
    {
        using var reader = repository.OpenReader();
        var version = draft.PrepareVersion(Metadata(draft.Length), FoundationTests.Time, draft.Reusable!.Checksum);
        return writer.PrepareMutation(new([version]), reader.Authority);
    }
    internal string[] Descriptors => Directory.GetFiles(Repository.DirectoryPath, "*.ready", SearchOption.AllDirectories);
    public void Dispose() => Temp.Dispose();
}

public sealed class ReusablePayloadTests
{
    [Fact]
    public void CodecRoundTripsStrictIndependentSchemaAndMaximumPath()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); using var draft = ReuseFixture.Ready(writer);
        var d = draft.Reusable!; var bytes = ReusablePayloadDescriptor.Encode(d);
        Assert.Equal(1, ReusablePayloadDescriptor.ReusablePayloadDescriptorVersion); Assert.Equal(d, ReusablePayloadDescriptor.Decode(bytes));
        var path = string.Join('/', Enumerable.Repeat(new string('a', 255), 7)) + "/" + new string('b', 255);
        var max = d with { Path = new(FoundationTests.Root, path) };
        Assert.Equal(2047, path.Length); Assert.Equal(max, ReusablePayloadDescriptor.Decode(ReusablePayloadDescriptor.Encode(max)));
        Assert.InRange(bytes.Length, 218, ReusablePayloadDescriptor.MaximumBytes);
    }
    [Theory]
    [InlineData("schema")] [InlineData("repository")] [InlineData("root")] [InlineData("payload")] [InlineData("owner")]
    [InlineData("length")] [InlineData("mtime")] [InlineData("state")] [InlineData("transaction")] [InlineData("path")]
    [InlineData("checksum")] [InlineData("trailing")] [InlineData("truncated")] [InlineData("oversized")]
    public void StrictDecoderRejectsInvalidEvidence(string bad)
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); using var draft = ReuseFixture.Ready(writer);
        var bytes = ReusablePayloadDescriptor.Encode(draft.Reusable!); var body = bytes[..^32];
        // foo.txt is seven UTF8 bytes; fixed offsets are independently derived from this codec's field table.
        var p = 12 + 16 + 16 + 4 + 7;
        switch (bad)
        {
            case "schema": body[8] = 2; break;
            case "repository": body.AsSpan(12, 16).Clear(); break;
            case "root": body.AsSpan(28, 16).Clear(); break;
            case "path": body[48] = (byte)'/'; break;
            case "payload": body.AsSpan(p, 16).Clear(); break;
            case "owner": body.AsSpan(p + 16, 16).Clear(); break;
            case "length": body.AsSpan(p + 32, 8).Fill(255); break;
            case "mtime": body.AsSpan(p + 40, 8).Clear(); break;
            case "state": body[^17] = 8; break;
            case "transaction": body[^1] = 1; break;
            case "checksum": bytes[^1] ^= 1; Assert.Throws<InvalidDataException>(() => ReusablePayloadDescriptor.Decode(bytes)); return;
            case "trailing": body = [.. body, 0]; break;
            case "truncated": Assert.Throws<InvalidDataException>(() => ReusablePayloadDescriptor.Decode(bytes[..^1])); return;
            case "oversized": Assert.Throws<InvalidDataException>(() => ReusablePayloadDescriptor.Decode(new byte[9001])); return;
        }
        var invalid = body.Concat(Convert.FromHexString(Format.Hash(body))).ToArray();
        Assert.Throws<InvalidDataException>(() => ReusablePayloadDescriptor.Decode(invalid));
    }
    [Fact]
    public void OnlyOwnedCompletedDraftsAreEligibleAndDisposalPreservesReadyOnly()
    {
        using var f = new ReuseFixture(); using var other = new ReuseFixture(); string ordinary, reusable;
        using (var writer = f.Repository.OpenWriter())
        using (var foreign = other.Repository.OpenWriter())
        {
            using (var draft = writer.CreatePayloadDraft())
            {
                ordinary = draft.StagingPath; draft.Write([1]);
                Assert.Throws<InvalidOperationException>(() => writer.MarkPayloadReusable(draft, ReuseFixture.PathKey, FoundationTests.Time, ReuseFixture.Hash([1])));
            }
            Assert.False(File.Exists(ordinary));
            using var ready = ReuseFixture.Ready(writer); reusable = ready.StagingPath;
            Assert.Throws<ArgumentException>(() => foreign.MarkPayloadReusable(ready, ReuseFixture.PathKey, FoundationTests.Time, ready.Reusable!.Checksum));
            Assert.Throws<InvalidOperationException>(() => writer.MarkPayloadReusable(ready, ReuseFixture.PathKey, FoundationTests.Time, ready.Reusable!.Checksum));
            ready.Dispose(); using var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time); Assert.NotNull(candidate);
            Assert.Throws<ArgumentException>(() => foreign.AdoptReusablePayload(candidate));
            Assert.Throws<InvalidOperationException>(() => writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
            candidate.Dispose(); Assert.Throws<ArgumentException>(() => writer.AdoptReusablePayload(candidate));
        }
        Assert.True(File.Exists(reusable)); Assert.Single(f.Descriptors);
    }
    [Theory]
    [InlineData("exact")] [InlineData("case")] [InlineData("length")] [InlineData("mtime")] [InlineData("path")] [InlineData("root")]
    public void ExactLookupUsesFrozenPathLengthAndMtime(string match)
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); ReuseFixture.Ready(writer).Dispose();
        var path = match == "root" ? new HistoryPath(Guid.NewGuid(), "foo.txt") : new(FoundationTests.Root, match == "path" ? "other" : match == "case" ? "FOO.TXT" : "foo.txt");
        using var found = writer.FindReusablePayload(path, match == "length" ? 6 : 5, match == "mtime" ? FoundationTests.Time.AddTicks(1) : FoundationTests.Time);
        Assert.Equal(match is "exact" or "case", found is not null);
        Assert.Equal(0, writer.ReusableStatistics.GlobalEntriesExamined);
    }
    [Theory]
    [InlineData("corrupt")] [InlineData("short")] [InlineData("long")] [InlineData("missing")] [InlineData("descriptor")] [InlineData("collision")]
    public void BadOptionalCacheDeclinesWithoutChangingHistory(string fault)
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); var draft = ReuseFixture.Ready(writer); var artifact = draft.StagingPath; draft.Dispose();
        var authority = f.Repository.ReadAuthority();
        switch (fault)
        {
            case "corrupt": File.WriteAllText(artifact, "jello"); break;
            case "short": File.WriteAllText(artifact, "a"); break;
            case "long": File.WriteAllText(artifact, "hello!"); break;
            case "missing": File.Delete(artifact); break;
            case "descriptor": File.WriteAllText(f.Descriptors.Single(), "bad"); break;
            case "collision":
                var d = ReusablePayloadDescriptor.Decode(File.ReadAllBytes(f.Descriptors.Single()));
                var other = d with { Path = new(FoundationTests.Root, "different") };
                File.WriteAllBytes(f.Descriptors.Single(), ReusablePayloadDescriptor.Encode(other)); break;
        }
        Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
        Assert.Equal(authority, f.Repository.ReadAuthority()); using var reader = f.Repository.OpenReader(); Assert.Empty(reader.EnumerateCurrent());
    }
    [Fact]
    public void ConflictingCandidatesAndBucketOverflowDeclineInsteadOfArbitraryReuse()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter();
        ReuseFixture.Ready(writer).Dispose(); ReuseFixture.Ready(writer, "jello"u8.ToArray()).Dispose();
        Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time)); Assert.Equal(1, writer.ReusableStatistics.Conflicts);
        for (var i = 0; i < 16; i++) ReuseFixture.Ready(writer).Dispose();
        var before = writer.ReusableStatistics.BucketEntriesExamined;
        Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
        Assert.Equal(17, writer.ReusableStatistics.BucketEntriesExamined - before);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void AdoptStageOrInstalledUnpublishedWithoutCopyAndPublishNormally(bool installed, bool encrypted)
    {
        using var f = new ReuseFixture(encrypted); Guid id;
        using (var writer = f.Repository.OpenWriter())
        {
            using var draft = ReuseFixture.Ready(writer); id = draft.VersionId;
            if (installed) { using var publication = ReuseFixture.Prepare(f.Repository, writer, draft); draft.Install(); }
        }
        using (var writer = f.Repository.OpenWriter())
        {
            using var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time); Assert.NotNull(candidate);
            using var draft = writer.AdoptReusablePayload(candidate); Assert.Equal(id, draft.VersionId);
            Assert.Equal(installed, draft.Installed); Assert.Equal(5, writer.ReusableStatistics.LocalValidationBytes);
            using var publication = ReuseFixture.Prepare(f.Repository, writer, draft);
            Assert.Empty(f.Descriptors); Assert.Equal(ReusablePayloadState.TransactionBound, draft.Reusable!.State);
            Assert.Equal(publication.IntendedAuthority.TransactionId, draft.Reusable.TransactionId);
            draft.Install(); Assert.True(publication.Commit().Changed); Assert.Equal(0, writer.ReusableStatistics.AdoptionPayloadCopyBytes);
        }
        using var reader = f.Repository.OpenReader(); Assert.Equal(id, reader.GetCurrent(ReuseFixture.PathKey)!.Identity.VersionId);
        Assert.Empty(f.Descriptors); Assert.Empty(Directory.GetFiles(f.Repository.DirectoryPath, "*.bound", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void ExplicitReconciliationUsesExistingOutcomeAndKeepsUncertaintyBound(bool published, bool encrypted)
    {
        using var f = new ReuseFixture(encrypted); Guid tx, id;
        using (var writer = f.Repository.OpenWriter())
        {
            using var draft = ReuseFixture.Ready(writer); id = draft.VersionId;
            using var publication = ReuseFixture.Prepare(f.Repository, writer, draft); tx = publication.IntendedAuthority.TransactionId; draft.Install();
            writer.Observe = stage =>
            {
                if (stage == (published ? PublicationStage.AfterReplace : PublicationStage.BeforeMetadataInstall))
                { if (published) writer.LoseHandleForTest(); else throw new IOException("injected before install"); }
            };
            Assert.ThrowsAny<IOException>(() => publication.Commit()); Assert.Empty(f.Descriptors);
        }
        using (var writer = f.Repository.OpenWriter())
        {
            if (!published) Assert.Throws<IOException>(() => writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
            else Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
            Assert.Equal(published ? PublicationOutcome.Published : PublicationOutcome.Unpublished, writer.Reconcile(tx));
            using var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time); Assert.Equal(!published, candidate is not null);
        }
        Assert.True(File.Exists(f.Repository.GetPayloadPath(id)));
    }
    [Fact]
    public void StaleReadyEvidenceNeverAdoptsOrDeletesPublishedOrRemovedUuid()
    {
        using var f = new ReuseFixture(); byte[] stale; string descriptor; Guid id;
        using (var writer = f.Repository.OpenWriter())
        {
            using var draft = ReuseFixture.Ready(writer); id = draft.VersionId; descriptor = f.Descriptors.Single(); stale = File.ReadAllBytes(descriptor);
            using var publication = ReuseFixture.Prepare(f.Repository, writer, draft); draft.Install(); publication.Commit();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!); File.WriteAllBytes(descriptor, stale);
        using (var writer = f.Repository.OpenWriter())
        {
            Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
            Assert.False(writer.RemoveReadyReusablePayload(Assert.Single(writer.GetReadyReusablePayloads().Entries)));
            Assert.True(File.Exists(f.Repository.GetPayloadPath(id)));
            using var reader = f.Repository.OpenReader(); writer.Apply(new(currentRemovals: [ReuseFixture.PathKey]), reader.Authority);
            using var fresh = f.Repository.OpenReader(); writer.RemoveVersions([fresh.GetNewestVersion(ReuseFixture.PathKey)!.Identity], fresh.Authority, cleanupPayloads: false);
        }
        File.WriteAllBytes(descriptor, stale);
        using (var writer = f.Repository.OpenWriter())
        {
            Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
            Assert.False(writer.RemoveReadyReusablePayload(Assert.Single(writer.GetReadyReusablePayloads().Entries)));
            Assert.True(File.Exists(f.Repository.GetPayloadPath(id)));
        }
    }
    [Fact]
    public void ExactCleanupProtectsSelectedAndUnknownArtifactsAndDoesNotChangeAuthority()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); var draft = ReuseFixture.Ready(writer); var artifact = draft.StagingPath; draft.Dispose();
        var unknown = Path.Combine(Path.GetDirectoryName(artifact)!, $"{Guid.NewGuid():N}.part"); File.WriteAllText(unknown, "unknown");
        var entry = Assert.Single(writer.GetReadyReusablePayloads().Entries); var authority = f.Repository.ReadAuthority();
        using (var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time))
            Assert.False(writer.RemoveReadyReusablePayload(entry));
        Assert.True(writer.RemoveReadyReusablePayload(entry)); Assert.False(File.Exists(artifact)); Assert.True(File.Exists(unknown));
        Assert.Equal(authority, f.Repository.ReadAuthority()); Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
    }
    [Fact]
    public void MissingArtifactDescriptorCanBeRetiredWithoutHistoryMutation()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); var draft = ReuseFixture.Ready(writer); var artifact = draft.StagingPath; draft.Dispose();
        File.Delete(artifact); var entry = Assert.Single(writer.GetReadyReusablePayloads().Entries);
        Assert.False(writer.RemoveReadyReusablePayload(entry)); Assert.Empty(f.Descriptors);
    }
    [Fact]
    public void ExactBucketWorkDoesNotGrowWithUnrelatedEntries()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter();
        for (var i = 0; i < 64; i++) ReuseFixture.Ready(writer, path: new(FoundationTests.Root, $"other/{i}")).Dispose();
        ReuseFixture.Ready(writer).Dispose();
        using var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time);
        Assert.NotNull(candidate); Assert.Equal(1, writer.ReusableStatistics.BucketEntriesExamined); Assert.Equal(0, writer.ReusableStatistics.GlobalEntriesExamined);
    }
    [Fact]
    public void ObsoleteBoundBookkeepingCleanupNeverTouchesPublishedPayloadOrHistory()
    {
        using var f = new ReuseFixture(); string boundName; byte[] boundBytes; Guid id;
        using (var writer = f.Repository.OpenWriter())
        {
            using var draft = ReuseFixture.Ready(writer); id = draft.VersionId;
            using var publication = ReuseFixture.Prepare(f.Repository, writer, draft); draft.Install();
            boundName = Directory.GetFiles(f.Repository.DirectoryPath, "*.bound", SearchOption.AllDirectories).Single(); boundBytes = File.ReadAllBytes(boundName);
            publication.Commit();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(boundName)!); File.WriteAllBytes(boundName, boundBytes);
        var authority = f.Repository.ReadAuthority();
        using (var writer = f.Repository.OpenWriter()) Assert.Equal(1, writer.CleanupReusableBookkeeping());
        Assert.Equal(authority, f.Repository.ReadAuthority()); Assert.True(File.Exists(f.Repository.GetPayloadPath(id)));
        using var reader = f.Repository.OpenReader(); Assert.Equal(id, reader.GetCurrent(ReuseFixture.PathKey)!.Identity.VersionId);
    }
    [Fact]
    public void DeletingAllKnownCacheCannotChangeRebuildCompactionOrHistory()
    {
        using var f = new ReuseFixture(); StoredVersion original;
        using (var writer = f.Repository.OpenWriter())
        {
            using var draft = ReuseFixture.Ready(writer); using var publication = ReuseFixture.Prepare(f.Repository, writer, draft); draft.Install(); publication.Commit();
        }
        using (var reader = f.Repository.OpenReader()) original = reader.GetCurrent(ReuseFixture.PathKey)!;
        using (var writer = f.Repository.OpenWriter())
        {
            ReuseFixture.Ready(writer, path: new(FoundationTests.Root, "cache-only")).Dispose();
            var before = f.Repository.ReadAuthority(); Assert.True(writer.RemoveReadyReusablePayload(Assert.Single(writer.GetReadyReusablePayloads().Entries)));
            Assert.Equal(before, f.Repository.ReadAuthority()); writer.RebuildIndexes(); writer.CompactMetadata(); writer.RebuildIndexes();
        }
        using var fresh = f.Repository.OpenReader(); Assert.Equal(original, fresh.GetCurrent(ReuseFixture.PathKey));
        Assert.Equal(original, Assert.Single(fresh.EnumerateVersions(ReuseFixture.PathKey))); Assert.Null(fresh.GetNewestVersion(new(FoundationTests.Root, "cache-only")));
    }
    [Fact]
    public void RequiredAuthorityCorruptionIsNeverAnOptionalCacheMiss()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); ReuseFixture.Ready(writer).Dispose();
        var entry = Assert.Single(writer.GetReadyReusablePayloads().Entries);
        File.WriteAllText(Path.Combine(f.Repository.DirectoryPath, "state.json"), "broken");
        Assert.Throws<InvalidDataException>(() => writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
        Assert.Throws<InvalidDataException>(() => writer.RemoveReadyReusablePayload(entry));
    }
    [WindowsFact]
    public void UnsafeArtifactAndCacheTopologyNeverFollowLinksOrDeleteTargets()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter(); var draft = ReuseFixture.Ready(writer); var artifact = draft.StagingPath; draft.Dispose();
        var outside = f.Temp.Child("outside"); File.WriteAllText(outside, "hello");
        File.Delete(artifact); File.CreateSymbolicLink(artifact, outside);
        Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
        Assert.False(writer.RemoveReadyReusablePayload(Assert.Single(writer.GetReadyReusablePayloads().Entries)));
        Assert.Equal("hello", File.ReadAllText(outside)); File.Delete(artifact);
        var bucket = writer.ReusableBucket(ReuseFixture.PathKey); var moved = f.Temp.Child("cache-outside"); Directory.Move(bucket, moved);
        Directory.CreateSymbolicLink(bucket, moved);
        Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
        Assert.Single(Directory.GetFiles(moved)); Directory.Delete(bucket);
    }
    [Fact]
    public void NoProvenanceMeansNoReuseAndNoUnknownSweep()
    {
        using var f = new ReuseFixture(); using var writer = f.Repository.OpenWriter();
        var orphan = writer.CreatePayloadDraft(); orphan.Write("hello"u8); orphan.CompleteCopy(5);
        var path = orphan.StagingPath; var final = f.Repository.GetPayloadPath(Guid.NewGuid()); Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllText(final, "hello");
        Assert.Null(writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time)); Assert.Empty(writer.GetReadyReusablePayloads().Entries);
        Assert.True(File.Exists(path)); Assert.True(File.Exists(final)); orphan.Dispose();
        Assert.Equal(0, writer.CleanupReusableBookkeeping()); Assert.Equal(0, writer.ReusableStatistics.CleanupDeferred);
    }
    [Theory]
    [InlineData("reusable-a-partial")] [InlineData("reusable-a-ready")] [InlineData("reusable-a-revoked")]
    [InlineData("reusable-a-prepared")] [InlineData("reusable-a-installed")] [InlineData("reusable-a-published")]
    public async Task RealProcessDeathNeverLeavesReadyAuthorizationForPublishablePayload(string boundary)
    {
        using var f = new ReuseFixture();
        await using (var child = WriterProcess.Start(boundary, f.Repository.DirectoryPath)) { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        using var writer = f.Repository.OpenWriter(); var txs = writer.EnumeratePendingPublications().ToArray();
        if (boundary is "reusable-a-prepared" or "reusable-a-installed" or "reusable-a-published")
        {
            Assert.Empty(f.Descriptors);
            if (boundary == "reusable-a-published")
            { using var capture = f.Repository.OpenReader(); Assert.Equal(PublicationOutcome.Published, writer.Reconcile(capture.Authority.TransactionId)); }
            else
            {
                Assert.Single(txs); Assert.Throws<IOException>(() => writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time));
                Assert.Equal(PublicationOutcome.Unpublished, writer.Reconcile(txs[0]));
            }
        }
        else Assert.Empty(txs);
        using var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time);
        Assert.Equal(boundary is "reusable-a-ready" or "reusable-a-prepared" or "reusable-a-installed", candidate is not null);
        using var reader = f.Repository.OpenReader(); Assert.Equal(boundary == "reusable-a-published", reader.GetCurrent(ReuseFixture.PathKey) is not null);
    }
}
