using FluffHistory.Core;

namespace FluffHistory.Application;

public enum ShellItemKind { File, Folder }
public sealed record ShellPathTarget(Guid SourceRootId, string RelativePath, string RuntimeRoot, bool IsActive, ShellItemKind Kind);

/// <summary>Lexical live mapping only. OriginalPath is descriptive and never participates.
/// A nested identity is accepted only when every covering parent explicitly excludes it.</summary>
public static class ShellPathResolver
{
    public static ShellPathTarget? Resolve(AppSettings settings, string livePath, ShellItemKind kind)
    {
        if (!Enum.IsDefined(kind)) return null;
        try
        {
            var path = SourcePaths.Normalize(livePath);
            var covering = FolderExclusions.Apply(settings.Sources, settings.ExcludedFolders).Where(s => s.RuntimePath is not null && SourcePaths.Within(path, s.RuntimePath)).ToArray();
            var eligible = covering.Where(s => !Excluded(s, path)).ToArray();
            // Do not reinterpret an excluded/inactive child as part of a broader identity.
            if (eligible.Length != 1) return null;
            var mapping = eligible[0];
            if (covering.Any(s => s.SourceRootId != mapping.SourceRootId &&
                (!SourcePaths.Within(mapping.RuntimePath!, s.RuntimePath!) || !Excluded(s, mapping.RuntimePath!)))) return null;
            var relative = SourcePaths.Same(path, mapping.RuntimePath) ? "" : SourcePaths.Relative(mapping.RuntimePath!, path);
            if (relative.Length == 0 && kind == ShellItemKind.File) return null;
            if (relative.Length != 0) _ = new HistoryPath(mapping.SourceRootId, relative);
            return new(mapping.SourceRootId, relative, SourcePaths.Normalize(mapping.RuntimePath!), mapping.IsProtected, kind);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException) { return null; }
    }
    private static bool Excluded(SourceMapping source, string path)
    {
        if (SourcePaths.Same(source.RuntimePath, path)) return false;
        var relative = new HistoryPath(source.SourceRootId, SourcePaths.Relative(source.RuntimePath!, path)).CanonicalPath;
        return source.ToConfiguration().ExcludedDirectories.Any(e => relative == e || relative.StartsWith(e + "/", StringComparison.Ordinal));
    }
}
