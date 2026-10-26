using FluffHistory.Core;
using Xunit;

namespace FluffHistory.Tests;

public sealed class LogicalFileMetadataTests
{
    private static readonly Guid RootId = Guid.Parse("8720ba1f-413c-413e-bd17-055d9555395b");
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("a/../outside")]
    [InlineData("a/./file")]
    [InlineData("a//file")]
    [InlineData("a/")]
    [InlineData("C:/absolute")]
    [InlineData("a\\file")]
    [InlineData("a/\0file")]
    [InlineData("a/file.")]
    [InlineData("a/file ")]
    [InlineData("a/NUL.txt")]
    [InlineData("a/file?name")]
    public void UnsafeOrNonNormalizedLogicalPathsAreRefused(string path)
    {
        Assert.Throws<ArgumentException>(() => Create(path: path));
    }

    [Fact]
    public void NegativeLengthAndUnsupportedAttributesAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(length: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(attributes: FileAttributes.ReparsePoint));
    }

    private static LogicalFileMetadata Create(
        long length = 10,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null,
        FileAttributes attributes = 0,
        string path = "Documents/report.txt",
        Guid? rootId = null) =>
        new(rootId ?? RootId, path, length, created ?? Timestamp, modified ?? Timestamp, attributes);
}
