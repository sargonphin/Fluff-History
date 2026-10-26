using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic"), Trait("Category", "SmbIntegration")]
public sealed class SmbIntegrationTests(ITestOutputHelper output)
{
    [SmbFact]
    public async Task UncCreateReopenUnicodeLongPathsAndIndependentWriterExclusion()
    {
        using var f = new SmbTestEnvironment();
        var path = Path.Combine(f.Remote, "repository-über-日本語", new string('a', 90), new string('b', 90));
        var repo = Repository.Create(path); var opened = Repository.Open(path);
        Assert.Equal(repo.Identity, opened.Identity);
        if (SmbNetworkMeasurementTests.After)
        { Assert.True(repo.ObserveCapacity().Capacity > 0); Assert.True(repo.ObserveAllocationGeometry(f.Local).AllocationUnit > 0); }
        using (var writer = repo.OpenWriter(f.Local))
        using (var reader = repo.OpenReader())
            writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "über-日本語")]), reader.Authority);
        using (var reader = opened.OpenReader()) Assert.Equal("über-日本語", reader.GetSourceRoot(FoundationTests.Root)!.OriginalPath);
        await using var first = WriterProcess.Start("hold-writer", path);
        await first.WaitUntilReadyAsync();
        await using (var second = WriterProcess.Start("try-writer", path)) Assert.Equal(10, await second.ExitCodeAsync());
        using (var reader = repo.OpenReader()) Assert.NotNull(reader.GetSourceRoot(FoundationTests.Root));
        await first.KillAsync();
        await using var next = WriterProcess.Start("try-writer", path); Assert.Equal(0, await next.ExitCodeAsync());
        output.WriteLine("SMB_CASE create/open Unicode longpath freshprocess exclusion readers deathrelease passed");
    }

    [SmbFact]
    public async Task ConcurrentObserversSeeOnlyWholeValidAuthorities()
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        using var stop = new CancellationTokenSource(); var seen = new HashSet<AuthorityIdentity>();
        var observer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) { var authority = repo.ReadAuthority(); lock (seen) seen.Add(authority.Identity); }
        });
        var expected = new HashSet<AuthorityIdentity> { repo.ReadAuthority().Identity };
        var published = 0; var denied = 0;
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var before = repo.ReadAuthority().Identity; var unpublished = false;
                using (var writer = repo.OpenWriter(f.Local))
                {
                    try
                    {
                        expected.Add(writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source" + i)]), before).Authority);
                        published++;
                    }
                    catch (IOException error) when (error is not PublicationUncertainException && DeniedNativeRename(error))
                    {
                        // Samba can deny a rename under observer activity. This independent
                        // intent is not retried: require exact nonpublication and reconcile
                        // under fresh ownership before attempting the next Source{i} value.
                        Assert.Equal(before, repo.ReadAuthority().Identity);
                        Assert.Throws<InvalidOperationException>(() => writer.OpenOwnedReader());
                        denied++; unpublished = true;
                    }
                }
                if (unpublished)
                {
                    using var fresh = repo.OpenWriter(f.Local);
                    var transaction = Assert.Single(fresh.EnumeratePendingPublications());
                    Assert.Equal(PublicationOutcome.Unpublished, fresh.Reconcile(transaction));
                }
            }
        }
        finally { stop.Cancel(); await observer.WaitAsync(TimeSpan.FromSeconds(30)); }
        Assert.True(published >= 2, "Repeated confirmed publications must be observed.");
        Assert.NotEmpty(seen); Assert.All(seen, value => Assert.Contains(value, expected));
        output.WriteLine($"SMB_CASE atomic observers {seen.Count} valid states, {published} publications, {denied} definite unpublished denials");
        new SmbNetworkMeasurementTests(output).Save("authority-observers", new { Attempts = 12, Published = published,
            DefiniteUnpublishedReconciled = denied, DistinctValidObservedAuthorities = seen.Count, InvalidObservations = 0 });
    }

    private static bool DeniedNativeRename(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is IOException { InnerException: System.ComponentModel.Win32Exception { NativeErrorCode: 5 or 32 } } &&
                current.Message == "Network authority replacement rename failed.") return true;
        return false;
    }

    [SmbFact]
    public void DeniedAuthorityRenameKeepsPredecessorAndRequiresFreshReconciliation()
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var before = repo.ReadAuthority().Identity; var state = Path.Combine(repo.DirectoryPath, "state.json");
        var bytes = File.ReadAllBytes(state); Guid transaction;
        // Real SMB sharing denial, not a simulated Replace exception. Hold the old
        // authority without delete sharing until the failed writer has been disposed.
        using (var held = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var writer = repo.OpenWriter(f.Local))
        {
            var error = Assert.ThrowsAny<IOException>(() => writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Denied intent")]), before));
            Assert.IsNotType<PublicationUncertainException>(error); Assert.True(DeniedNativeRename(error));
            Assert.Equal(before, repo.ReadAuthority().Identity); Assert.Equal(bytes, File.ReadAllBytes(state));
            Assert.Throws<InvalidOperationException>(() => writer.OpenOwnedReader());
            transaction = Guid.ParseExact(Path.GetFileName(Assert.Single(Directory.GetFiles(repo.DirectoryPath, "state.*.tmp")))[6..^4], "N");
        }
        using var fresh = repo.OpenWriter(f.Local);
        Assert.Equal(PublicationOutcome.Unpublished, fresh.Reconcile(transaction));
        using (var reader = repo.OpenReader()) Assert.Null(reader.GetSourceRoot(FoundationTests.Root));
        var differentRoot = Guid.NewGuid();
        Assert.True(fresh.Apply(new(sourceRoots: [new(differentRoot, "Independent next intent")]), before).Changed);
        using (var reader = repo.OpenReader()) Assert.Null(reader.GetSourceRoot(FoundationTests.Root));
        new SmbNetworkMeasurementTests(output).Save("authority-denied", new { Outcome = "Unpublished", PredecessorPreserved = true,
            FailedSessionAborted = true, ReconciledUnderFreshOwnership = true, OriginalIntentRetried = false, IndependentNextIntentPublished = true });
    }

    [SmbTheory]
    [InlineData("reusable-a-partial", false)] [InlineData("reusable-a-ready", false)]
    [InlineData("m19-before-candidate", false)] [InlineData("reusable-a-prepared", false)]
    [InlineData("before-replace", false)] [InlineData("after-replace", true)]
    [InlineData("m19-confirmation", true)]
    public async Task ProcessDeathPreservesExactRemoteAuthorityAndReconciliation(string mode, bool published)
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        using (var writer = repo.OpenWriter(f.Local))
            writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source")]), repo.ReadAuthority().Identity);
        var before = repo.ReadAuthority().Identity;
        await using (var child = WriterProcess.Start(mode, repo.DirectoryPath))
        { await child.WaitUntilReadyAsync(); await child.KillAsync(); }
        var actual = Repository.Open(repo.DirectoryPath).ReadAuthority().Identity;
        Assert.Equal(published, actual != before);
        using var fresh = repo.OpenWriter(f.Local);
        var candidates = Directory.GetFiles(repo.DirectoryPath, "state.*.tmp");
        foreach (var candidate in candidates)
        {
            var id = Guid.ParseExact(Path.GetFileName(candidate)[6..^4], "N");
            Assert.Equal(published ? PublicationOutcome.Published : PublicationOutcome.Unpublished, fresh.Reconcile(id));
        }
        if (published) Assert.Equal(PublicationOutcome.Published, fresh.Reconcile(actual.TransactionId));
        output.WriteLine($"SMB_CASE death {mode} published={published} candidates={candidates.Length}");
    }

    [SmbFact]
    public void RemoteSourceEnumeratesChangesDeletesAndReactivatesHistory()
    {
        using var f = new SmbTestEnvironment(); var source = Path.Combine(f.Remote, "source"); Directory.CreateDirectory(source);
        var file = Path.Combine(source, "日本語.txt"); File.WriteAllText(file, "hello");
        var repo = Repository.Create(Path.Combine(f.Local, "repository"));
        var engine = new FileHistoryEngine(repo, new(WorkspaceRoot: Path.Combine(f.Local, "work")));
        SourceSelection[] selections = [new(new(FoundationTests.Root, source), source)];
        Assert.Equal(1, engine.Scan(selections).VersionsCreated);
        Assert.Equal(1, engine.Scan(selections).Unchanged);
        File.WriteAllText(file, "changed-content"); Assert.Equal(1, engine.Scan(selections).VersionsCreated);
        var time = File.GetLastWriteTimeUtc(file); File.Delete(file); Assert.Equal(1, engine.Scan(selections).Removals);
        File.WriteAllText(file, "changed-content"); File.SetLastWriteTimeUtc(file, time);
        Assert.Equal(1, engine.Scan(selections).Reactivations);
        File.WriteAllText(Path.Combine(source, "new.txt"), "new"); Assert.Equal(1, engine.Scan(selections).VersionsCreated);
        using var reader = repo.OpenReader(); Assert.Equal(2, reader.EnumerateCurrent().Count());
        output.WriteLine("SMB_CASE remote source unchanged changed new delete reactivation passed");
    }

    [SmbTheory]
    [InlineData("root")] [InlineData("child")] [InlineData("observation")] [InlineData("content")]
    public void RemoteSourceFailuresNeverProveDeletion(string stage)
    {
        using var f = new SmbTestEnvironment(); var source = Path.Combine(f.Remote, "source"); Directory.CreateDirectory(Path.Combine(source, "child"));
        File.WriteAllText(Path.Combine(source, "child", "file.txt"), "hello");
        var repo = Repository.Create(Path.Combine(f.Local, "repository"));
        SourceSelection[] selections = [new(new(FoundationTests.Root, source), source)];
        new FileHistoryEngine(repo).Scan(selections);
        File.WriteAllText(Path.Combine(source, "child", "file.txt"), "changed-content");
        var failed = new FileHistoryEngine(repo, new(), new FailedRemoteSource(stage, source)).Scan(selections);
        Assert.Equal(0, failed.Removals); Assert.Equal(0, failed.VersionsCreated);
        using var reader = repo.OpenReader(); Assert.Single(reader.EnumerateCurrent());
        output.WriteLine("SMB_CASE source barrier " + stage + " " + JsonSerializer.Serialize(failed));
    }
    private sealed class FailedRemoteSource(string stage, string root) : SourceAccess
    {
        internal override IEnumerable<string> EnumerateNames(string directory)
        {
            if (stage == "root" && directory == root || stage == "child" && directory.EndsWith("child", StringComparison.Ordinal)) throw new IOException("Interrupted enumeration");
            return base.EnumerateNames(directory);
        }
        internal override SourceObservation Observe(string path)
        { if (stage == "observation" && path.EndsWith("file.txt", StringComparison.Ordinal)) throw new IOException("Interrupted observation"); return base.Observe(path); }
        internal override int Read(Stream stream, Span<byte> buffer)
        { if (stage == "content") throw new IOException("Interrupted content"); return base.Read(stream, buffer); }
    }

    [SmbTheory]
    [InlineData(false)] [InlineData(true)]
    public void ReusablePayloadReadyValidationBindingDiscardCommitAndEviction(bool discard)
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        using var writer = repo.OpenWriter(f.Local);
        writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source")]), repo.ReadAuthority().Identity);
        using (var draft = ReuseFixture.Ready(writer)) { }
        using (var candidate = writer.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time))
        {
            Assert.NotNull(candidate); Assert.Equal(5, writer.ReusableStatistics.LocalValidationBytes);
            using var adopted = writer.AdoptReusablePayload(candidate);
            using var mutation = ReuseFixture.Prepare(repo, writer, adopted);
            Assert.Equal(ReusablePayloadState.TransactionBound, adopted.Reusable!.State);
            if (!discard) { adopted.Install(); mutation.Commit(); }
        }
        if (discard)
        {
            writer.Dispose(); using var fresh = repo.OpenWriter(f.Local);
            var ready = fresh.GetReadyReusablePayloads(); Assert.Single(ready.Entries);
            Assert.True(fresh.RemoveReadyReusablePayload(ready.Entries[0]));
        }
        using var reader = repo.OpenReader(); Assert.Equal(discard ? 0 : 1, reader.EnumerateCurrent().Count());
        output.WriteLine("SMB_CASE M17 lifecycle discard=" + discard);
    }

    [SmbTheory]
    [InlineData(false)] [InlineData(true)]
    public void ReusablePayloadReconciliationRestoresReadyOnlyForDefiniteUnpublished(bool published)
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository")); Guid transaction;
        using (var writer = repo.OpenWriter(f.Local))
        {
            writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source")]), repo.ReadAuthority().Identity);
            using var draft = ReuseFixture.Ready(writer); using var mutation = ReuseFixture.Prepare(repo, writer, draft);
            transaction = mutation.IntendedAuthority.TransactionId; draft.Install();
            writer.Observe = stage =>
            {
                if (stage == (published ? PublicationStage.AfterReplace : PublicationStage.BeforeMetadataInstall))
                { if (published) writer.LoseHandleForTest(); else throw new IOException("Pre-install interruption"); }
            };
            Assert.ThrowsAny<IOException>(() => mutation.Commit());
            Assert.Empty(Directory.GetFiles(repo.DirectoryPath, "*.ready", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(repo.DirectoryPath, "*.bound", SearchOption.AllDirectories));
        }
        using var fresh = repo.OpenWriter(f.Local);
        Assert.Equal(published ? PublicationOutcome.Published : PublicationOutcome.Unpublished, fresh.Reconcile(transaction));
        using var ready = fresh.FindReusablePayload(ReuseFixture.PathKey, 5, FoundationTests.Time);
        Assert.Equal(!published, ready is not null);
        output.WriteLine("SMB_CASE M17 reconcile published=" + published);
    }

    [SmbTheory]
    [InlineData(false)] [InlineData(true)]
    public void RestoreAcrossSmbPreservesContentMetadataAndUnrelatedFiles(bool remoteRepository)
    {
        using var f = new SmbTestEnvironment();
        var repo = Repository.Create(Path.Combine(remoteRepository ? f.Remote : f.Local, "repository"));
        var source = Path.Combine(f.Local, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "foo.txt"), "hello-日本語");
        File.SetAttributes(Path.Combine(source, "foo.txt"), FileAttributes.Hidden | FileAttributes.Archive);
        new FileHistoryEngine(repo, new(WorkspaceRoot: Path.Combine(f.Local, "work"))).Scan([new(new(FoundationTests.Root, source), source)]);
        using var reader = repo.OpenReader(); var selected = reader.EnumerateCurrent().Single();
        var target = Path.Combine(remoteRepository ? f.Local : f.Remote, "restore"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "unrelated"), "keep");
        var result = new RestoreEngine(repo).RestoreFile(selected.Identity, Path.Combine(target, "foo.txt"));
        Assert.Equal(1, result.Restored); Assert.Equal(1, result.ChecksumsVerified);
        Assert.Equal("hello-日本語", File.ReadAllText(Path.Combine(target, "foo.txt")));
        Assert.Equal(selected.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(target, "foo.txt")));
        Assert.Equal(selected.Metadata.Attributes, File.GetAttributes(Path.Combine(target, "foo.txt")) & LogicalFileMetadata.SupportedAttributes);
        var creationDeltaTicks = File.GetCreationTimeUtc(Path.Combine(target, "foo.txt")).Ticks - selected.Metadata.CreationTimeUtc.Ticks;
        Assert.InRange(Math.Abs(creationDeltaTicks), 0, 10); // NTFS exact; Samba may round to filesystem microseconds.
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "unrelated")));
        Assert.Empty(Directory.GetFiles(target, ".fluff-restore-*.tmp"));
        output.WriteLine("SMB_CASE restore remoteRepository=" + remoteRepository + " creationDeltaTicks=" + creationDeltaTicks + " " + JsonSerializer.Serialize(result));
    }

    [SmbFact]
    public void ReaderRebuildCompactionRetirementAndM18EpochExpiry()
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        using (var writer = repo.OpenWriter(f.Local))
        {
            writer.Apply(new(sourceRoots: [new(FoundationTests.Root, "Source")]), repo.ReadAuthority().Identity);
            writer.Apply(new([FoundationTests.Prepare(writer, "foo.txt")]), repo.ReadAuthority().Identity);
        }
        using var held = repo.OpenReader(); var scope = new HistoryScope(FoundationTests.Root);
        Assert.NotNull(held.GetCurrent(new(FoundationTests.Root, "foo.txt"))); // Pin an actual metadata handle; the timeline's own session closes.
        Assert.Single(held.GetTimelinePage(scope).Items);
        using var mutation = repo.OpenWriter(f.Local);
        mutation.Apply(new(sourceRoots: [new(FoundationTests.Root, "Updated")]), repo.ReadAuthority().Identity);
        Assert.Single(held.GetTimelinePage(scope).Items);
        mutation.RebuildIndexes(); Assert.Single(held.GetTimelinePage(scope).Items);
        mutation.CompactMetadata();
        Assert.Throws<ReaderGenerationExpiredException>(() => held.GetTimelinePage(scope));
        var first = mutation.CleanupRetiredMetadata(); held.Dispose(); var second = mutation.CleanupRetiredMetadata();
        Assert.True(first.DeferredSegments > 0); Assert.True(second.DeletedSegments > 0);
        using var fresh = repo.OpenReader(); Assert.Single(fresh.GetTimelinePage(scope).Items);
        output.WriteLine("SMB_CASE retirement " + JsonSerializer.Serialize(new { First = first, Second = second }));
    }

    [SmbFact]
    public void ObserveActualShareCapacityGeometryAndWriteDeleteFeedback()
    {
        using var f = new SmbTestEnvironment(); var repo = Repository.Create(Path.Combine(f.Remote, "repository"));
        var before = repo.ObserveCapacity();
        AllocationGeometry? geometry = null; string? error = null;
        try { geometry = repo.ObserveAllocationGeometry(f.Local); } catch (IOException e) { error = e.ToString(); }
        if (SmbNetworkMeasurementTests.After)
        {
            Assert.Null(error); Assert.NotNull(geometry);
            Assert.Throws<NotSupportedException>(() => repo.ObserveAllocationGeometry(Path.Combine(f.Remote, "remote-workspace")));
        }
        var path = Path.Combine(f.Remote, "allocation.bin");
        using (var file = File.Create(path))
        { var buffer = new byte[1024 * 1024]; for (var i = 0; i < 32; i++) file.Write(buffer); file.Flush(true); }
        var written = repo.ObserveCapacity(); File.Delete(path); var deleted = repo.ObserveCapacity();
        Assert.True(written.Free < before.Free);
        Assert.True(deleted.Free > written.Free);
        output.WriteLine("SMB_CAPACITY " + JsonSerializer.Serialize(new { Before = before, Written = written, Deleted = deleted, Geometry = geometry, Error = error }));
    }
}
