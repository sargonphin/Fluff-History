using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FluffHistory.Tests;

public sealed class SmbScaleProjectionTests(ITestOutputHelper output)
{
    internal sealed record Projection(int Paths, long Survivors, int PathCharacters, int CurrentKeyBytes, int VersionKeyBytes,
        int CurrentEntriesPerPage, int VersionEntriesPerPage, long CurrentPages, long VersionPages, int CurrentDepth, int VersionDepth,
        long SemanticBytes, long IndexBytes, long StructureBeforeHeadersSeals, long DenseLowerBytes, long DenseUpperBytes,
        long SegmentsLower, long SegmentsUpper, long FourSegmentArchives, long EightSegmentArchives,
        double Ideal100MbitSeconds, double Ideal1GbitSeconds, long LocalRawInputPlusOutputLowerBytes);
    internal static Projection Project(int paths, long survivors, int retiredSegments)
    {
        var name = SmbDiagnosticFixture.Name(0); var path = new HistoryPath(FoundationTests.Root, name);
        var pointer = new FrameAddress(Guid.NewGuid(), 1, 160, 1, FrameKind.Semantic, 1, new string('A', 64));
        var version = new StoredVersion(new(path, 1, 0, Guid.NewGuid()),
            new(FoundationTests.Root, name, 4096, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
            FoundationTests.Time, new("EF46DB3751D8E999"));
        var versionSize = Format.FrameHeaderBytes + MetadataCodec.EncodeSemantic(new(SemanticKind.VersionAdded, pointer, Version: version)).Length;
        var currentSize = Format.FrameHeaderBytes + MetadataCodec.EncodeSemantic(new(SemanticKind.CurrentUpsert, pointer, Path: path, VersionAddress: pointer)).Length;
        var sourceSize = Format.FrameHeaderBytes + MetadataCodec.EncodeSemantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source"))).Length;
        var currentKey = PathKeys.Current(path).Length; var versionKey = PathKeys.Version(version.Identity).Length;
        var currentCapacity = Math.Min(Format.MaximumPageEntries, (Format.PageBytes - 4) / (2 + currentKey + Format.AddressBytes));
        var versionCapacity = Math.Min(Format.MaximumPageEntries, (Format.PageBytes - 4) / (2 + versionKey + Format.AddressBytes));
        var (currentPages, currentDepth) = Pages(paths, currentCapacity); var (versionPages, versionDepth) = Pages(survivors, versionCapacity);
        var index = checked((currentPages + versionPages + 1) * (Format.PageBytes + Format.FrameHeaderBytes));
        var semantic = checked(survivors * versionSize + (long)paths * currentSize + sourceSize);
        var root = new MetadataRoot(1, 1, Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), new string('0', 64),
            pointer, pointer with { Kind = FrameKind.Page }, pointer with { Kind = FrameKind.Page }, pointer with { Kind = FrameKind.Page },
            retiredSegments == 0 ? null : pointer with { Kind = FrameKind.Retired });
        var retirement = retiredSegments == 0 ? 0 : checked(137L + (retiredSegments - 1L) * (137 + Format.AddressBytes));
        var structure = retirement + MetadataCodec.EncodeRoot(root).Length + Format.FrameHeaderBytes;
        var framed = semantic + index + structure;
        var lowerSegments = Divide(framed, Format.MaximumSegmentBytes - Format.SegmentHeaderBytes - Format.SealBytes);
        // Largest emitted frame is a padded index page; bound unusable tail space at each rotation.
        var upperSegments = Divide(framed, Format.MaximumSegmentBytes - Format.SegmentHeaderBytes - Format.SealBytes - Format.PageBytes - Format.FrameHeaderBytes);
        var lower = framed + lowerSegments * Format.SegmentHeaderBytes + Math.Max(0, lowerSegments - 1) * Format.SealBytes;
        var upper = framed + upperSegments * Format.SegmentHeaderBytes + Math.Max(0, upperSegments - 1) * Format.SealBytes;
        return new(paths, survivors, name.Length, currentKey, versionKey, currentCapacity, versionCapacity, currentPages, versionPages,
            currentDepth, versionDepth, semantic, index, structure, lower, upper, lowerSegments, upperSegments,
            Divide(upperSegments, 4), Divide(upperSegments, 8), upper / 12_500_000.0, upper / 125_000_000.0, checked(2 * upper));
        static (long Pages, int Depth) Pages(long entries, int capacity)
        {
            long total = 0; var depth = 0;
            do { entries = Divide(entries, capacity); total += entries; depth++; } while (entries > 1);
            return (total, depth);
        }
        static long Divide(long value, long divisor) => value / divisor + (value % divisor == 0 ? 0 : 1);
    }

    [Fact]
    public void CodecAndPageProjectionMatchesSmallPhysicalDenseRepository()
    {
        using var temp = new TemporaryDirectory(); var repository = Repository.Create(temp.Child());
        SmbDiagnosticFixture.Populate(repository, 31, 263);
        var before = SmbNetworkMeasurementTests.Inventory(repository.DirectoryPath);
        using (var writer = repository.OpenWriter()) { writer.CompactMetadata(); writer.CleanupRetiredMetadata(); }
        var actual = MetadataFrameAccounting.Capture(repository); var projection = Project(31, 263, before.Segments);
        Assert.Equal(actual.SemanticBytes, projection.SemanticBytes); Assert.Equal(actual.PageBytes, projection.IndexBytes);
        Assert.InRange(actual.LogicalBytes, projection.DenseLowerBytes, projection.DenseUpperBytes);
    }

    [MetadataDiagnosticFact, Trait("Category", "MetadataDiagnostic"), Trait("Category", "M19Diagnostic")]
    public void ProjectStressWithoutConstructingFiveMillionVersions()
    {
        var model = Project(250_000, 5_000_000, 0);
        new SmbNetworkMeasurementTests(output).Save("stress-projection", new { Model = model,
            Notes = "Projection for the fixed network path shape. No physical 5M repository. Excludes unknown legacy retired-segment identities, pending payload cleanup anchors and dirty COW data; add these independently. Compression uses separate measured clean Baseline/Heavy ratios. Local storage lower model is raw input plus equal-size output, excluding sort scratch and transient archives." });
    }
}
