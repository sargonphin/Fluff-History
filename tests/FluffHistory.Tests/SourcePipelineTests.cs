using System.Text;
using System.Runtime.InteropServices;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class SourcePipelineTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly TemporaryDirectory Temp = new();
        internal Repository Repository { get; }
        internal string Source => Temp.Child("source");
        internal SourceSelection Selection { get; private set; }
        internal Fixture(bool encrypted = false)
        {
            Repository = encrypted ? Repository.CreateEncrypted(Temp.Child(), "synthetic-unit-recovery") : Repository.Create(Temp.Child()); Directory.CreateDirectory(Source);
            Selection = new(new(FoundationTests.Root, Source), Source);
        }
        internal void Configure(bool protect = true, long maximum = long.MaxValue, string[]? exclusions = null) =>
            Selection = new(new(FoundationTests.Root, Source, protect, maximum, exclusions), Source);
        internal string File(string name, string content = "hello")
        {
            var path = Path.Combine(Source, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content, new UTF8Encoding(false)); System.IO.File.SetLastWriteTimeUtc(path, FoundationTests.Time.UtcDateTime); return path;
        }
        internal ScanResult Scan(ScanOptions? options = null, SourceAccess? access = null, CancellationToken token = default) =>
            new FileHistoryEngine(Repository, options ?? new(), access ?? new()).Scan([Selection], token);
        internal StoredVersion[] Current() { using var r = Repository.OpenReader(); return r.EnumerateCurrent().ToArray(); }
        internal StoredVersion[] History(string name) { using var r = Repository.OpenReader(); return r.EnumerateVersions(new(FoundationTests.Root, name)).ToArray(); }
        public void Dispose() => Temp.Dispose();
    }

    private sealed class Faults : SourceAccess
    {
        internal Func<string, SourceObservation, SourceObservation>? Metadata;
        internal Action<string>? BeforeObserve, BeforeOpen;
        internal Action<string>? AfterEnumeration;
        internal Action<string, int>? BeforeRead;
        internal string? FailedDirectory;
        internal bool PartialEnumeration;
        internal long Opened, ReadBytes;
        private readonly Dictionary<Stream, (string Path, int Reads)> _streams = [];
        internal override SourceObservation Observe(string path) { BeforeObserve?.Invoke(path); var v = base.Observe(path); return Metadata?.Invoke(path, v) ?? v; }
        internal override Stream OpenRead(string path) { BeforeOpen?.Invoke(path); var s = base.OpenRead(path); _streams[s] = (path, 0); Opened++; return s; }
        internal override int Read(Stream stream, Span<byte> buffer)
        {
            var entry = _streams[stream]; _streams[stream] = (entry.Path, entry.Reads + 1); BeforeRead?.Invoke(entry.Path, entry.Reads);
            var n = base.Read(stream, buffer); ReadBytes += n; return n;
        }
        internal override IEnumerable<string> EnumerateNames(string path)
        {
            if (path == FailedDirectory && !PartialEnumeration) throw new UnauthorizedAccessException("Injected inaccessible scope.");
            foreach (var name in base.EnumerateNames(path))
            { yield return name; if (path == FailedDirectory) throw new IOException("Injected partial enumeration."); }
            AfterEnumeration?.Invoke(path);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InitialNestedCopyKnownChecksumsAndUnchangedSecondScanWritesNothing(bool encrypted)
    {
        using var f = new Fixture(encrypted); f.File("hello.txt"); f.File("nested/empty", ""); Directory.CreateDirectory(Path.Combine(f.Source, "empty-directory"));
        var sourceBefore = TemporaryDirectory.Snapshot(f.Source); var access = new Faults(); var first = f.Scan(access: access);
        Assert.Equal(2, first.VersionsCreated); Assert.Equal(2, access.Opened); Assert.Equal(5, access.ReadBytes);
        Assert.Equal(5, first.SourceBytesRead); Assert.Equal(5, first.PayloadBytesWritten);
        Assert.Equal(sourceBefore, TemporaryDirectory.Snapshot(f.Source));
        Assert.Equal("26C7827D889F6DA3", Assert.Single(f.History("hello.txt")).Checksum.Value);
        Assert.Equal("EF46DB3751D8E999", Assert.Single(f.History("nested/empty")).Checksum.Value);
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        var times = Directory.GetFiles(f.Repository.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, System.IO.File.GetLastWriteTimeUtc);
        var noOpen = new Faults { BeforeOpen = _ => throw new InvalidOperationException("Unchanged file content opened.") };
        var second = f.Scan(access: noOpen);
        Assert.Equal(first.Authority, second.Authority); Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, second.ContentStreamsOpened); Assert.Equal(0, second.BatchesCommitted); Assert.Equal(0, second.PayloadBytesWritten);
        Assert.Equal(0, second.HistoricalLookups);
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        foreach (var p in times) Assert.Equal(p.Value, System.IO.File.GetLastWriteTimeUtc(p.Key));
    }

    [Theory]
    [InlineData("length", true)]
    [InlineData("mtime", true)]
    [InlineData("creation", false)]
    [InlineData("attributes", false)]
    public void ComparisonUsesOnlyLengthAndLastWrite(string change, bool changed)
    {
        using var f = new Fixture(); var path = f.File("a"); f.Scan();
        switch (change)
        {
            case "length": System.IO.File.AppendAllText(path, "more"); break;
            case "mtime": System.IO.File.SetLastWriteTimeUtc(path, FoundationTests.Time.AddDays(1).UtcDateTime); break;
            case "creation": if (OperatingSystem.IsWindows()) System.IO.File.SetCreationTimeUtc(path, FoundationTests.Time.AddDays(2).UtcDateTime); break;
            case "attributes": System.IO.File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive); break;
        }
        var result = f.Scan(); Assert.Equal(changed ? 1 : 0, result.VersionsCreated); Assert.Equal(changed ? 2 : 1, f.History("a").Length);
    }

    [Fact]
    public void DeleteRecreateReactivatesExactIdentityAndDifferentMetadataCreatesVersion()
    {
        using var f = new Fixture(); var path = f.File("a"); f.Scan(); var old = Assert.Single(f.Current());
        System.IO.File.Delete(path); Assert.Equal(1, f.Scan().Removals); Assert.Empty(f.Current()); Assert.Single(f.History("a"));
        f.File("a"); var reactivated = f.Scan(); Assert.Equal(1, reactivated.Reactivations); Assert.Equal(0, reactivated.PayloadBytesWritten);
        Assert.Equal(old.Identity, Assert.Single(f.Current()).Identity);
        System.IO.File.Delete(path); f.Scan(); f.File("a", "different"); Assert.Equal(1, f.Scan().VersionsCreated); Assert.Equal(2, f.History("a").Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileAndDirectoryRenameDoNotMoveHistory(bool directory)
    {
        using var f = new Fixture(); var old = directory ? "old/a" : "old"; var next = directory ? "new/a" : "new";
        f.File(old); f.Scan();
        if (directory) Directory.Move(Path.Combine(f.Source, "old"), Path.Combine(f.Source, "new"));
        else System.IO.File.Move(Path.Combine(f.Source, old), Path.Combine(f.Source, next));
        var result = f.Scan(); Assert.Equal(1, result.Removals); Assert.Equal(1, result.VersionsCreated);
        Assert.Single(f.History(old)); Assert.Single(f.History(next)); Assert.Equal(new HistoryPath(FoundationTests.Root, next), Assert.Single(f.Current()).Identity.Path);
    }

    [Fact]
    public void CaseOnlyRenameIsUnchanged()
    {
        using var f = new Fixture(); var old = f.File("lower"); f.Scan();
        System.IO.File.Move(old, old + ".tmp"); System.IO.File.Move(old + ".tmp", Path.Combine(f.Source, "LOWER"));
        var result = f.Scan(); Assert.Equal(1, result.Unchanged); Assert.Equal(0, result.BatchesCommitted); Assert.Single(f.History("lower"));
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("inaccessible-root")]
    [InlineData("subtree")]
    [InlineData("partial")]
    [InlineData("metadata")]
    [InlineData("read")]
    [InlineData("open")]
    [InlineData("reparse")]
    [InlineData("efs")]
    [InlineData("oversize")]
    public void FailedAndUnsupportedObservationsNeverProveDeletion(string failure)
    {
        using var f = new Fixture(); var protectedFile = f.File("folder/a"); f.File("absent"); f.Scan();
        System.IO.File.Delete(Path.Combine(f.Source, "absent")); var access = new Faults();
        switch (failure)
        {
            case "missing-root": Directory.Move(f.Source, f.Source + "-moved"); break;
            case "inaccessible-root": access.FailedDirectory = f.Source; break;
            case "partial": access.FailedDirectory = f.Source; access.PartialEnumeration = true; break;
            case "subtree": System.IO.File.Delete(protectedFile); access.FailedDirectory = Path.GetDirectoryName(protectedFile); break;
            case "metadata": access.BeforeObserve = p => { if (p == protectedFile) throw new IOException("metadata failed"); }; break;
            case "read": System.IO.File.SetLastWriteTimeUtc(protectedFile, FoundationTests.Time.AddDays(1).UtcDateTime); access.BeforeRead = (_, _) => throw new IOException("read failed"); break;
            case "open": System.IO.File.SetLastWriteTimeUtc(protectedFile, FoundationTests.Time.AddDays(1).UtcDateTime); access.BeforeOpen = _ => throw new IOException("sharing denied"); break;
            case "reparse": access.Metadata = (p, v) => p == Path.GetDirectoryName(protectedFile) ? v with { Attributes = v.Attributes | FileAttributes.ReparsePoint } : v; break;
            case "efs": access.Metadata = (p, v) => p == protectedFile ? v with { Attributes = v.Attributes | FileAttributes.Encrypted } : v; break;
            case "oversize": f.Configure(maximum: 5); f.Scan(); System.IO.File.AppendAllText(protectedFile, "grown"); break;
        }
        var result = f.Scan(access: access);
        Assert.Contains(f.Current(), v => v.Identity.Path == new HistoryPath(FoundationTests.Root, "folder/a"));
        Assert.Single(f.History("folder/a"));
        if (failure is "missing-root" or "inaccessible-root" or "partial") Assert.Equal(0, result.Removals);
        else Assert.DoesNotContain(f.Current(), v => v.Identity.Path == new HistoryPath(FoundationTests.Root, "absent"));
    }

    [Fact]
    public void UnstableSourceRetriesReobserveAndExhaustionKeepsBaselineWhileOtherFileCommits()
    {
        using var f = new Fixture(); var unstable = f.File("unstable"); f.Scan(); f.File("good");
        System.IO.File.SetLastWriteTimeUtc(unstable, FoundationTests.Time.AddDays(1).UtcDateTime);
        var access = new Faults { BeforeRead = (p, n) => { if (p == unstable && n == 0) System.IO.File.AppendAllText(p, "growth"); } };
        var result = f.Scan(access: access); Assert.Equal(1, result.SourceSkips); Assert.Equal(1, result.RetryAttempts);
        Assert.Equal(1, result.VersionsCreated); Assert.Single(f.History("unstable")); Assert.Single(f.History("good"));
    }

    [Fact]
    public void RetryCanSucceedAfterFreshObservation()
    {
        using var f = new Fixture(); var path = f.File("a"); var fail = true;
        var access = new Faults { BeforeRead = (p, n) => { if (fail) { fail = false; System.IO.File.AppendAllText(p, "more"); } } };
        var result = f.Scan(access: access); Assert.Equal(1, result.RetryAttempts); Assert.Equal(1, result.VersionsCreated); Assert.Equal(9, Assert.Single(f.Current()).Metadata.Length);
    }

    [Fact]
    public void ExplicitProtectionChangesRemoveInBoundedStepsBeforeRestrictiveConfig()
    {
        using var f = new Fixture(); for (var i = 0; i < 7; i++) f.File($"folder/{i}"); f.File("keep"); f.Scan();
        f.Configure(exclusions: ["folder"]); var result = f.Scan(new(BatchSize: 2));
        Assert.Equal(7, result.Removals); Assert.Equal(5, result.BatchesCommitted); Assert.Single(f.Current());
        Assert.Single(f.History("folder/0"));
        f.Configure(protect: false); Assert.Equal(1, f.Scan().Removals); Assert.Empty(f.Current());
        f.Configure(); Assert.Equal(8, f.Scan().Reactivations);
    }

    [Fact]
    public void CancellationAndBudgetFailureCannotDeletePartiallyCoveredRoot()
    {
        using var f = new Fixture(); f.File("old"); f.Scan(); System.IO.File.Delete(Path.Combine(f.Source, "old"));
        for (var i = 0; i < 7; i++) f.File($"new{i}");
        using var cts = new CancellationTokenSource();
        var engine = new FileHistoryEngine(f.Repository, new(BatchSize: 2));
        engine.BatchCommitted = r => { if (r.VersionsCreated == 2) cts.Cancel(); };
        var result = engine.Scan([f.Selection], cts.Token);
        Assert.True(result.Cancelled); Assert.Equal(2, result.VersionsCreated); Assert.Equal(0, result.Removals);
        Assert.Contains(f.Current(), v => v.Identity.Path == new HistoryPath(FoundationTests.Root, "old"));
        Assert.Throws<ScanLimitExceededException>(() => f.Scan(new(MaximumStateEntries: 4)));
        Assert.Contains(f.Current(), v => v.Identity.Path == new HistoryPath(FoundationTests.Root, "old"));
    }

    [Fact]
    public void CancellationBeforeFirstCommitCleansOwnedCopiesAndCreatesNoVersion()
    {
        using var f = new Fixture(); f.Scan(); f.File("a"); using var cts = new CancellationTokenSource();
        var access = new Faults { BeforeRead = (_, _) => cts.Cancel() };
        var result = f.Scan(access: access, token: cts.Token);
        Assert.True(result.Cancelled); Assert.Equal(0, result.VersionsCreated); Assert.Empty(f.Current());
        Assert.False(Directory.Exists(Path.Combine(f.Repository.DirectoryPath, "staging")));
    }

    [Fact]
    public void ProtectionTimestampIsSharedAcrossBatchesAndLateCancellationKeepsCommit()
    {
        using var f = new Fixture(); f.Scan(); for (var i = 0; i < 5; i++) f.File($"a{i}");
        var clock = FoundationTests.Time.AddYears(1); var engine = new FileHistoryEngine(f.Repository, new(BatchSize: 2));
        engine.Clock = () => { var now = clock; clock = clock.AddHours(1); return now; };
        var result = engine.Scan([f.Selection]); Assert.Equal(3, result.BatchesCommitted);
        Assert.Single(f.Current().Select(v => v.StoredUtc).Distinct());
        Assert.All(f.Current(), v => Assert.True(v.StoredUtc > v.Metadata.LastWriteTimeUtc));
        f.File("later"); using var cts = new CancellationTokenSource();
        engine.ConfigureWriter = w => w.Observe = stage => { if (stage == PublicationStage.AfterReplace) cts.Cancel(); };
        var late = engine.Scan([f.Selection], cts.Token); Assert.Equal(1, late.VersionsCreated); Assert.Single(f.History("later")); Assert.True(late.Cancelled);
    }

    [Theory]
    [InlineData("flush")]
    [InlineData("write")]
    [InlineData("install")]
    [InlineData("metadata")]
    public void DestinationFailuresAbortAndNeverBecomeSourceSkips(string failure)
    {
        using var f = new Fixture(); f.Scan(); f.File("a");
        var engine = new FileHistoryEngine(f.Repository);
        engine.ConfigureWriter = w =>
        {
            if (failure == "metadata") w.Observe = s => { if (s == PublicationStage.CandidateFlushed) throw new IOException("metadata destination failed"); };
            else w.PayloadObserve = (s, _) => { if (s == (failure == "flush" ? PayloadStage.BeforeFlush : failure == "write" ? PayloadStage.BeforeWrite : PayloadStage.BeforeInstall)) throw new IOException("payload destination failed"); };
        };
        Assert.Throws<IOException>(() => engine.Scan([f.Selection])); Assert.Empty(f.Current());
    }

    [Fact]
    public void PhysicalRootOverlapAndRepositoryNestingFailBeforeMutation()
    {
        using var f = new Fixture(); var engine = new FileHistoryEngine(f.Repository); var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        var other = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => engine.Scan([f.Selection, new(new(other, "child"), Path.Combine(f.Source, "child"))]));
        Assert.Throws<ArgumentException>(() => engine.Scan([new(new(other, "repo"), f.Repository.DirectoryPath)]));
        Assert.Throws<ArgumentException>(() => engine.Scan([new(new(other, "parent"), f.Temp.Root)]));
        Assert.Throws<ArgumentException>(() => engine.Scan([new(new(other, "volume"), Path.GetPathRoot(f.Repository.DirectoryPath)!)]));
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
    }

    [Fact]
    public void MultipleRootsWithSameRelativePathStayIndependentAndFailureDoesNotUndoProgress()
    {
        using var f = new Fixture(); f.File("a"); var otherPath = f.Temp.Child("other"); Directory.CreateDirectory(otherPath);
        System.IO.File.WriteAllText(Path.Combine(otherPath, "a"), "second"); var other = Guid.NewGuid();
        var second = new SourceSelection(new(other, otherPath), otherPath); var engine = new FileHistoryEngine(f.Repository);
        Assert.Equal(2, engine.Scan([f.Selection, second]).VersionsCreated);
        using var reader = f.Repository.OpenReader(); Assert.Equal(2, reader.EnumerateCurrent().Count());
        f.File("b"); Directory.Move(otherPath, otherPath + "-missing");
        var next = engine.Scan([f.Selection, second]); Assert.Equal(1, next.VersionsCreated); Assert.Equal(0, next.Removals);
    }

    [Fact]
    public void EqualChangedFilesGetIndependentPayloadsAndPermissiveSourceSharing()
    {
        using var f = new Fixture(); f.File("a"); f.File("b");
        var access = new Faults { BeforeRead = (p, _) => { using var compatible = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); } };
        var result = f.Scan(access: access); var current = f.Current();
        Assert.Equal(2, result.VersionsCreated); Assert.Equal(10, result.SourceBytesRead); Assert.Equal(10, result.PayloadBytesWritten);
        Assert.Equal(2, current.Select(v => v.Identity.VersionId).Distinct().Count());
        Assert.Single(current.Select(v => v.Checksum).Distinct());
    }

    [Fact]
    public void MetadataFailureOnDirectoryProtectsEveryPriorDescendant()
    {
        using var f = new Fixture(); var file = f.File("folder/deep/a"); f.Scan(); System.IO.File.Delete(file);
        var access = new Faults { BeforeObserve = p => { if (p == Path.Combine(f.Source, "folder")) throw new UnauthorizedAccessException("directory metadata"); } };
        var result = f.Scan(access: access); Assert.Equal(0, result.Removals); Assert.Single(f.Current());
    }

    [Fact]
    public void ConfirmedAbsentDirectoryRemovesDescendantsAndExclusionIsNotDeletionEvidence()
    {
        using var f = new Fixture(); f.File("folder/a"); f.File("folder/deep/b"); f.Scan();
        Directory.Delete(Path.Combine(f.Source, "folder"), true);
        var result = f.Scan(); Assert.Equal(2, result.Removals); Assert.Empty(f.Current()); Assert.Single(f.History("folder/deep/b"));
    }

    [Fact]
    public void CancellationDuringRestrictiveConfigurationKeepsOldPermissiveConfigurationUntilRemovalsFinish()
    {
        using var f = new Fixture(); for (var i = 0; i < 7; i++) f.File($"folder/{i}"); f.Scan(); f.Configure(protect: false);
        using var cts = new CancellationTokenSource(); var engine = new FileHistoryEngine(f.Repository, new(BatchSize: 2));
        engine.BatchCommitted = _ => cts.Cancel();
        var result = engine.Scan([f.Selection], cts.Token); Assert.True(result.Cancelled); Assert.Equal(2, result.Removals);
        using (var r = f.Repository.OpenReader()) { Assert.True(r.GetSourceRoot(FoundationTests.Root)!.IsProtected); Assert.Equal(5, r.EnumerateCurrent().Count()); }
        Assert.Equal(5, f.Scan(new(BatchSize: 2)).Removals); Assert.Empty(f.Current());
    }

    [Fact]
    public void RepositoryCorruptionAndLostOwnershipAreNeverSourceSkips()
    {
        using var f = new Fixture(); f.Scan(); f.File("a"); var engine = new FileHistoryEngine(f.Repository);
        engine.ConfigureWriter = w => w.PayloadObserve = (stage, _) => { if (stage == PayloadStage.BeforeFlush) w.LoseHandleForTest(); };
        Assert.ThrowsAny<Exception>(() => engine.Scan([f.Selection])); Assert.Empty(f.Current());
        System.IO.File.WriteAllText(Path.Combine(f.Repository.DirectoryPath, "state.json"), "corrupt");
        Assert.ThrowsAny<Exception>(() => f.Scan());
    }

    [Fact]
    public void RootWithoutFilesCanPublishConfigurationButEmptyDirectoryHasNoVersion()
    {
        using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Source, "empty"));
        var first = f.Scan(); Assert.Equal(1, first.BatchesCommitted); Assert.Equal(0, first.VersionsCreated); Assert.Empty(f.Current());
        Assert.Equal(0, f.Scan().BatchesCommitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryFileDirectoryReplacementEndsOnlyProvenOldMembership(bool wasDirectory)
    {
        using var f = new Fixture(); f.File(wasDirectory ? "item/a" : "item"); f.Scan();
        if (wasDirectory) { Directory.Delete(Path.Combine(f.Source, "item"), true); f.File("item"); }
        else { System.IO.File.Delete(Path.Combine(f.Source, "item")); f.File("item/a"); }
        var result = f.Scan(); Assert.Equal(1, result.Removals); Assert.Equal(1, result.VersionsCreated); Assert.Single(f.Current());
    }

    [Fact]
    public void ExplicitMaximumSizeChangeEndsProtectionAndLaterWideningReactivates()
    {
        using var f = new Fixture(); f.File("large"); f.File("empty", ""); f.Scan(); f.Configure(maximum: 0);
        Assert.Equal(1, f.Scan().Removals); Assert.Single(f.Current()); Assert.Single(f.History("large"));
        f.Configure(); var result = f.Scan(); Assert.Equal(1, result.Reactivations); Assert.Equal(0, result.ContentStreamsOpened);
    }

    [Fact]
    public void PathSubstitutionDuringCopyIsRejectedBeforeFreshRetryPublishesReplacement()
    {
        using var f = new Fixture(); var file = f.File("a"); var replacement = f.Temp.Child("replacement");
        System.IO.File.WriteAllText(replacement, "replacement"); System.IO.File.SetLastWriteTimeUtc(replacement, FoundationTests.Time.AddDays(1).UtcDateTime);
        var once = true; var access = new Faults
        {
            BeforeRead = (p, _) =>
        {
            if (!once) return;
            // Windows applications use ReplaceFile for an open, delete-shared destination.
            if (OperatingSystem.IsWindows()) System.IO.File.Replace(replacement, p, null);
            else System.IO.File.Move(replacement, p, true);
            once = false;
        }
        };
        var result = f.Scan(access: access); Assert.Equal(1, result.RetryAttempts); Assert.Equal(1, result.VersionsCreated);
        Assert.Equal(11, Assert.Single(f.Current()).Metadata.Length); Assert.Single(f.History("a"));
    }

    [Fact]
    public void RootDisappearingAtEnumerationEndDoesNotTurnIntoMassDeletion()
    {
        using var f = new Fixture(); var file = f.File("a"); f.Scan(); System.IO.File.Delete(file);
        var access = new Faults { AfterEnumeration = p => { if (p == f.Source) Directory.Move(p, p + "-moved"); } };
        var result = f.Scan(access: access); Assert.Equal(0, result.Removals); Assert.Single(f.Current());
    }

    [WindowsFact]
    public void ActualWindowsSharingDenialSkipsFileAndReparseDirectoryIsNotFollowed()
    {
        using var f = new Fixture(); var path = f.File("a"); f.Scan(); System.IO.File.SetLastWriteTimeUtc(path, FoundationTests.Time.AddDays(1).UtcDateTime);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { var result = f.Scan(); Assert.Equal(1, result.SourceSkips); Assert.Single(f.History("a")); }
        var target = f.Temp.Child("target"); Directory.CreateDirectory(target); System.IO.File.WriteAllText(Path.Combine(target, "secret"), "never copy");
        Directory.CreateSymbolicLink(Path.Combine(f.Source, "link"), target);
        f.Scan(); Assert.DoesNotContain(f.Current(), v => v.Metadata.RelativePath.StartsWith("link/", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void ExtendedWindowsRootSpellingsCannotBypassOverlapAndLongNamesCanBeCopied()
    {
        using var f = new Fixture(); var engine = new FileHistoryEngine(f.Repository); var id = Guid.NewGuid();
        var alias = @"\\?\" + f.Repository.DirectoryPath;
        Assert.Throws<ArgumentException>(() => engine.Scan([new(new(id, alias), alias)]));
        var sourceAlias = @"\\?\" + f.Source;
        Assert.Throws<ArgumentException>(() => engine.Scan([f.Selection, new(new(id, sourceAlias), sourceAlias)]));
        Assert.Throws<ArgumentException>(() => engine.Scan([new(new(id, "ambiguous"), f.Repository.DirectoryPath + ". ")]));
        var name = string.Join('/', Enumerable.Repeat(new string('a', 90), 4)) + "/file";
        f.File(name); Assert.Equal(1, f.Scan().VersionsCreated); Assert.Single(f.History(name));
    }

    [LinuxFact]
    public void UnixPipeDoesNotBlockOrBecomeAnOrdinarySuccessfulVersion()
    {
        using var f = new Fixture();
        Assert.Equal(0, MakePipe(Path.Combine(f.Source, "pipe"), 384));
        var result = f.Scan(); Assert.Equal(1, result.SourceSkips); Assert.Equal(0, result.VersionsCreated); Assert.Empty(f.Current());
    }
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakePipe(string path, uint mode);
}
