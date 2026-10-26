using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class FoundationTests
{
    internal static readonly Guid Root = Guid.Parse("e24d151a-7907-4b40-937a-8e0b911c628d");
    internal static readonly DateTimeOffset Time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static PreparedVersion Prepare(RepositoryWriter writer, string path = "foo.txt", Guid? root = null)
    {
        var p = writer.PrepareVersion(new LogicalFileMetadata(root ?? Root, path, 1, Time, Time, FileAttributes.Archive), Time, new("D24EC4F1A98C6E5B"));
        Directory.CreateDirectory(Path.GetDirectoryName(writer.GetPreparedPayloadPath(p))!);
        File.WriteAllBytes(writer.GetPreparedPayloadPath(p), [42]); return p;
    }
    internal static MutationResult Add(Repository repository, RepositoryWriter writer, string path = "foo.txt", Guid? root = null)
    {
        using var reader = repository.OpenReader(); var p = Prepare(writer, path, root);
        return writer.Apply(new([p], sourceRoots: [new(root ?? Root, @"C:\Source")]), reader.Authority);
    }
    [Fact]
    public void EmptyRepositoryHasOnlyEmptyAuthorityAndExplicitFormat()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        Assert.Equal(repo.Identity, Repository.Open(temp.Child()).Identity);
        Assert.Equal(4, RepositoryIdentity.FormatVersion); using var r = repo.OpenReader();
        Assert.Equal(0, r.Authority.MutationSequence); Assert.Empty(r.EnumerateCurrent()); Assert.Empty(r.EnumerateVersions(new(Root, "foo"))); Assert.Empty(r.EnumerateSourceRoots());
        Assert.False(Directory.Exists(Path.Combine(temp.Child(), "runs")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentAndEmptyDestinationsCanBeCreated(bool existing)
    { using var temp = new TemporaryDirectory(); if (existing) Directory.CreateDirectory(temp.Child()); Assert.NotNull(Repository.Create(temp.Child())); }
    [Theory]
    [InlineData("foreign")]
    [InlineData("writer.lock")]
    [InlineData("repository.json.tmp")]
    public void ForeignOrInterruptedDestinationIsNeverRepurposed(string name)
    {
        using var temp = new TemporaryDirectory(); Directory.CreateDirectory(temp.Child()); File.WriteAllText(Path.Combine(temp.Child(), name), "evidence");
        Assert.Null(Repository.Discover(temp.Child())); Assert.Throws<IOException>(() => Repository.Create(temp.Child())); Assert.Equal("evidence", File.ReadAllText(Path.Combine(temp.Child(), name)));
    }
    [Fact]
    public void CreationRefusesValidRepositoryAndOpeningNeverRepairs()
    {
        using var temp = new TemporaryDirectory(); Repository.Create(temp.Child()); Assert.Throws<IOException>(() => Repository.Create(temp.Child()));
        File.Delete(Path.Combine(temp.Child(), "state.json")); Assert.Throws<FileNotFoundException>(() => Repository.Open(temp.Child())); Assert.False(File.Exists(Path.Combine(temp.Child(), "state.json")));
    }
    [Fact]
    public void LockIsExclusiveReadersIndependentAndFilePersists()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var writer = repo.OpenWriter()) { Assert.Throws<IOException>(() => repo.OpenWriter()); using var read = repo.OpenReader(); Assert.Empty(read.EnumerateCurrent()); }
        using (var writer = repo.OpenWriter()) Assert.True(File.Exists(Path.Combine(temp.Child(), "writer.lock")));
    }
    [Fact]
    public void AddDeleteRecreateKeepsPathHistoryAndIndependentRoots()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); var path = new HistoryPath(Root, "foo.txt");
        using var writer = repo.OpenWriter(); var first = Add(repo, writer);
        using (var r = repo.OpenReader()) { Assert.Single(r.EnumerateVersions(path)); Assert.NotNull(r.GetCurrent(path)); Assert.InRange(r.Segments.Statistics.SemanticReads, 1, 3); }
        var deleted = writer.Apply(new(currentRemovals: [path]), first.Authority);
        using (var r = repo.OpenReader()) { Assert.Null(r.GetCurrent(path)); Assert.Single(r.EnumerateVersions(path)); }
        var second = writer.Apply(new([Prepare(writer, "FOO.txt")]), deleted.Authority);
        var other = Guid.NewGuid(); Add(repo, writer, "foo.txt", other);
        using var fresh = Repository.Open(temp.Child()).OpenReader(); var history = fresh.EnumerateVersions(path).ToArray();
        Assert.Equal(2, history.Length); Assert.Equal(new long[] { 1, 3 }, history.Select(v => v.Identity.Sequence));
        Assert.Equal(history[1], fresh.GetCurrent(path)); Assert.Equal(history[1], fresh.GetNewestVersion(path)); Assert.Single(fresh.EnumerateVersions(new(other, "foo.txt")));
        Assert.Equal(history.Reverse(), fresh.EnumerateVersions(path, true)); Assert.Equal(second.Authority.MutationSequence, history[1].Identity.Sequence);
    }
    [Fact]
    public void EmptyAndSemanticNoopsDoNotChangeAnyBytesOrModificationTimes()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child());
        using (var w = repo.OpenWriter()) Add(repo, w);
        var before = TemporaryDirectory.Snapshot(temp.Child()); var times = Directory.GetFiles(temp.Child(), "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        using (var writer = repo.OpenWriter()) using (var read = repo.OpenReader())
        {
            Assert.False(writer.Apply(new(), read.Authority).Changed);
            Assert.False(writer.Apply(new(currentUpserts: [read.GetCurrent(new(Root, "foo.txt"))!.Identity], currentRemovals: [new(Root, "absent")], sourceRoots: [new(Root, @"C:\Source")]), read.Authority).Changed);
            Assert.Null(writer.LastWriteStatistics);
        }
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child())); foreach (var t in times) Assert.Equal(t.Value, File.GetLastWriteTimeUtc(t.Key));
    }
    [Fact]
    public void PrefixBoundaryAndCaseAreExact()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var w = repo.OpenWriter(); using var r = repo.OpenReader();
        w.Apply(new([Prepare(w, "Foo"), Prepare(w, "Foo2/a"), Prepare(w, "Foo/Bar"), Prepare(w, "é/𐐨.txt")], sourceRoots: [new(Root, @"C:\Source")]), r.Authority);
        using var next = repo.OpenReader(); Assert.Single(next.EnumerateCurrent(Root, "foo")); Assert.NotNull(next.GetCurrent(new(Root, "É/𐐀.TXT"))); Assert.Equal(4, next.EnumerateCurrent().Count());
    }
    [Fact]
    public void PreparedVersionMustBelongToSessionAndHaveCorrectPayload()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); PreparedVersion p;
        using (var w = repo.OpenWriter()) p = Prepare(w);
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader()) Assert.Throws<ArgumentException>(() => w.Apply(new([p]), r.Authority));
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader())
        { p = Prepare(w); File.WriteAllBytes(w.GetPreparedPayloadPath(p), []); Assert.Throws<InvalidDataException>(() => w.Apply(new([p], sourceRoots: [new(Root, "Source")]), r.Authority)); }
    }
    [Fact]
    public void ConfigCannotSilentlyDropProtectedCurrentMembership()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using (var w = repo.OpenWriter()) Add(repo, w);
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader()) Assert.Throws<InvalidDataException>(() => w.Apply(new(sourceRoots: [new(Root, "Source", false)]), r.Authority));
        using (var w = repo.OpenWriter()) using (var r = repo.OpenReader()) w.Apply(new(currentRemovals: [new(Root, "foo.txt")], sourceRoots: [new(Root, "Source", false)]), r.Authority);
        using var result = repo.OpenReader(); Assert.Empty(result.EnumerateCurrent()); Assert.Single(result.EnumerateVersions(new(Root, "foo.txt")));
    }
    [Fact]
    public void RebuildAndCompactionPreserveExactSemanticsAndPayloadBytes()
    {
        using var temp = new TemporaryDirectory(); var repo = Repository.Create(temp.Child()); using var w = repo.OpenWriter();
        Add(repo, w); Add(repo, w); Add(repo, w, "gone.txt");
        using (var r = repo.OpenReader()) w.Apply(new(currentRemovals: [new(Root, "gone.txt")]), r.Authority);
        StoredVersion[] versions; StoredVersion[] current; AuthorityIdentity before;
        using (var r = repo.OpenReader()) { before = r.Authority; versions = r.EnumerateVersions(new(Root, "foo.txt")).ToArray(); current = r.EnumerateCurrent().ToArray(); }
        var payloads = TemporaryDirectory.Snapshot(Path.Combine(temp.Child(), "versions"));
        w.RebuildIndexes(); w.CompactMetadata(); w.RebuildIndexes();
        using (var r = repo.OpenReader()) { Assert.Equal(before.MutationSequence, r.Authority.MutationSequence); Assert.Equal(before.Generation + 3, r.Authority.Generation); Assert.Equal(versions, r.EnumerateVersions(new(Root, "foo.txt"))); Assert.Equal(current, r.EnumerateCurrent()); Assert.Single(r.EnumerateVersions(new(Root, "gone.txt"))); }
        Assert.Equal(payloads, TemporaryDirectory.Snapshot(Path.Combine(temp.Child(), "versions")));
        var unknown = Path.Combine(temp.Child(), "metadata", "unknown.seg"); File.WriteAllText(unknown, "keep"); Assert.True(w.CleanupRetiredMetadata().DeletedSegments > 0); Assert.Equal("keep", File.ReadAllText(unknown));
        using var fresh = repo.OpenReader(); Assert.Equal(versions, fresh.EnumerateVersions(new(Root, "foo.txt")));
    }
}
