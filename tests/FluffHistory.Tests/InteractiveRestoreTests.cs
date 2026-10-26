using FluffHistory.Engine;
using FluffHistory.Windows;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

internal sealed class FakeShellCopy : IShellCopyOperation
{
    internal readonly List<(string Source, string Folder, string Name)> QueueItems = [];
    internal IShellCopyCallbacks Callbacks = null!;
    internal nint Owner; internal uint Flags; internal int Performs, Disposals, ContentCopies;
    internal Func<int, string, string, (int Hr, string? Path)>? Outcome;
    internal Action<int, string>? BeforePre;
    internal Action<int, string>? AfterPre { get; set; }
    internal ShellExecution? OverrideExecution;
    internal bool RetryFirstFailure { get; set; }
    internal bool OmitPost { get; set; }
    internal bool PendingFirst { get; set; }
    public void Configure(nint owner, uint flags, IShellCopyCallbacks callbacks) { Owner = owner; Flags = flags; Callbacks = callbacks; }
    public void Queue(string source, string destinationFolder, string historicalName) => QueueItems.Add((source, destinationFolder, historicalName));
    public ShellExecution Perform()
    {
        Performs++; var hr = 0;
        for (var i = 0; i < QueueItems.Count; i++)
        {
            var (source, folder, name) = QueueItems[i]; BeforePre?.Invoke(i, source);
            hr = Callbacks.CheckCancellation(); if (hr < 0) break;
            hr = Callbacks.PreCopy(source, folder); if (hr < 0) break;
            if (PendingFirst)
            {
                hr = Callbacks.PostCopy(source, folder, 0x0027000B, null); if (hr < 0) break;
                hr = Callbacks.PreCopy(source, folder); if (hr < 0) break;
            }
            AfterPre?.Invoke(i, source);
            var target = Path.Combine(folder, name); var outcome = Outcome?.Invoke(i, source, target) ?? (0, target);
            if (outcome.Item1 == 0)
            { File.Copy(source, outcome.Item2 ?? target, overwrite: true); ContentCopies++; }
            if (!OmitPost) hr = Callbacks.PostCopy(source, folder, outcome.Item1, outcome.Item2); if (hr < 0) break;
            if (RetryFirstFailure && outcome.Item1 < 0)
            {
                hr = Callbacks.PreCopy(source, folder); if (hr < 0) break;
                File.Copy(source, target, overwrite: true); ContentCopies++;
                hr = Callbacks.PostCopy(source, folder, 0, target); if (hr < 0) break;
            }
        }
        Callbacks.Finish(hr); return OverrideExecution ?? new(hr, hr < 0);
    }
    public void Dispose() { Disposals++; }
}

