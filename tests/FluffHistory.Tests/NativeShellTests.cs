using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Engine;
using FluffHistory.Windows;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class NativeShellTests
{
    [WindowsFact]
    public async Task ExactShellRestoreUsesOlderTiedVersionAndOriginalLocationChangeDetection()
    {
        using var f = new HistoryFixture(); var old = f.Add("Folder/a.txt", "old", 2); f.Add("Folder/a.txt", "newer", 2);
        using var reader = f.Repository.OpenReader(); var source = f.Temp.Child("source");
        var result = await new InteractiveRestore(f.Repository).RestoreExactAsync(reader.Authority, old.Identity,
            InteractiveRestoreDestination.OriginalLocation, originalSource: new(new(FoundationTests.Root, "descriptive"), source));
        Assert.Equal(1, result.Copied); Assert.Equal(0, result.Failed); Assert.Equal(0, result.Unconfirmed);
        var target = Path.Combine(source, "FOLDER", "a.txt"); Assert.Equal("old", File.ReadAllText(target));
        Assert.True(File.GetLastWriteTimeUtc(target) > old.Metadata.LastWriteTimeUtc.UtcDateTime);
        Assert.Equal(1, result.VerifiedPayloads); Assert.Equal(1, result.PerformOperationsCount);
    }
    [WindowsFact]
    public async Task InteractiveServiceCopiesSelectedNestedFilesAndAppliesHistoricalMetadataOnActualOutputs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new HistoryFixture(); var a = f.Add("Documents/Report.txt", "report", 2, attributes: FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        f.Add("Documents/Work/Nested/b.txt", "nested", 1); f.Add("Documents/Work/c.txt", "third", 3);
        var output = f.Temp.Child("out"); Directory.CreateDirectory(Path.Combine(output, "Work", "Nested"));
        var request = DisplayedRestoreTests.Request(f.Repository, output, [new DisplayedRestoreFile(a.Identity), new DisplayedRestoreFolder("Work")], "Documents");
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var actualOutput = repeat == 0 ? output : f.Temp.Child("out" + repeat);
            var result = await new InteractiveRestore(f.Repository).RestoreAsync(request with { DestinationDirectory = actualOutput });
            Assert.Equal(0, result.HResult); Assert.False(result.OperationsAborted); Assert.Empty(result.Problems);
            Assert.Equal(3, result.Copied); Assert.Equal(3, result.QueuedCopies); Assert.Equal(3, result.VerifiedPayloads);
            Assert.Equal(1, result.PerformOperationsCount); Assert.Equal(1, result.PeakVerificationHandles); Assert.Equal(0, result.MetadataWarnings);
            Assert.Equal(result.PlannedBytes, result.VerificationBytes); Assert.Equal(result.PlannedBytes, result.CopiedPayloadBytes);
            var report = Path.Combine(actualOutput, "Report.txt"); Assert.Equal("report", File.ReadAllText(report));
            Assert.Equal(a.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(report)); Assert.Equal(a.Metadata.CreationTimeUtc.UtcDateTime, File.GetCreationTimeUtc(report));
            Assert.Equal(a.Metadata.Attributes, File.GetAttributes(report)); Assert.All(result.Items, i => Assert.NotNull(i.ActualPath));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(actualOutput, "Work", "Nested", "b.txt")));
            Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        }
    }

    [WindowsFact]
    public async Task OriginalLocationShellRestoreUsesNowAndNextRealM13ScanCreatesNewVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new HistoryFixture(); var source = f.Temp.Child("source"); Directory.CreateDirectory(Path.Combine(source, "Folder"));
        var mapping = new SourceSelection(new(HistoryFixture.Root, "obsolete-location"), source);
        var path = Path.Combine(source, "Folder", "A.txt"); File.WriteAllText(path, "old"); File.SetLastWriteTimeUtc(path, HistoryFixture.Time.UtcDateTime);
        var engine = new FileHistoryEngine(f.Repository); Assert.Equal(1, engine.Scan([mapping]).VersionsCreated);
        using var initial = f.Repository.OpenReader(); var old = initial.GetCurrent(new(HistoryFixture.Root, "Folder/A.txt"))!;
        File.WriteAllText(path, "newer content"); File.SetLastWriteTimeUtc(path, HistoryFixture.Time.AddDays(1).UtcDateTime);
        Assert.Equal(1, engine.Scan([mapping]).VersionsCreated); File.Delete(path); // Avoid real conflict UI in automated tests.
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath); var started = DateTime.UtcNow;
        var request = DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("unused"), [new DisplayedRestoreFile(old.Identity)], "Folder", new(old.StoredUtc)) with
        { DestinationMode = InteractiveRestoreDestination.OriginalLocation, DestinationDirectory = null, OriginalSource = mapping };
        var restored = await new InteractiveRestore(f.Repository).RestoreAsync(request);
        Assert.Equal(1, restored.Copied); Assert.Equal(0, restored.ChangeDetectionTimestampFailed); Assert.Equal(0, restored.MetadataWarnings);
        Assert.Equal("old", File.ReadAllText(path)); Assert.InRange(File.GetLastWriteTimeUtc(path), started.AddSeconds(-2), DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath)); Assert.Equal(1, engine.Scan([mapping]).VersionsCreated);
        using var after = f.Repository.OpenReader(); var newest = after.GetCurrent(old.Identity.Path)!;
        Assert.NotEqual(old.Identity.VersionId, newest.Identity.VersionId); Assert.Equal(old.Checksum, newest.Checksum);
    }

    [WindowsFact]
    public void CallbackExceptionsAndUnexpectedNonCopyRequestsCannotEscapeUnmanagedBoundary()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new HistoryFixture(); using var reader = f.Repository.OpenReader();
        using var callbacks = new ProbeCallbacks(reader, []); var sink = new NativeProgressSink(callbacks);
        Assert.True(sink.PreCopyItem(0, null!, null!, null) < 0); Assert.NotNull(callbacks.Error);
        callbacks.Error = null; Assert.True(sink.PreMoveItem(0, null!, null!, null) < 0); Assert.IsType<InvalidOperationException>(callbacks.Error);
        Assert.True(sink.PreDeleteItem(0, null!) < 0); Assert.True(sink.PreRenameItem(0, null!, null) < 0);
        Assert.True(sink.PostCopyItem(0, null!, null!, null, 0, null) < 0);
    }

    [WindowsFact]
    public async Task ActualShellCopiesUuidPayloadsUnderHistoricalNamesWhileVerifiedHandleDeniesMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new HistoryFixture();
        var versions = new[] { f.Add("first.txt", "first"), f.Add("second.txt", "second"), f.Add("Folder/third.txt", "third") };
        var destination = f.Temp.Child("restore"); Directory.CreateDirectory(destination); Directory.CreateDirectory(Path.Combine(destination, "Folder"));
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        await StaOperation.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var reader = f.Repository.OpenReader();
            using var callbacks = new ProbeCallbacks(reader, versions.ToDictionary(v => f.Repository.GetPayloadPath(v.Identity.VersionId)));
            using var operation = new NativeShellCopyOperation(); operation.Configure(0, 0, callbacks);
            foreach (var version in versions)
            {
                var relative = version.Metadata.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                operation.Queue(f.Repository.GetPayloadPath(version.Identity.VersionId), Path.GetDirectoryName(Path.Combine(destination, relative))!, Path.GetFileName(relative));
            }
            var execution = operation.Perform();
            Assert.Null(callbacks.Error); Assert.Equal(0, execution.HResult); Assert.False(execution.Aborted); Assert.Equal(0, execution.AbortedQueryHResult);
            Assert.Equal(3, callbacks.PreCount); Assert.Equal(3, callbacks.PostCount); Assert.Equal(1, callbacks.PeakHandles);
            Assert.Equal(3, callbacks.Actual.Count); Assert.All(callbacks.Actual, p => Assert.True(File.Exists(p)));
            return true;
        });
        Assert.Equal("first", File.ReadAllText(Path.Combine(destination, "first.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "second.txt")));
        Assert.Equal("third", File.ReadAllText(Path.Combine(destination, "Folder", "third.txt")));
        Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        foreach (var version in versions)
            using (new FileStream(f.Repository.GetPayloadPath(version.Identity.VersionId), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    private sealed class ProbeCallbacks(RepositoryReader reader, Dictionary<string, StoredVersion> versions) : IShellCopyCallbacks, IDisposable
    {
        private readonly Dictionary<string, VersionPayload> _active = [];
        internal int PreCount, PostCount, PeakHandles;
        internal Exception? Error;
        internal List<string> Actual { get; } = [];
        public int CheckCancellation() => 0;
        public int PreCopy(string source, string destinationFolder)
        {
            var payload = reader.OpenVersionPayload(versions[source].Identity)!; _active.Add(source, payload);
            Assert.Throws<IOException>(() => { using var blocked = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Throws<IOException>(() => File.Delete(source));
            var replacement = Path.Combine(Path.GetDirectoryName(source)!, ".probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(replacement, "replacement");
                Assert.Throws<IOException>(() => File.Replace(replacement, source, null));
            }
            finally { if (File.Exists(replacement)) File.Delete(replacement); }
            PeakHandles = Math.Max(PeakHandles, _active.Count); PreCount++; return 0;
        }
        public int PostCopy(string source, string destinationFolder, int hResult, string? actualPath)
        {
            Assert.Equal(0, hResult); Assert.NotNull(actualPath); Actual.Add(actualPath); PostCount++;
            Assert.True(_active.Remove(source, out var payload)); payload!.Dispose(); return 0;
        }
        public int Finish(int hResult) { Dispose(); return 0; }
        public void Fault(Exception exception) => Error = exception;
        public void Dispose() { foreach (var p in _active.Values) p.Dispose(); _active.Clear(); }
    }
}
