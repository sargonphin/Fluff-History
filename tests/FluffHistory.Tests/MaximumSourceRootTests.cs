using System.IO.Hashing;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class MaximumSourceRootTests
{
    [Theory]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("eeffffff-ffff-ffff-ffff-ffffffffffff")]
    public void WriterValidationAndBothFilteredCurrentReadersAcceptFinalRoot(string rootText)
    {
        var root = Guid.Parse(rootText); using var temp = new TemporaryDirectory(); using var repository = Repository.Create(temp.Child());
        using (var writer = repository.OpenWriter())
        using (var reader = repository.OpenReader())
        using (var draft = writer.CreatePayloadDraft())
        {
            var bytes = "boundary data"u8.ToArray(); draft.Write(bytes); draft.CompleteCopy(bytes.Length);
            var version = draft.PrepareVersion(new(root, "Folder/leaf.txt", bytes.Length, FoundationTests.Time, FoundationTests.Time, FileAttributes.Archive),
                FoundationTests.Time, new(Convert.ToHexString(XxHash64.Hash(bytes))));
            using var prepared = writer.PrepareMutation(new([version], sourceRoots: [new(root, "boundary-root")]), reader.Authority);
            draft.Install(); prepared.Commit();
        }
        using var current = repository.OpenReader();
        Assert.Single(current.EnumerateCurrent(root)); Assert.Single(current.EnumerateCurrentComparison(root));
        Assert.Single(current.EnumerateCurrent(root, "Folder")); Assert.Single(current.EnumerateCurrentComparison(root, "Folder"));
        Assert.Empty(current.EnumerateCurrent(Guid.NewGuid()));
        using var validate = repository.OpenWriter(); // Full writer configuration validation uses the same filtered range.
    }
}
