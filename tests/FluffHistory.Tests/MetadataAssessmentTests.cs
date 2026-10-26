using System.Security.Cryptography;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class MetadataAssessmentTests
{
    [Theory]
    [InlineData(29, false)] [InlineData(30, true)] [InlineData(31, true)]
    public void InclusiveThresholdUsesExactIntegerAccounting(int percent, bool eligible)
    {
        const long total = 1_000_000_000;
        Assert.Equal(eligible, MetadataMaintenancePolicy.Eligible(total, percent * (total / 100)));
    }

    [Fact]
    public void RatioAloneCannotCompactTinyRepositoriesAndFloorIsInclusive()
    {
        Assert.False(MetadataMaintenancePolicy.Eligible(1000, 999));
        var floor = MetadataMaintenancePolicy.MinimumReclaimableBytes;
        Assert.False(MetadataMaintenancePolicy.Eligible(floor * 2, floor - 1));
        Assert.True(MetadataMaintenancePolicy.Eligible(floor * 2, floor));
        Assert.False(MetadataMaintenancePolicy.Eligible(0, 0));
        Assert.False(MetadataMaintenancePolicy.Eligible(1, long.MaxValue));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AssessmentPredictsExactDenseDecodedOutputAndDoesNotMutate(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using var repo = encrypted ? Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password) : Repository.Create(temp.Child());
        var first = EncryptionTests.Add(repo, "folder/document.txt", [1, 2, 3]);
        EncryptionTests.Add(repo, "folder/document.txt", [4, 5]);
        EncryptionTests.Add(repo, "other.txt", [6]);
        using var writer = repo.OpenWriter();
        using (var reader = repo.OpenReader()) writer.RemoveVersions([first.Identity], reader.Authority);
        var before = Snapshot(repo); var authority = repo.ReadAuthority().Identity;
        var files = Inventory(repo);
        var assessment = writer.AssessMetadata();
        Assert.Equal(files, Inventory(repo));
        Assert.Equal(authority, assessment.Active.Authority);
        Assert.Equal(before, Snapshot(repo));
        writer.CompactMetadata();
        var io = writer.LastWriteStatistics!;
        Assert.True(io.PhysicalBytesWritten <= assessment.MaximumOutputBytes);
        Assert.Equal(assessment.DenseDecodedBytes,
            io.BytesWritten - io.SegmentsCreated * Format.SegmentHeaderBytes - (io.SegmentsCreated - 1) * Format.SealBytes);
        Assert.Equal(before, Snapshot(repo));
        Assert.Equal(assessment.DenseDecodedBytes, writer.InspectActiveMetadata().DecodedBytes);
        writer.CleanupRetiredMetadata();
        var fresh = writer.AssessMetadata();
        Assert.Equal(assessment.DenseDecodedBytes, fresh.Active.DecodedBytes);
        Assert.False(MetadataMaintenancePolicy.Eligible(fresh.Active.DecodedBytes, fresh.ReclaimableBytes));
        Assert.Equal(before, Snapshot(repo));
    }

    [Fact]
    public void AssessmentHonorsCancellationBudgetsAndUnresolvedCandidates()
    {
        using var temp = new TemporaryDirectory(); using var repo = Repository.Create(temp.Child());
        using var writer = repo.OpenWriter(); FoundationTests.Add(repo, writer);
        var authority = repo.ReadAuthority().Identity;
        Assert.Throws<OperationCanceledException>(() => writer.AssessMetadata(cancellationToken: new(true)));
        Assert.Throws<IOException>(() => writer.AssessMetadata(new(MaximumRecords: 1)));
        Assert.Equal(authority, repo.ReadAuthority().Identity);
        File.WriteAllText(Path.Combine(repo.DirectoryPath, "state.unresolved.tmp"), "unresolved");
        Assert.Throws<IOException>(() => writer.AssessMetadata());
        Assert.Throws<IOException>(() => writer.InspectActiveMetadata());
        Assert.Equal(authority, repo.ReadAuthority().Identity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OutputUpperBoundCoversRealForcedRotationAndAppenderReservation(bool encrypted)
    {
        using var temp = new TemporaryDirectory();
        using var repo = encrypted ? Repository.CreateEncrypted(temp.Child(), EncryptionTests.Password) : Repository.Create(temp.Child());
        for (var i = 0; i < 20; i++) EncryptionTests.Add(repo, $"folder/document-{i}.txt", [(byte)i]);
        var before = Snapshot(repo);
        using var writer = repo.OpenWriter(); writer.RotationBytes = 32 * 1024;
        var assessment = writer.AssessMetadata();
        writer.CompactMetadata(new(MaximumOutputBytes: assessment.MaximumOutputBytes));
        var io = writer.LastWriteStatistics!; Assert.True(io.SegmentsCreated > 1);
        Assert.True(io.PhysicalBytesWritten <= assessment.MaximumOutputBytes);
        Assert.Equal(assessment.DenseDecodedBytes,
            io.BytesWritten - io.SegmentsCreated * Format.SegmentHeaderBytes - (io.SegmentsCreated - 1) * Format.SealBytes);
        Assert.Equal(before, Snapshot(repo));
    }

    internal static string Snapshot(Repository repo)
    {
        using var reader = repo.OpenReader();
        var state = new
        {
            Sources = reader.EnumerateSourceRoots().ToArray(),
            Current = reader.EnumerateCurrent().ToArray(),
            Versions = reader.VersionIndex.Range(reader.Captured.Root.Versions).Select(e => reader.Version(e.Value)).ToArray(),
            Payloads = Directory.GetFiles(Path.Combine(repo.DirectoryPath, "versions"), "*.bin", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => new { Name = Path.GetFileName(p), Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray()
        };
        return System.Text.Json.JsonSerializer.Serialize(state);
    }

    private static string Inventory(Repository repo) => string.Join("\n", Directory.GetFiles(repo.DirectoryPath, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != "writer.lock").OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(repo.DirectoryPath, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
}
