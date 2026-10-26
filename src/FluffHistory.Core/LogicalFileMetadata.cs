namespace FluffHistory.Core;

public sealed record LogicalFileMetadata
{
    public const FileAttributes SupportedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    public LogicalFileMetadata(
        Guid sourceRootId,
        string relativePath,
        long length,
        DateTimeOffset creationTimeUtc,
        DateTimeOffset lastWriteTimeUtc,
        FileAttributes attributes)
    {
        ModelValidation.RequireIdentity(sourceRootId, nameof(sourceRootId));
        ModelValidation.RequireRelativePath(relativePath, nameof(relativePath));

        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ModelValidation.RequireUtc(creationTimeUtc, nameof(creationTimeUtc));
        ModelValidation.RequireUtc(lastWriteTimeUtc, nameof(lastWriteTimeUtc));
        if ((attributes & ~SupportedAttributes) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attributes), "Unsupported file attributes.");
        }

        SourceRootId = sourceRootId;
        RelativePath = relativePath;
        Length = length;
        CreationTimeUtc = creationTimeUtc;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Attributes = attributes;
    }

    public Guid SourceRootId { get; }
    public string RelativePath { get; }
    public long Length { get; }
    public DateTimeOffset CreationTimeUtc { get; }
    public DateTimeOffset LastWriteTimeUtc { get; }
    public FileAttributes Attributes { get; }

}
