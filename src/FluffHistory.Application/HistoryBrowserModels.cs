using System.Collections.Immutable;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;

namespace FluffHistory.Application;

/// <summary>In-process handles bound to one capture. Never serialize in settings.</summary>
public sealed class BrowserVersion
{
    internal VersionIdentity Identity { get; }
    internal AuthorityIdentity Authority { get; }
    internal BrowserVersion(StoredVersion version, AuthorityIdentity authority, bool isCurrent = false)
    { Identity = version.Identity; Authority = authority; RelativePath = version.Metadata.RelativePath;
      Length = version.Metadata.Length; StoredUtc = version.StoredUtc; LastWriteUtc = version.Metadata.LastWriteTimeUtc; IsCurrent = isCurrent; }
    public bool IsCurrent { get; }
    public Guid SourceRootId => Identity.Path.SourceRootId;
    public string RelativePath { get; }
    public long Length { get; }
    public DateTimeOffset StoredUtc { get; }
    public DateTimeOffset LastWriteUtc { get; }
    public override string ToString() => $"{RelativePath} · {Length:N0} bytes · {StoredUtc.ToLocalTime():g}";
}
public sealed class BrowserContinuation
{
    internal Guid Capture { get; init; }
    internal string Query { get; init; } = "";
    internal HistoryCursor? Cursor { get; init; }
    internal ImmutableArray<ScopeHistoryItem> Pending { get; init; } = [];
    internal bool End { get; init; }
    internal string? LastFolder { get; init; }
    internal BrowserContinuation() { }
}
public sealed record BrowserPage<T>(ImmutableArray<T> Items, BrowserContinuation? Continuation);
public sealed record BrowserScope(Guid SourceRootId, string Folder = "", DateTimeOffset? AtUtc = null);
public sealed class BrowserSelection
{
    internal Guid Capture { get; }
    internal AuthorityIdentity Authority { get; }
    internal DisplayedRestoreRequest Request { get; }
    internal BrowserSelection(DisplayedRestoreRequest request, Guid capture = default) { Request = request; Authority = request.Authority; Capture = capture; }
}
public sealed class BrowserChild
{
    internal BrowserChild(string name, bool isFolder, bool hasConflict, BrowserVersion? version, BrowserSelection selection)
    { Name = name; IsFolder = isFolder; HasConflict = hasConflict; Version = version; Selection = selection; }
    public string Name { get; }
    public bool IsFolder { get; }
    public bool HasConflict { get; }
    public BrowserVersion? Version { get; }
    public BrowserSelection Selection { get; }
    public override string ToString() => $"{(IsFolder ? "Folder" : "File")}  {Name}{(HasConflict ? "  [historical conflict]" : "")}" +
        (Version is null ? "" : $"  · {Version.Length:N0} bytes · {Version.StoredUtc.ToLocalTime():g}");
}
public sealed record BrowserState(Guid RepositoryId, Guid CaptureId, ImmutableArray<SourceRootConfiguration> Sources);
