using System.Collections.Immutable;
using System.Text;
using FluffHistory.Core;

namespace FluffHistory.Application;

/// <summary>Independent application folder choices, projected into existing relative source exclusions.
/// No filesystem access, repository authority, or separate engine exclusion mechanism.</summary>
public static class FolderExclusions
{
    public const string ConflictMessage = "A folder selected for backup cannot also be excluded. Remove that folder from backup or change the exclusion.";
    private const string LimitMessage = "Choose at most 256 excluded folders, with at most 256 KiB of folder paths.";

    public static ImmutableArray<string> Normalize(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var choices = folders.Take(257).ToArray();
        if (choices.Length > 256 || choices.Any(p => p is null)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.ExclusionLimit, LimitMessage);
        var paths = choices.Select(SourcePaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Sum(p => (long)Encoding.UTF8.GetByteCount(p) + 4) > 256 * 1024) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.ExclusionLimit, LimitMessage);
        return paths.Where(path => !paths.Any(parent => !SourcePaths.Same(path, parent) && SourcePaths.Within(path, parent))).ToImmutableArray();
    }

    public static ImmutableArray<string> Add(ImmutableArray<string> folders, string path)
    {
        path = SourcePaths.Normalize(path);
        if (folders.Any(parent => SourcePaths.Within(path, parent))) return Normalize(folders);
        return Normalize(folders.Where(child => !SourcePaths.Within(child, path)).Append(path));
    }

    public static ImmutableArray<string> Remove(ImmutableArray<string> folders, string path)
        => folders.Where(p => !SourcePaths.Same(p, path)).ToImmutableArray();

    public static void ValidateSource(string sourcePath, IEnumerable<string> folders)
    {
        if (folders.Any(folder => SourcePaths.Within(sourcePath, folder))) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.ExclusionConflict, ConflictMessage);
    }

    public static ImmutableArray<SourceMapping> Apply(ImmutableArray<SourceMapping> sources, ImmutableArray<string> folders)
    {
        if (folders.IsDefault) throw new InvalidDataException("Invalid excluded folder list.");
        var choices = Normalize(folders);
        return sources.Select(source =>
        {
            if (!source.IsProtected || source.RuntimePath is null) return source;
            ValidateSource(source.RuntimePath, choices);
            var relative = choices.Where(p => SourcePaths.Within(p, source.RuntimePath))
                .Select(p => new HistoryPath(source.SourceRootId, SourcePaths.Relative(source.RuntimePath, p)).CanonicalPath);
            var updated = source with { ExcludedDirectories = source.ExcludedDirectories.Concat(relative)
                .Select(p => new HistoryPath(source.SourceRootId, p).CanonicalPath)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray() };
            try { _ = updated.ToConfiguration(); }
            catch (ArgumentException error) { throw ConfigurationIssues.Invalid(ConfigurationIssueCode.EffectiveExclusionLimit, "This source supports at most 256 effective excluded folders and 256 KiB of configuration. Choose fewer or shorter exclusions.", error); }
            return updated;
        }).ToImmutableArray();
    }

    // Earlier internal builds exposed relative choices per source. Present mapped choices in
    // the independent list on editing, without writing settings or changing dormant identities.
    // An old choice conflicting with a separately selected root stays visible for correction.
    public static AppSettings ForEditing(AppSettings settings)
    {
        var folders = settings.ExcludedFolders.ToList();
        var sources = settings.Sources.Select(source =>
        {
            if (source.RuntimePath is null) return source;
            var separator = source.RuntimePath.Contains('\\') ? '\\' : '/';
            folders.AddRange(source.ExcludedDirectories.Select(relative => SourcePaths.Normalize(
                source.RuntimePath.TrimEnd('\\', '/') + separator + relative.Replace('/', separator))));
            return source with { ExcludedDirectories = [] };
        }).ToImmutableArray();
        var choices = folders.Select(SourcePaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        // Retain oversized legacy lists for editing instead of crashing the management window.
        // Normal validation requires reducing them before saving the new independent list.
        try { choices = Normalize(choices); } catch (InvalidDataException) { }
        return settings with { Sources = sources, ExcludedFolders = choices };
    }
}
