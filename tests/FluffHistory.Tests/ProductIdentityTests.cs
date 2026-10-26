using System.Text.Json;
using FluffHistory.Core;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ProductIdentityTests
{
    [Fact]
    public void NewRepositoryHasFluffHistoryHeaderAndExplicitFormat4()
    {
        using var temp = new TemporaryDirectory();
        var repository = Repository.Create(temp.Child());
        using var header = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temp.Child(), "repository.json")));
        Assert.Equal("Fluff-History", header.RootElement.GetProperty("product").GetString());
        Assert.Equal(4, header.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(repository.Identity, Repository.Discover(temp.Child())!.Identity);
        Assert.Equal(repository.Identity, Repository.Open(temp.Child()).Identity);
    }

    [Theory]
    [InlineData("Fluff-Backup")]
    [InlineData("fluff-history")]
    [InlineData("Other")]
    public void ForeignProductIsRejectedWithoutRelabelingOrMutation(string product)
    {
        using var temp = new TemporaryDirectory();
        Repository.Create(temp.Child());
        var path = Path.Combine(temp.Child(), "repository.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Fluff-History", product, StringComparison.Ordinal));
        var before = TemporaryDirectory.Snapshot(temp.Child());
        Assert.Throws<InvalidDataException>(() => Repository.Discover(temp.Child())!.Identity);
        Assert.Throws<InvalidDataException>(() => Repository.Open(temp.Child()));
        Assert.Equal(before, TemporaryDirectory.Snapshot(temp.Child()));
    }

    [Theory]
    [InlineData("ef46db3751d8e999", "EF46DB3751D8E999")]
    [InlineData("0000000000000001", "0000000000000001")]
    public void ChecksumSurvivesHistoryCodecWithoutLosingCaseOrLeadingZeroes(string value, string expected)
    {
        using var temp = new TemporaryDirectory();
        var repository = Repository.Create(temp.Child());
        using (var writer = repository.OpenWriter())
        using (var reader = repository.OpenReader())
        {
            var version = writer.PrepareVersion(new(FoundationTests.Root, "file", 0,
                FoundationTests.Time, FoundationTests.Time, 0), FoundationTests.Time, new(value));
            var path = writer.GetPreparedPayloadPath(version);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            writer.Apply(new([version], sourceRoots: [new(FoundationTests.Root, "Source")]), reader.Authority);
        }
        using var reopened = Repository.Open(temp.Child()).OpenReader();
        Assert.Equal(expected, reopened.GetCurrent(new(FoundationTests.Root, "file"))!.Checksum.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("EF46DB3751D8E99")]
    [InlineData("0EF46DB3751D8E999")]
    [InlineData("GF46DB3751D8E999")]
    [InlineData(" EF46DB3751D8E99")]
    public void InvalidChecksumIsRejected(string value) => Assert.Throws<ArgumentException>(() => new XxHash64Checksum(value));
}
