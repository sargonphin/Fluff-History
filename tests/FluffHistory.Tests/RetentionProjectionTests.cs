using System.Buffers.Binary;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class RetentionProjectionTests
{
    [Fact]
    public void GroupedWindowsMatchIndependentFullVersionCurrentMergeAndRetainOneHandlePool()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        MetadataDiagnosticFixture.Populate(repo, 257, rotation: 65_536, storedTime: (pass, i) => FoundationTests.Time.AddDays(pass == 1 && i % 3 == 0 ? -9 : i % 7));
        RetentionVersion[] expected;
        using (var reader = repo.OpenReader()) expected = reader.VersionIndex.Range(reader.Captured.Root.Versions).Select(e =>
        { var v = reader.Version(e.Value, versionKey: e.Key); return new RetentionVersion(v.Identity, v.StoredUtc, v.Metadata.Length, reader.GetCurrent(v.Identity.Path)?.Identity == v.Identity); }).ToArray();
        foreach (var limit in new[] { 1, 3, 16_384 })
        {
            using var reader = repo.OpenComparisonReader(temp.Child("unused"));
            Assert.Equal(expected, reader.EnumerateRetentionVersions(new ComparisonReadBudget(limit)).ToArray());
            var stats = reader.LastRetentionStatistics!; Assert.Equal(514, stats.VersionsExamined); Assert.Equal(257, stats.CurrentEntriesExamined);
            Assert.Equal(257, stats.ProtectedVersions); Assert.Equal(772, stats.SemanticRecordsValidated); Assert.Equal(1, stats.SourceRecordsValidated);
            Assert.InRange(stats.PeakRetainedHandles, 1, 8); Assert.True(stats.SegmentOpens < 514 || limit == 1);
            Assert.Equal((514 + limit - 1) / limit, stats.VersionWindows); Assert.Equal((257 + limit - 1) / limit, stats.CurrentWindows);
            Assert.Equal(stats.VersionWindows + stats.CurrentWindows, stats.GroupedWindows); Assert.InRange(stats.MaximumBufferedEntries, 1, limit);
            Assert.InRange(stats.MaximumRetainedEntries, 1, 2 * limit); Assert.InRange(stats.MaximumRetainedKeyBytes, 1, 8 * 1024 * 1024);
            Assert.Null(reader.Workspace!.CreatedDirectory);
        }
    }

    [Fact]
    public void LongKeysForceReadAndCandidateByteBoundsBeforeCountBounds()
    {
        using var f = new HistoryFixture(); var prefix = string.Join('/', Enumerable.Repeat(new string('a', 200), 9));
        for (var i = 0; i < 4; i++) { f.Add(prefix + "/leaf" + i); f.Remove(prefix + "/leaf" + i); }
        using var reader = f.Repository.OpenReader();
        Assert.Equal(4, reader.EnumerateRetentionVersions(new ComparisonReadBudget(3, Format.MaximumKeyBytes)).Count());
        Assert.Equal(4, reader.LastRetentionStatistics!.GroupedWindows); Assert.Equal(1, reader.LastRetentionStatistics.MaximumBufferedEntries);
        var engine = new RetentionEngine(f.Repository, new(MaximumCandidateBytes: 8512)); var evaluation = new RetentionEvaluation(RetentionPolicy.OneMonth, RetentionTests.Evaluation);
        RetentionCursor? cursor = null; var identities = new List<VersionIdentity>();
        do { var page = engine.GetCandidates(reader, evaluation, cursor: cursor); Assert.InRange(page.Items.Count, 0, 1); Assert.InRange(page.ChargedCandidateBytes, 0, 8512); identities.AddRange(page.Items.Select(v => v.Identity)); cursor = page.Continuation; } while (cursor is not null);
        Assert.Equal(4, identities.Distinct().Count());
        var execution = engine.Execute(RetentionTests.Preview(f.Repository)); Assert.Equal(4, execution.Removed); Assert.Equal(4, execution.BatchesCommitted); Assert.Equal(1, execution.MaximumBatchCount);
    }

    [Fact]
    public void AllFfRootAndFrozenCasePathsDoNotRequireFiniteRootUpperBound()
    {
        using var f = new HistoryFixture(); var root = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        // The legacy writer's filtered-current root helper has a documented all-FF limitation.
        // Construct a valid format-3 fixture directly to exercise the new unbounded repository scan.
        var path = new HistoryPath(root, "É/\U00010428");
        var metadata = new LogicalFileMetadata(root, "é/\U00010400", 1, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive);
        var old = new StoredVersion(new(path, 1, 0, Guid.NewGuid()), metadata, FoundationTests.Time, new("D24EC4F1A98C6E5B"));
        var current = new StoredVersion(new(path, 1, 1, Guid.NewGuid()), metadata, FoundationTests.Time.AddDays(-2), old.Checksum);
        Corrupt(f.Repository, (reader, append) =>
        {
            var config = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(root, "missing"))));
            var a = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, config, Version: old)));
            var b = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, a, Version: current)));
            var upsert = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, b, Path: path, VersionAddress: b)));
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
            return reader.Captured.Root with { Sequence = 1, Journal = upsert,
                Current = new OrderedIndex(IndexKind.Current, Read, append).Update(null, [(PathKeys.Current(path), b)]),
                Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(null, [(PathKeys.Version(old.Identity), a), (PathKeys.Version(current.Identity), b)]),
                Sources = new OrderedIndex(IndexKind.Sources, Read, append).Update(null, [(PathKeys.Source(root), config)]) };
        });
        foreach (var version in new[] { old, current }) { var payload = f.Repository.GetPayloadPath(version.Identity.VersionId); Directory.CreateDirectory(Path.GetDirectoryName(payload)!); File.WriteAllBytes(payload, [42]); }
        var preview = RetentionTests.Preview(f.Repository); Assert.Equal(2, preview.Examined); Assert.Equal(1, preview.Protected); Assert.Equal(1, preview.Eligible);
        var result = new RetentionEngine(f.Repository).Execute(preview); Assert.Equal(1, result.Removed);
        using var reader = f.Repository.OpenReader(); Assert.Null(reader.GetVersion(old.Identity)); Assert.Equal(current, reader.GetCurrent(current.Identity.Path));
    }

    [Theory]
    [InlineData("unprotected")] [InlineData("size")] [InlineData("excluded")] [InlineData("missing-source")]
    [InlineData("missing-current-version")] [InlineData("missing-current-path")] [InlineData("current-path-binding")]
    public void InconsistentCommittedCurrentOrConfigurationFailsClosedWithoutCleanup(string defect)
    {
        using var f = new HistoryFixture(); var old = f.Add("dir/a"); var current = f.Add("dir/a", day: 1);
        Corrupt(f.Repository, (reader, append) =>
        {
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
            var root = reader.Captured.Root;
            if (defect.StartsWith("missing-current", StringComparison.Ordinal))
            {
                var changes = new List<(byte[], FrameAddress?)> { (PathKeys.Version(current.Identity), null) };
                if (defect == "missing-current-path") changes.Add((PathKeys.Version(old.Identity), null));
                return root with { Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(root.Versions, changes) };
            }
            if (defect == "current-path-binding")
                return root with { Current = new OrderedIndex(IndexKind.Current, Read, append).Update(null, [(PathKeys.Current(new(FoundationTests.Root, "wrong")), reader.CurrentIndex.Range(root.Current).Single().Value)]) };
            FrameAddress? source = null;
            if (defect != "missing-source") source = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null,
                Source: new(FoundationTests.Root, "missing", defect != "unprotected", defect == "size" ? 0 : long.MaxValue, defect == "excluded" ? ["dir"] : null))));
            return root with { Sources = new OrderedIndex(IndexKind.Sources, Read, append).Update(root.Sources, [(PathKeys.Source(FoundationTests.Root), source)]) };
        });
        var before = RetentionTests.Inventory(f.Repository);
        Assert.Throws<InvalidDataException>(() => RetentionTests.Preview(f.Repository));
        Assert.Equal(before, RetentionTests.Inventory(f.Repository)); Assert.True(File.Exists(f.Repository.GetPayloadPath(current.Identity.VersionId)));
    }

    [Theory]
    [InlineData("key-sequence")] [InlineData("key-ordinal")] [InlineData("stored-zero")] [InlineData("length")]
    [InlineData("previous-forward")] [InlineData("checksum")] [InlineData("trailing")]
    public void RetentionRejectsRehashedMalformedOrMisboundVersions(string defect)
    {
        using var f = new HistoryFixture(); var old = f.Add(); f.Add(day: 1);
        Corrupt(f.Repository, (reader, append) =>
        {
            var previous = defect == "previous-forward" ? reader.Captured.Root.Journal! with { Serial = reader.Captured.Address.Serial + 1000 } : null;
            var body = MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, previous, Version: old)); var key = PathKeys.Version(old.Identity);
            if (defect == "key-sequence") BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(key.Length - 12), 100);
            if (defect == "key-ordinal") BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(key.Length - 4), 1);
            if (defect == "stored-zero") BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(30), 0);
            if (defect == "length") BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(body.Length - 36), -1);
            if (defect == "checksum") body = body[..^1];
            if (defect == "trailing") body = [.. body, 42];
            var address = append.Append(FrameKind.Semantic, body);
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
            return reader.Captured.Root with { Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(null, [(key, address)]) };
        }, validateFrames: false);
        Assert.Throws<InvalidDataException>(() => RetentionTests.Preview(f.Repository));
    }

    [Fact]
    public void EligibleLogicalByteOverflowCannotProduceCompletePreviewOrMutateRepository()
    {
        using var f = new HistoryFixture();
        Corrupt(f.Repository, (reader, append) =>
        {
            var entries = new List<(byte[], FrameAddress?)>(); FrameAddress? journal = null;
            foreach (var name in new[] { "a", "b" })
            {
                var identity = new VersionIdentity(new(FoundationTests.Root, name), 1, entries.Count, Guid.NewGuid());
                var version = new StoredVersion(identity, new(FoundationTests.Root, name, long.MaxValue, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive), FoundationTests.Time, new("EF46DB3751D8E999"));
                journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, journal, Version: version))); entries.Add((PathKeys.Version(identity), journal));
            }
            byte[] Read(FrameAddress p) => append.ReadNew(p) ?? reader.Read(p);
            return reader.Captured.Root with { Sequence = 1, Journal = journal, Versions = new OrderedIndex(IndexKind.Versions, Read, append).Update(null, entries) };
        });
        var before = RetentionTests.Inventory(f.Repository); Assert.Throws<OverflowException>(() => RetentionTests.Preview(f.Repository)); Assert.Equal(before, RetentionTests.Inventory(f.Repository));
    }

    internal static void Corrupt(Repository repo, Func<RepositoryReader, SegmentAppender, MetadataRoot> change, bool validateFrames = true)
    {
        using var reader = repo.OpenReader(); using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, reader.Captured.Root.Epoch, reader.Captured);
        var root = change(reader, append) with { Generation = reader.Authority.Generation + 1, PreviousGeneration = reader.Authority.Generation,
            PreviousTransactionId = reader.Authority.TransactionId, PreviousRootHash = reader.Captured.Address.Sha256, TransactionId = Guid.NewGuid() };
        var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root));
        if (validateFrames) append.FlushAndValidate(); else append.Dispose();
        AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(new(repo.Identity, root, address))); AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
    }

    [Fact]
    public void CancellationDisposalAndRetirementCloseHandlesAndNeverRefreshCapture()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); MetadataDiagnosticFixture.Populate(repo, 257, rotation: 65_536);
        using (var reader = repo.OpenReader()) using (var cts = new CancellationTokenSource())
        {
            reader.RetentionWindowCompleted = cts.Cancel;
            Assert.Throws<OperationCanceledException>(() => reader.EnumerateRetentionVersions(new ComparisonReadBudget(2), cancellationToken: cts.Token).ToArray());
        }
        using (var reader = repo.OpenReader())
        {
            reader.RetentionWindowCompleted = reader.Dispose;
            Assert.Throws<ObjectDisposedException>(() => reader.EnumerateRetentionVersions(new ComparisonReadBudget(2)).ToArray());
        }
        using (var reader = repo.OpenReader())
        {
            bool retired = false;
            reader.RetentionWindowCompleted = () => { if (retired) return; retired = true; using var writer = repo.OpenWriter(); writer.CompactMetadata(); Assert.True(writer.CleanupRetiredMetadata().DeletedSegments > 0); };
            Assert.Throws<ReaderGenerationExpiredException>(() => reader.EnumerateRetentionVersions(new ComparisonReadBudget(2)).ToArray()); Assert.True(retired);
        }
        foreach (var path in Directory.GetFiles(Path.Combine(repo.DirectoryPath, "metadata"), "*.seg")) using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
}
