using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

[Collection("SMB diagnostics"), Trait("Category", "MetadataDiagnostic")]
public sealed class Format4SmbSmokeTests(ITestOutputHelper output)
{
    [SmbTheory] [InlineData(false)] [InlineData(true)]
    public void BackupReopenGroupedReadersAndExactUnpublishedReconciliation(bool encrypted)
    {
        using var f = new SmbTestEnvironment();
        var source = Path.Combine(f.Local, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "日本語.txt"), "original raw payload");
        File.WriteAllText(Path.Combine(source, "other.txt"), "second raw file");
        var repo = encrypted ? Repository.CreateEncrypted(Path.Combine(f.Remote, "repository"), EncryptionTests.Password) : Repository.Create(Path.Combine(f.Remote, "repository"));
        var engine = new FileHistoryEngine(repo, new(WorkspaceRoot: Path.Combine(f.Local, "work")));
        SourceSelection[] selection = [new(new(FoundationTests.Root, source), source)];
        Assert.Equal(2, engine.Scan(selection).VersionsCreated);
        File.WriteAllText(Path.Combine(source, "日本語.txt"), "changed raw payload bytes");
        Assert.Equal(1, engine.Scan(selection).VersionsCreated);
        var directory = repo.DirectoryPath; repo.Dispose();
        repo = encrypted ? Repository.OpenWithPassword(directory, EncryptionTests.Password) : Repository.Open(directory); Assert.Equal(4, RepositoryIdentity.FormatVersion);
        using (var reader = repo.OpenReader())
        {
            Assert.Equal(2, reader.EnumerateCurrentComparison().Count());
            Assert.True(reader.LastComparisonStatistics!.Io.BlocksDecoded > 0);
            Assert.NotEmpty(reader.GetTimelinePage(new(FoundationTests.Root)).Items);
            var versions = reader.EnumerateRetentionVersions().ToArray();
            Assert.Equal(3, versions.Length); Assert.Equal(2, versions.Count(v => v.IsCurrent));
            var current = reader.GetCurrent(new(FoundationTests.Root, "日本語.txt"))!;
            using var payload = reader.OpenVersionPayload(current.Identity)!; using var content = new StreamReader(payload.Content);
            Assert.Equal("changed raw payload bytes", content.ReadToEnd());
            if (!encrypted) Assert.Equal("changed raw payload bytes", File.ReadAllText(repo.GetPayloadPath(current.Identity.VersionId)));
            var target = Path.Combine(f.Local, "restored.txt"); Assert.Equal(1, new RestoreEngine(repo).RestoreFile(current.Identity, target).Restored);
            Assert.Equal("changed raw payload bytes", File.ReadAllText(target));
            output.WriteLine("Format4 SMB grouped physical reads: " + reader.LastComparisonStatistics.DestinationReadRequests);
        }
        var before = repo.ReadAuthority(); Guid transaction;
        using (var held = new FileStream(Path.Combine(repo.DirectoryPath, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var writer = repo.OpenWriter(Path.Combine(f.Local, "denied-work")))
        using (var prepared = writer.PrepareMutation(new(sourceRoots: [new(Guid.NewGuid(), "Independent source")]), before.Identity))
        {
            transaction = prepared.IntendedAuthority.TransactionId;
            Assert.ThrowsAny<IOException>(() => prepared.Commit());
            Assert.Equal(before, repo.ReadAuthority());
            Assert.Throws<InvalidOperationException>(() => writer.OpenOwnedReader());
        }
        using (var fresh = repo.OpenWriter(Path.Combine(f.Local, "reconcile")))
            Assert.Equal(PublicationOutcome.Unpublished, fresh.Reconcile(transaction));
        using var reopened = repo.OpenReader(); Assert.Equal(2, reopened.EnumerateCurrentComparison().Count());
        output.WriteLine($"Format4 SMB encrypted={encrypted}: create/backup/change/reopen, Current/Timeline/Retention, logical restore and native rename-denial reconciliation passed.");
    }
}
