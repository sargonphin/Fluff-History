using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class RestoreTests
{
    [Fact]
    public void ScopeInPlaceAndCurrentIdentityRestoresAlwaysUseRestorationTimeWithVerifiedFallback()
    {
        using var f = new HistoryFixture(); var a = f.Add("Folder/a", "a"); f.Add("Folder/b", "b");
        var source = f.Temp.Child("source"); Directory.CreateDirectory(source); var mapping = new SourceSelection(new(HistoryFixture.Root, "ignored"), source);
        var engine = new RestoreEngine(f.Repository); var now = DateTimeOffset.UtcNow;
        engine.RestoreTime = () => now;
        var result = engine.RestoreScopeInPlace(new(HistoryFixture.Root), mapping);
        Assert.Equal(2, result.Restored);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) Assert.Equal(now.UtcDateTime, File.GetLastWriteTimeUtc(file));
        // Even the exact current identity must become a new live modification when restore time rounds to its baseline.
        engine.RestoreTime = () => a.Metadata.LastWriteTimeUtc;
        result = engine.RestoreFileInPlace(a.Identity, mapping, ExistingTargetPolicy.OverwriteFile);
        Assert.Equal(1, result.Restored); Assert.Equal(1, result.MtimeAdjustedForChangeDetection);
        Assert.NotEqual(a.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(source, "Folder", "a")));
    }
    [Theory]
    [InlineData("")] [InlineData("hello")]
    public void ExactRestoreStreamsOnceCreatesParentsAndLeavesRepositoryUnchangedWithoutWriter(string content)
    {
        using var f = new HistoryFixture(); var old = f.Add("Mixed/Old.txt", content); f.Add("Mixed/Old.txt", "newer", 1);
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        var destination = f.Temp.Child("output/deep/file.txt"); using var writer = f.Repository.OpenWriter();
        var result = new RestoreEngine(f.Repository).RestoreFile(old.Identity, destination);
        Assert.Equal(1, result.Restored); Assert.Equal(0, result.Failed); Assert.Equal(content, File.ReadAllText(destination));
        Assert.Equal(old.Metadata.Length, result.BytesRead); Assert.Equal(result.BytesRead, result.BytesWritten); Assert.Equal(1, result.ChecksumsVerified);
        Assert.Equal(old.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(destination));
        writer.Dispose(); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(destination)!));
    }

    [Theory]
    [InlineData(ExistingTargetPolicy.FailIfExists, 0, 1, 0)]
    [InlineData(ExistingTargetPolicy.SkipExisting, 0, 0, 1)]
    [InlineData(ExistingTargetPolicy.OverwriteFile, 1, 0, 0)]
    public void ExistingFilePolicyIsExplicit(ExistingTargetPolicy policy, int restored, int failed, int skipped)
    {
        using var f = new HistoryFixture(); var v = f.Add(); var destination = f.Temp.Child("target"); File.WriteAllText(destination, "keep");
        var result = new RestoreEngine(f.Repository).RestoreFile(v.Identity, destination, policy);
        Assert.Equal(restored, result.Restored); Assert.Equal(failed, result.Failed); Assert.Equal(skipped, result.Skipped);
        Assert.Equal(restored == 1 ? "hello" : "keep", File.ReadAllText(destination));
    }

    [Theory]
    [InlineData("checksum")] [InlineData("short")] [InlineData("missing")] [InlineData("directory")]
    public void CorruptOrMissingPayloadNeverReplacesValidDestination(string failure)
    {
        using var f = new HistoryFixture(); var version = f.Add(); var payload = f.Repository.GetPayloadPath(version.Identity.VersionId);
        switch (failure)
        {
            case "checksum": File.WriteAllText(payload, "wrong"); break;
            case "short": File.WriteAllText(payload, "x"); break;
            case "missing": File.Delete(payload); break;
            case "directory": File.Delete(payload); Directory.CreateDirectory(payload); break;
        }
        var target = f.Temp.Child("target"); File.WriteAllText(target, "preserve");
        var result = new RestoreEngine(f.Repository).RestoreFile(version.Identity, target, ExistingTargetPolicy.OverwriteFile);
        Assert.Equal(1, result.Failed); Assert.Equal(0, result.Restored); Assert.Equal("preserve", File.ReadAllText(target));
        Assert.Equal(failure == "checksum" ? 1 : 0, result.ChecksumFailures);
        Assert.Empty(Directory.GetFiles(f.Temp.Root, ".fluff-restore-*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void DirectoryAndParentFileConflictsNeverReplaceTrees(bool directoryAtTarget)
    {
        using var f = new HistoryFixture(); var v = f.Add(); var target = f.Temp.Child("destination");
        if (directoryAtTarget) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "unrelated"), "keep"); }
        else { File.WriteAllText(target, "parent-file"); target = Path.Combine(target, "child"); }
        var before = TemporaryDirectory.Snapshot(f.Temp.Root);
        var result = new RestoreEngine(f.Repository).RestoreFile(v.Identity, target, ExistingTargetPolicy.OverwriteFile);
        Assert.Equal(1, result.Failed); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Temp.Root));
    }

    [Fact]
    public void DestinationNestingAndWindowsAliasSpellingsAreRejectedBeforeWrites()
    {
        using var f = new HistoryFixture(); var v = f.Add(); var restore = new RestoreEngine(f.Repository);
        Assert.Throws<ArgumentException>(() => restore.RestoreFile(v.Identity, Path.Combine(f.Repository.DirectoryPath, "bad")));
        Assert.Throws<ArgumentException>(() => restore.RestoreScope(new(HistoryFixture.Root), f.Temp.Root));
        if (OperatingSystem.IsWindows()) Assert.Throws<ArgumentException>(() => restore.RestoreScope(new(HistoryFixture.Root), @"\\?\" + f.Repository.DirectoryPath));
    }

    [Fact]
    public void ForeignTemporaryCollisionIsPreservedAndOwnedTemporaryIsCleanedAfterMetadataFailure()
    {
        using var f = new HistoryFixture(); var v = f.Add(); var target = f.Temp.Child("target"); var id = Guid.NewGuid();
        var foreign = f.Temp.Child($".fluff-restore-{id:N}.tmp"); File.WriteAllText(foreign, "foreign");
        var engine = new RestoreEngine(f.Repository) { TemporaryIdentity = () => id };
        Assert.Equal(1, engine.RestoreFile(v.Identity, target).Failed); Assert.Equal("foreign", File.ReadAllText(foreign));
        engine.TemporaryIdentity = Guid.NewGuid; engine.Observe = (stage, _) => { if (stage == RestoreStage.BeforeMetadata) throw new IOException("Injected metadata failure."); };
        Assert.Equal(1, engine.RestoreFile(v.Identity, target).Failed); Assert.False(File.Exists(target));
        Assert.Equal(new[] { foreign }, Directory.GetFiles(f.Temp.Root, ".fluff-restore-*.tmp"));
    }

    [Fact]
    public void CancellationBeforeInstallPreservesTargetAndAfterInstallReportsCommittedProgress()
    {
        using var f = new HistoryFixture(); var v = f.Add(); var target = f.Temp.Child("target"); File.WriteAllText(target, "keep");
        using (var cts = new CancellationTokenSource())
        {
            var engine = new RestoreEngine(f.Repository) { Observe = (stage, _) => { if (stage == RestoreStage.BeforeInstall) cts.Cancel(); } };
            var result = engine.RestoreFile(v.Identity, target, ExistingTargetPolicy.OverwriteFile, cts.Token);
            Assert.True(result.Cancelled); Assert.Equal(0, result.Restored); Assert.Equal("keep", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(f.Temp.Root, ".fluff-restore-*.tmp"));
        }
        using (var cts = new CancellationTokenSource())
        {
            var engine = new RestoreEngine(f.Repository) { Observe = (stage, _) => { if (stage == RestoreStage.Installed) cts.Cancel(); } };
            var result = engine.RestoreFile(v.Identity, target, ExistingTargetPolicy.OverwriteFile, cts.Token);
            Assert.True(result.Cancelled); Assert.Equal(1, result.Restored); Assert.Equal("hello", File.ReadAllText(target));
        }
    }

    [Fact]
    public void FolderPreflightRejectsHistoricalCollisionWithoutCreatingDestination()
    {
        using var f = new HistoryFixture(); f.Add("Foo"); f.Add("Foo.txt"); f.Add("Foo/Bar");
        var target = f.Temp.Child("restore");
        var result = new RestoreEngine(f.Repository).RestoreScope(new(HistoryFixture.Root), target,
            options: new(FailurePolicy: RestoreFailurePolicy.ContinueIndependentFiles));
        Assert.Equal(3, result.Selected); Assert.True(result.SelectionComplete); Assert.Equal(2, result.SelectionConflicts);
        Assert.Equal(0, result.Restored); Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData(RestoreFailurePolicy.StopOnFailure, 1)]
    [InlineData(RestoreFailurePolicy.ContinueIndependentFiles, 2)]
    public void FolderRuntimeFailurePoliciesKeepCompletedFilesAndUnrelatedDestinationContent(RestoreFailurePolicy failure, int expected)
    {
        using var f = new HistoryFixture(); f.Add("a"); var bad = f.Add("b"); f.Add("c");
        File.WriteAllText(f.Repository.GetPayloadPath(bad.Identity.VersionId), "wrong");
        var target = f.Temp.Child("restore"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "unrelated"), "keep");
        var result = new RestoreEngine(f.Repository).RestoreScope(new(HistoryFixture.Root), target, options: new(FailurePolicy: failure));
        Assert.Equal(3, result.Selected); Assert.Equal(expected, result.Restored); Assert.Equal(1, result.Failed); Assert.Equal(1, result.ChecksumFailures);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "a"))); Assert.False(File.Exists(Path.Combine(target, "b")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "unrelated")));
    }

    [Fact]
    public void FolderAsOfSelectionUsesDisplayPathsAndCancellationKeepsEarlierFile()
    {
        using var f = new HistoryFixture(); f.Add("Folder/A", "old", 0); f.Add("Folder/A", "new", 5); f.Add("Folder/B", day: 1); f.Add("later", day: 9);
        using var cts = new CancellationTokenSource(); var engine = new RestoreEngine(f.Repository);
        engine.Observe = (stage, _) => { if (stage == RestoreStage.Installed) cts.Cancel(); };
        var target = f.Temp.Child("restore"); var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        var result = engine.RestoreScope(new(HistoryFixture.Root, "folder"), target, new(HistoryFixture.Time.AddDays(2)), cancellationToken: cts.Token);
        Assert.Equal(2, result.Selected); Assert.Equal(1, result.Restored); Assert.True(result.Cancelled);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Folder", "A"))); Assert.False(File.Exists(Path.Combine(target, "Folder", "B")));
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InPlaceRestoreUsesExplicitMappingAndNextM13ScanDetectsOlderBytes(bool repeatedMetadata)
    {
        using var f = new HistoryFixture(); var source = f.Temp.Child("relocated"); Directory.CreateDirectory(source);
        var selected = new SourceSelection(new(HistoryFixture.Root, "stale-descriptive-path"), source);
        var path = Path.Combine(source, "Mixed.txt"); var stamp = HistoryFixture.Time;
        File.WriteAllText(path, "old"); File.SetLastWriteTimeUtc(path, stamp.UtcDateTime); var scan = new FileHistoryEngine(f.Repository); scan.Scan([selected]);
        using var before = f.Repository.OpenReader(); var old = before.GetCurrent(new(HistoryFixture.Root, "mixed.txt"))!;
        File.WriteAllText(path, repeatedMetadata ? "mid" : "new-longer"); File.SetLastWriteTimeUtc(path, stamp.AddDays(1).UtcDateTime); scan.Scan([selected]);
        if (repeatedMetadata) { File.WriteAllText(path, "end"); File.SetLastWriteTimeUtc(path, stamp.UtcDateTime); scan.Scan([selected]); }
        var inventory = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath); var engine = new RestoreEngine(f.Repository);
        var alternate = f.Temp.Child("alternate"); var alt = engine.RestoreFile(old.Identity, alternate);
        Assert.Equal(0, alt.MtimeAdjustedForChangeDetection); Assert.Equal(stamp.UtcDateTime, File.GetLastWriteTimeUtc(alternate));
        var result = engine.RestoreFileInPlace(old.Identity, selected, ExistingTargetPolicy.OverwriteFile);
        Assert.Equal(1, result.Restored); Assert.Equal(0, result.MtimeAdjustedForChangeDetection); Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal("old", File.ReadAllText(path)); Assert.Equal(inventory, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        Assert.NotEqual(stamp.UtcDateTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(1, scan.Scan([selected]).VersionsCreated);
        Assert.Throws<ArgumentException>(() => engine.RestoreFileInPlace(old.Identity, new(new(Guid.NewGuid(), source), source)));
    }

    [WindowsFact]
    public void WindowsMetadataAndRealSharingFailureArePreservedAcrossAtomicReplacement()
    {
        using var f = new HistoryFixture(); var v = f.Add(attributes: FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        var target = f.Temp.Child("target"); File.WriteAllText(target, "keep"); var engine = new RestoreEngine(f.Repository);
        using (var deny = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(1, engine.RestoreFile(v.Identity, target, ExistingTargetPolicy.OverwriteFile).Failed);
        Assert.Equal("keep", File.ReadAllText(target)); Assert.Equal(1, engine.RestoreFile(v.Identity, target, ExistingTargetPolicy.OverwriteFile).Restored);
        Assert.Equal(v.Metadata.CreationTimeUtc.UtcDateTime, File.GetCreationTimeUtc(target));
        Assert.Equal(v.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(target)); Assert.Equal(v.Metadata.Attributes, File.GetAttributes(target));
    }

    [Fact]
    public void DestinationAndPayloadSymlinksAreRejectedIncludingSubstitutionBeforeInstall()
    {
        using var f = new HistoryFixture(); var v = f.Add(); var external = f.Temp.Child("external"); Directory.CreateDirectory(external);
        var link = f.Temp.Child("link"); Directory.CreateSymbolicLink(link, external);
        var engine = new RestoreEngine(f.Repository); Assert.Equal(1, engine.RestoreFile(v.Identity, Path.Combine(link, "bad")).Failed);
        var payload = f.Repository.GetPayloadPath(v.Identity.VersionId); File.Delete(payload); File.CreateSymbolicLink(payload, Path.Combine(external, "absent"));
        Assert.Equal(1, engine.RestoreFile(v.Identity, f.Temp.Child("bad")).Failed);
        File.Delete(payload); File.WriteAllText(payload, "hello");
        var parent = f.Temp.Child("parent"); Directory.CreateDirectory(parent);
        engine.Observe = (stage, _) => { if (stage == RestoreStage.BeforeInstall) { Directory.Move(parent, parent + "-owned"); Directory.CreateSymbolicLink(parent, external); } };
        Assert.Equal(1, engine.RestoreFile(v.Identity, Path.Combine(parent, "bad")).Failed); Assert.Empty(Directory.GetFiles(external));
        // A substituted parent can leave a temp under its old name; do not sweep unknown locations.
    }

    [Fact]
    public void PayloadTruncationAndLateTargetAppearanceAreSafe()
    {
        using var f = new HistoryFixture(); var v = f.Add(content: new string('x', 300_000)); var target = f.Temp.Child("target");
        File.WriteAllText(target, "keep"); var engine = new RestoreEngine(f.Repository);
        engine.Observe = (stage, _) => { if (stage == RestoreStage.BeforeInstall) File.WriteAllText(target, "appeared"); };
        // A competing ordinary target is respected by the no-overwrite policy at installation.
        File.Delete(target);
        Assert.Equal(1, engine.RestoreFile(v.Identity, target).Failed); Assert.Equal("appeared", File.ReadAllText(target));
        var payload = f.Repository.GetPayloadPath(v.Identity.VersionId); engine.Observe = null;
        File.WriteAllText(payload, "short"); Assert.Equal(1, engine.RestoreFile(v.Identity, target, ExistingTargetPolicy.OverwriteFile).Failed);
        Assert.Equal("appeared", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InPlaceMtimeAdjustmentVerifiesActualTimestampAndFailsSafelyIfNotRepresentable(bool actualRounding)
    {
        using var f = new HistoryFixture(); var a = f.Add(content: "old", mtime: HistoryFixture.Time.AddTicks(actualRounding ? 1 : 0));
        f.Add(content: "mid", day: 1); f.Add(content: "new", day: 2, mtime: HistoryFixture.Time);
        var source = f.Temp.Child("source"); Directory.CreateDirectory(source); var target = Path.Combine(source, "a"); File.WriteAllText(target, "keep");
        var map = new SourceSelection(new(HistoryFixture.Root, "descriptive"), source); var engine = new RestoreEngine(f.Repository);
        engine.SetLastWrite = (path, _) => File.SetLastWriteTimeUtc(path, HistoryFixture.Time.UtcDateTime);
        var result = engine.RestoreFileInPlace(a.Identity, map, ExistingTargetPolicy.OverwriteFile);
        Assert.Equal(1, result.Failed); Assert.Equal("keep", File.ReadAllText(target)); Assert.Equal(0, result.MtimeAdjustedForChangeDetection);
        var calls = 0; engine.SetLastWrite = (path, time) => File.SetLastWriteTimeUtc(path, ++calls < 3 ? HistoryFixture.Time.UtcDateTime : time);
        result = engine.RestoreFileInPlace(a.Identity, map, ExistingTargetPolicy.OverwriteFile);
        Assert.Equal(1, result.MtimeAdjustedForChangeDetection); Assert.True(calls >= 3); Assert.NotEqual(HistoryFixture.Time.UtcDateTime, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public void UnsupportedDestinationTimestampIsAFileFailureAndIndependentFilesCanContinue()
    {
        using var f = new HistoryFixture(); f.Add("a"); f.Add("b"); var target = f.Temp.Child("restore"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a"), "keep"); var engine = new RestoreEngine(f.Repository); var calls = 0;
        engine.SetLastWrite = (path, time) =>
        {
            if (++calls == 1) throw new ArgumentOutOfRangeException(nameof(time), "Timestamp cannot be represented on destination.");
            File.SetLastWriteTimeUtc(path, time);
        };
        var result = engine.RestoreScope(new(HistoryFixture.Root), target, options: new(ExistingTargetPolicy.OverwriteFile, RestoreFailurePolicy.ContinueIndependentFiles));
        Assert.Equal(1, result.Failed); Assert.Equal(1, result.Restored); Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "a")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "b"))); Assert.Empty(Directory.GetFiles(target, ".fluff-restore-*.tmp"));
    }
}
