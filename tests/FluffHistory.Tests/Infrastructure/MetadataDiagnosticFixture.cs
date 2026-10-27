using FluffHistory.Core;
using FluffHistory.Storage;

namespace FluffHistory.Tests.Infrastructure;

// Metadata-only test repositories: no payloads for the initial two populations.
// This preserves the 250k investigation's paths, shuffle, batches and default rotation.
internal static class MetadataDiagnosticFixture
{
    private const int BatchSize = 4096;
    internal static string Name(int i) => $"Documents/folder{i / 1000:D3}/document{i:D6}.txt";

    internal static void Populate(Repository repo, int fileCount, Action<int>? afterPopulation = null, long rotation = Format.MaximumSegmentBytes,
        Func<int, int, DateTimeOffset>? storedTime = null, Func<int, string>? name = null)
    {
        // Test-only fixture construction: valid semantic chain, both indexes and roots.
        // First pass is an ordered initial population; second pass represents later changes
        // whose chronology is independent of path order. No padding or reduced rotation size.
        using var ownership = repo.OpenWriter();
        using var initial = repo.OpenReader();
        using var append = new SegmentAppender(repo.DirectoryPath, repo.Identity, initial.Captured.Root.Epoch, initial.Captured, rotation);
        byte[] Read(FrameAddress p) => append.ReadNew(p) ?? initial.Read(p);
        var current = new OrderedIndex(IndexKind.Current, Read, append);
        var versions = new OrderedIndex(IndexKind.Versions, Read, append);
        var sources = new OrderedIndex(IndexKind.Sources, Read, append);
        FrameAddress? journal = null, currentRoot = null, versionRoot = null;
        FrameAddress Semantic(SemanticRecord record)
        {
            journal = append.Append(FrameKind.Semantic, MetadataCodec.EncodeSemantic(record with { Previous = journal }));
            return journal;
        }
        var source = Semantic(new(SemanticKind.SourceRoot, null, Source: new(FoundationTests.Root, "Source")));
        var sourceRoot = sources.Update(null, [(PathKeys.Source(FoundationTests.Root), source)]);
        var prior = initial.Captured;
        // Publish configuration separately so every subsequent 4096-version batch stays
        // within MutationBatch.MaximumChanges, including the first population batch.
        var configured = new MetadataRoot(1, 1, Guid.NewGuid(), prior.Root.Epoch, 0,
            prior.Root.TransactionId, prior.Address.Sha256, journal, null, null, sourceRoot, null);
        var configuredAddress = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(configured));
        append.FlushAndValidate();
        var authority = new Authority(repo.Identity, configured, configuredAddress);
        AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(authority));
        AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
        var order = Enumerable.Range(0, fileCount).ToArray();
        for (var pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                // Explicit fixed PRNG makes the input order repeatable across runtime versions.
                uint seed = 0x13579bdf;
                for (var i = order.Length - 1; i > 0; i--)
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    var j = (int)(seed % (uint)(i + 1));
                    (order[i], order[j]) = (order[j], order[i]);
                }
            }
            for (var start = 0; start < fileCount; start += BatchSize)
            {
                var sequence = authority.Root.Sequence + 1;
                var currentChanges = new List<(byte[], FrameAddress?)>();
                var versionChanges = new List<(byte[], FrameAddress?)>();
                for (var i = start; i < Math.Min(start + BatchSize, fileCount); i++)
                {
                    var path = new HistoryPath(FoundationTests.Root, name?.Invoke(order[i]) ?? Name(order[i]));
                    var time = FoundationTests.Time.AddDays(pass).AddMinutes(sequence);
                    var version = new StoredVersion(new(path, sequence, i - start, Guid.NewGuid()),
                        new(FoundationTests.Root, name?.Invoke(order[i]) ?? Name(order[i]), 4096, FoundationTests.Time, time, FileAttributes.Archive),
                        storedTime?.Invoke(pass, order[i]) ?? time, new("EF46DB3751D8E999"));
                    var pointer = Semantic(new(SemanticKind.VersionAdded, null, Version: version));
                    Semantic(new(SemanticKind.CurrentUpsert, null, Path: path, VersionAddress: pointer));
                    currentChanges.Add((PathKeys.Current(path), pointer));
                    versionChanges.Add((PathKeys.Version(version.Identity), pointer));
                }
                currentRoot = current.Update(currentRoot, currentChanges);
                versionRoot = versions.Update(versionRoot, versionChanges);
                var root = new MetadataRoot(authority.Root.Generation + 1, sequence, Guid.NewGuid(), authority.Root.Epoch,
                    authority.Root.Generation, authority.Root.TransactionId, authority.Address.Sha256,
                    journal, currentRoot, versionRoot, sourceRoot, null);
                var address = append.Append(FrameKind.Root, MetadataCodec.EncodeRoot(root));
                append.FlushAndValidate();
                authority = new(repo.Identity, root, address);
                AuthorityFiles.WriteNew(Path.Combine(repo.DirectoryPath, "fixture.tmp"), AuthorityFiles.StateBytes(authority));
                AuthorityFiles.Replace(repo.DirectoryPath, "fixture.tmp");
            }
            afterPopulation?.Invoke(pass + 1);
        }
    }
}
