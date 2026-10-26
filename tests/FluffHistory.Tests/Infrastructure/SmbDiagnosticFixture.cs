using System.Security.Cryptography;
using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Tests.Infrastructure;

// Physical metadata-only populations. Real ordinary publications are measured separately.
internal static class SmbDiagnosticFixture
{
    internal static string Name(int i) => $"Documents/area{i / 10000:D2}/folder{i / 100:D4}/document{i:D6}.txt";
    internal static void Populate(Repository repository, int paths, int survivors)
    {
        using var writer = repository.OpenWriter();
        using var initial = repository.OpenReader();
        using var append = new SegmentAppender(repository.DirectoryPath, repository.Identity,
            initial.Captured.Root.Epoch, initial.Captured, crypto: repository.Crypto);
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? initial.Read(p);
        var current = new OrderedIndex(IndexKind.Current, Read, append);
        var versions = new OrderedIndex(IndexKind.Versions, Read, append);
        var sources = new OrderedIndex(IndexKind.Sources, Read, append);
        FrameAddress? journal = null, currentRoot = null, versionsRoot = null;
        FrameAddress Add(SemanticRecord record) => journal = append.Append(FrameKind.Semantic,
            MetadataCodec.EncodeSemantic(record with { Previous = journal }));
        var source = Add(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source")));
        var sourceRoot = sources.Update(null, [(PathKeys.Source(FoundationTests.Root), source)]);
        var authority = initial.Captured;
        Publish();
        var order = Enumerable.Range(0, paths).ToArray();
        uint random = 0x13579bdf;
        var completed = 0;
        var pass = 0;
        while (completed < survivors)
        {
            if (pass++ > 0)
                for (var i = paths - 1; i > 0; i--)
                {
                    random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                    var j = (int)(random % (uint)(i + 1)); (order[i], order[j]) = (order[j], order[i]);
                }
            var population = Math.Min(paths, survivors - completed);
            for (var start = 0; start < population; start += 4096)
            {
                var changes = new List<(byte[], FrameAddress?)>();
                var history = new List<(byte[], FrameAddress?)>();
                var sequence = authority.Root.Sequence + 1;
                for (var i = start; i < Math.Min(start + 4096, population); i++)
                {
                    var path = new HistoryPath(FoundationTests.Root, Name(order[i]));
                    var time = FoundationTests.Time.AddMinutes(completed + i);
                    // Distinct actual checksum/UUID entropy; no optimistic constant-hash ZIP ratio.
                    var version = new StoredVersion(new(path, sequence, i - start, Guid.NewGuid()),
                        new(FoundationTests.Root, Name(order[i]), 4096, FoundationTests.Time, time, FileAttributes.Archive),
                        time, new(Convert.ToHexString(RandomNumberGenerator.GetBytes(8))));
                    var address = Add(new(SemanticKind.VersionAdded, null, Version: version));
                    Add(new(SemanticKind.CurrentUpsert, null, Path: path, VersionAddress: address));
                    changes.Add((PathKeys.Current(path), address)); history.Add((PathKeys.Version(version.Identity), address));
                }
                currentRoot = current.Update(currentRoot, changes); versionsRoot = versions.Update(versionsRoot, history);
                Publish();
            }
            completed += population;
        }
        void Publish()
        {
            var prior = authority.Root;
            var root = new MetadataRoot(prior.Generation + 1, prior.Sequence + 1, Guid.NewGuid(), prior.Epoch,
                prior.Generation, prior.TransactionId, authority.Address.Sha256, journal, currentRoot, versionsRoot, sourceRoot, null);
            var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root)); append.FlushAndValidate();
            authority = new(repository.Identity, root, address);
            AuthorityFiles.WriteNew(Path.Combine(repository.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(authority, repository.Crypto));
            AuthorityFiles.Replace(repository.DirectoryPath, "fixture.tmp");
        }
    }
}