public sealed class InteractiveRestoreTests
{
    [Fact]
    public void PendingCollisionCanReachShellUiAndRetryMustReverifyBeforeConfirmedCopy()
    {
        using var f = new HistoryFixture(); var version = f.Add("a.txt");
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), [new DisplayedRestoreFile(version.Identity)]));
        var result = InteractiveRestore.Execute(plan, () => new FakeShellCopy { PendingFirst = true });
        Assert.Equal(1, result.Copied); Assert.Equal(2, result.VerifiedPayloads); Assert.Equal(2, result.CopiesAttempted);
        Assert.Equal(0, result.Unconfirmed); Assert.Equal(0, result.CallbackFailures); Assert.Equal(0, result.NoCompletionReported);
        File.Delete(Path.Combine(plan.DestinationDirectory, "a.txt"));
        result = InteractiveRestore.Execute(plan, () => new FakeShellCopy { Outcome = (_, _, _) => (0x0027000B, null) });
        Assert.Equal(0, result.Copied); Assert.Equal(1, result.Unconfirmed); Assert.Equal(1, result.NoCompletionReported); Assert.Equal(0, result.CallbackFailures);
        Assert.False(File.Exists(Path.Combine(plan.DestinationDirectory, "a.txt")));
    }
    [Fact]
    public void UnexpectedActualOutputNeverReceivesMetadataAndStopsFurtherCopies()
    {
        using var f = new HistoryFixture(); var version = f.Add("a.txt"); var escaped = f.Temp.Child("outside.txt");
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), [new DisplayedRestoreFile(version.Identity)]));
        var shell = new FakeShellCopy { Outcome = (_, _, _) => (0, escaped) };
        var result = InteractiveRestore.Execute(plan, () => shell);
        Assert.Equal(1, result.Copied); Assert.True(result.OperationsAborted); Assert.Equal(1, result.CallbackFailures);
        Assert.NotEqual(version.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(escaped));
    }

    [Fact]
    public void DestinationParentSubstitutionBeforePreCopyIsRejectedWithoutContentWrites()
    {
        using var f = new HistoryFixture(); f.Add("Folder/a.txt"); var output = f.Temp.Child("out"); var outside = f.Temp.Child("outside"); Directory.CreateDirectory(outside);
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, output, [new DisplayedRestoreFolder("Folder")]));
        var shell = new FakeShellCopy { BeforePre = (_, _) =>
        {
            var parent = Path.Combine(output, "Folder"); Directory.Delete(parent); Directory.CreateSymbolicLink(parent, outside);
        } };
        var result = InteractiveRestore.Execute(plan, () => shell);
        Assert.Equal(0, result.Copied); Assert.Equal(0, shell.ContentCopies); Assert.True(result.OperationsAborted);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void ShellRetryReverifiesExactPayloadAndMissingCompletionsRemainExplicitlyUnconfirmed()
    {
        using var f = new HistoryFixture(); var v = f.Add("a.txt");
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), [new DisplayedRestoreFile(v.Identity)]));
        var retry = new FakeShellCopy { RetryFirstFailure = true, Outcome = (_, _, _) => (unchecked((int)0x80070005), null) };
        var result = InteractiveRestore.Execute(plan, () => retry);
        Assert.Equal(1, result.Copied); Assert.Equal(1, result.Failed); Assert.Equal(2, result.CopiesAttempted); Assert.Equal(2, result.VerifiedPayloads);
        Assert.Equal(0, result.NoCompletionReported); Assert.False(result.OperationsAborted);
        var noPost = new FakeShellCopy { OmitPost = true };
        result = InteractiveRestore.Execute(plan, () => noPost);
        Assert.Equal(0, result.Copied); Assert.Equal(1, result.NoCompletionReported); Assert.Equal(1, result.Unconfirmed); Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void ConcurrentVerificationCallbacksAreSerializedAndHandleBoundIsEnforced()
    {
        using var f = new HistoryFixture(); var versions = Enumerable.Range(0, 9).Select(i => f.Add($"file{i}.txt")).ToArray();
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), versions.Select(v => (DisplayedRestoreItem)new DisplayedRestoreFile(v.Identity)).ToArray()));
        Directory.CreateDirectory(plan.DestinationDirectory); var result = new InteractiveRestoreResult();
        using var callbacks = new RestoreCallbacks(plan, result, default, File.SetLastWriteTimeUtc, () => DateTimeOffset.UtcNow);
        Parallel.ForEach(plan.Files.Take(8), file => Assert.Equal(0, callbacks.PreCopy(file.PayloadPath, plan.DestinationDirectory)));
        Assert.Equal(8, result.VerifiedPayloads); Assert.Equal(8, result.PeakVerificationHandles);
        Assert.True(callbacks.PreCopy(plan.Files[8].PayloadPath, plan.DestinationDirectory) < 0);
        Assert.Equal(8, result.PeakVerificationHandles); callbacks.Finish(0);
        foreach (var file in plan.Files) using (new FileStream(file.PayloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void QueueUsesOneOperationWithNoCollisionPolicyAndCorrelatesActualRenamedOutput(bool encrypted)
    {
        using var f = new HistoryFixture(encrypted); var a = f.Add("Documents/A.txt", "historical"); var b = f.Add("Documents/Folder/B.txt", "second");
        var output = f.Temp.Child("out"); Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(output, "Folder"));
        File.WriteAllText(Path.Combine(output, "A.txt"), "keep-existing");
        var request = DisplayedRestoreTests.Request(f.Repository, output,
            [new DisplayedRestoreFile(a.Identity), new DisplayedRestoreFolder("Folder")], "Documents") with { OwnerWindow = (nint)0x12345678 };
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(request);
        var renamed = Path.Combine(output, "A (2).txt");
        var shell = new FakeShellCopy { Outcome = (i, _, target) => (0, i == 0 ? renamed : target) };
        var result = InteractiveRestore.Execute(plan, () => shell);
        Assert.Equal(0u, shell.Flags); Assert.Equal(InteractiveRestore.ProductionFlags, shell.Flags); Assert.Equal(request.OwnerWindow, shell.Owner);
        Assert.Equal(1, shell.Performs); Assert.Equal(1, shell.Disposals); Assert.Equal(1, result.PerformOperationsCount);
        Assert.Equal(2, result.QueuedCopies); Assert.Equal(2, result.Copied); Assert.Equal(2, result.VerifiedPayloads);
        Assert.Equal(plan.PlannedBytes, result.VerificationBytes); Assert.Equal(plan.PlannedBytes, result.CopiedPayloadBytes); Assert.Equal(1, result.PeakVerificationHandles);
        Assert.Equal("keep-existing", File.ReadAllText(Path.Combine(output, "A.txt"))); Assert.Equal("historical", File.ReadAllText(renamed));
        Assert.Equal(a.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(renamed));
        Assert.Equal(renamed, result.Items[0].ActualPath); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
        Assert.All(shell.QueueItems, q => Assert.EndsWith(encrypted ? ".plain" : ".bin", q.Source));
        if (encrypted) Assert.All(shell.QueueItems, q => Assert.False(File.Exists(q.Source)));
        Assert.Equal("A.txt", shell.QueueItems[0].Name); Assert.Equal("B.txt", shell.QueueItems[1].Name);
    }

    [Theory]
    [InlineData("corrupt", false)] [InlineData("truncated", false)] [InlineData("missing", false)] [InlineData("replaced", false)]
    [InlineData("corrupt", true)] [InlineData("truncated", true)] [InlineData("missing", true)] [InlineData("replaced", true)]
    public void IntegrityFailureBeforeCopyStopsLaterItemsAndPreservesEarlierSuccess(string damage, bool encrypted)
    {
        using var f = new HistoryFixture(encrypted); var a = f.Add("a.txt", "aaa"); var b = f.Add("b.txt", "bbb"); f.Add("c.txt", "ccc");
        var request = DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"),
            [new DisplayedRestoreFile(a.Identity), new DisplayedRestoreFile(b.Identity)]);
        // Include a third queued item to prove the remaining queue does not run after failed verification.
        using (var r = f.Repository.OpenReader()) request = request with { Items = [.. request.Items, new DisplayedRestoreFile(r.GetNewestVersion(new(HistoryFixture.Root, "c.txt"))!.Identity)] };
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(request);
        var shell = new FakeShellCopy { BeforePre = (i, path) =>
        {
            if (i != 1) return;
            if (damage == "missing") File.Delete(path);
            else if (damage == "replaced") { File.Delete(path); File.WriteAllText(path, "bad"); }
            else File.WriteAllText(path, damage == "truncated" ? "b" : "bad");
        } };
        var result = InteractiveRestore.Execute(plan, () => shell);
        Assert.Equal(1, result.Copied); Assert.Equal(1, result.IntegrityFailures); Assert.True(result.OperationsAborted);
        Assert.Equal(1, shell.ContentCopies); Assert.Equal("aaa", File.ReadAllText(Path.Combine(request.DestinationDirectory!, "a.txt")));
        Assert.False(File.Exists(Path.Combine(request.DestinationDirectory!, "b.txt"))); Assert.False(File.Exists(Path.Combine(request.DestinationDirectory!, "c.txt")));
        foreach (var file in plan.Files.Where(p => File.Exists(p.PayloadPath))) using (new FileStream(file.PayloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData("skip")] [InlineData("failure")] [InlineData("missing-created")] [InlineData("abort-success")]
    [InlineData("abort-failure")] [InlineData("aborted-query-failure")]
    public void ShellOutcomesAreNotInferredFromPerformSuccessAlone(string outcome)
    {
        using var f = new HistoryFixture(); var v = f.Add("a.txt");
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), [new DisplayedRestoreFile(v.Identity)]));
        var shell = new FakeShellCopy();
        switch (outcome)
        {
            case "skip": shell.Outcome = (_, _, _) => (0x00270005, null); break;
            case "failure": shell.Outcome = (_, _, _) => (unchecked((int)0x80070005), null); break;
            case "missing-created": shell.Outcome = (_, _, _) => (0, null); break;
            case "abort-success": shell.OverrideExecution = new(0, true); break;
            case "abort-failure": shell.OverrideExecution = new(unchecked((int)0x800704C7), true); break;
            case "aborted-query-failure": shell.OverrideExecution = new(0, false, unchecked((int)0x80004005)); break;
        }
        var r = InteractiveRestore.Execute(plan, () => shell);
        if (outcome == "skip") { Assert.Equal(1, r.Skipped); Assert.Equal(0, r.Copied); }
        if (outcome == "failure") { Assert.Equal(1, r.Failed); Assert.Equal(unchecked((int)0x80070005), r.Items[0].HResult); }
        if (outcome == "missing-created") { Assert.Equal(1, r.Unconfirmed); Assert.Equal(0, r.Copied); Assert.True(r.OperationsAborted); }
        if (outcome.StartsWith("abort-", StringComparison.Ordinal)) { Assert.True(r.OperationsAborted); Assert.Equal(1, r.Copied); }
        if (outcome == "aborted-query-failure") { Assert.True(r.AbortedQueryHResult < 0); Assert.NotEmpty(r.Problems); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MetadataFailureReportsCompletedContentAndContinuesIndependentCopies(bool original)
    {
        using var f = new HistoryFixture(); var a = f.Add("a.txt"); var b = f.Add("b.txt"); var output = f.Temp.Child("out");
        var request = DisplayedRestoreTests.Request(f.Repository, output, [new DisplayedRestoreFile(a.Identity), new DisplayedRestoreFile(b.Identity)]);
        if (original) request = request with { DestinationMode = InteractiveRestoreDestination.OriginalLocation, DestinationDirectory = null,
            OriginalSource = new(new(HistoryFixture.Root, "ignored"), output) };
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(request); var calls = 0;
        var result = InteractiveRestore.Execute(plan, () => new FakeShellCopy(), setLastWrite: (p, t) =>
        { if (++calls == 1) throw new IOException("Injected metadata failure"); File.SetLastWriteTimeUtc(p, t); });
        Assert.Equal(2, result.Copied); Assert.Equal(1, result.MetadataWarnings); Assert.Equal(original ? 1 : 0, result.ChangeDetectionTimestampFailed);
        Assert.False(result.OperationsAborted); Assert.NotNull(result.Items[0].Warning); Assert.True(File.Exists(Path.Combine(output, "b.txt")));
    }

    [Fact]
    public void CancellationKeepsCompletedFilesAndCallbackMismatchesFailWithoutThrowing()
    {
        using var f = new HistoryFixture(); var a = f.Add("a.txt"); var b = f.Add("b.txt");
        using var plan = new DisplayedRestorePlanner(f.Repository).Plan(DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), [new DisplayedRestoreFile(a.Identity), new DisplayedRestoreFile(b.Identity)]));
        using var cts = new CancellationTokenSource(); var shell = new FakeShellCopy { BeforePre = (i, _) => { if (i == 1) cts.Cancel(); } };
        var result = InteractiveRestore.Execute(plan, () => shell, cts.Token);
        Assert.Equal(1, result.Copied); Assert.True(result.OperationsAborted); Assert.True(result.CancellationRequested);
        var empty = new InteractiveRestoreResult(); using var callbacks = new RestoreCallbacks(plan, empty, default, File.SetLastWriteTimeUtc, () => DateTimeOffset.UtcNow);
        Assert.True(callbacks.PreCopy("unplanned", plan.DestinationDirectory) < 0); Assert.Equal(1, empty.CallbackFailures);
    }

    [Fact]
    public void PlatformGuardsDoNotInvokeShellOnOtherOperatingSystems()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new HistoryFixture(); var v = f.Add();
        var request = DisplayedRestoreTests.Request(f.Repository, f.Temp.Child("out"), [new DisplayedRestoreFile(v.Identity)]);
        Assert.Throws<PlatformNotSupportedException>(() => { _ = new InteractiveRestore(f.Repository).RestoreAsync(request); });
        Assert.Throws<PlatformNotSupportedException>(() => { _ = new HistoricalPreview(f.Repository).OpenAsync(v.Identity); });
    }
}
