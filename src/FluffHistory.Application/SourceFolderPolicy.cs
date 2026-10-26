using System.Collections.Immutable;
using FluffHistory.Core;
using FluffHistory.Engine;

namespace FluffHistory.Application;

public enum PersonalFolderKind { Folder, Desktop, Documents, Downloads, Pictures, Music, Videos }
public enum SourceSafetyKind { Ordinary, ApplicationData, System, SharedApplicationData, Temporary, HardBlocked }
public sealed record FolderPresentation(string Label, PersonalFolderKind Kind)
{
    public string Glyph => Kind switch { PersonalFolderKind.Desktop => "\uE977", PersonalFolderKind.Documents => "\uE8A5", PersonalFolderKind.Downloads => "\uE896",
        PersonalFolderKind.Pictures => "\uEB9F", PersonalFolderKind.Music => "\uE8D6", PersonalFolderKind.Videos => "\uE714", _ => "\uE8B7" };
}
public sealed record FolderSafety(SourceSafetyKind Kind)
{
    public bool RequiresConfirmation => Kind is not (SourceSafetyKind.Ordinary or SourceSafetyKind.HardBlocked);
    public string Warning => Kind switch
    {
        SourceSafetyKind.ApplicationData => "AppData contains application state, caches, databases and temporary files. It can change frequently and make the Timeline grow quickly. Restoring these files is not a substitute for reinstalling or correctly restoring an application.",
        SourceSafetyKind.System => "System and installed-program files are large and change frequently. Fluff-History is not a system-image backup. Restoring these files alone may not repair Windows or an installed application.",
        SourceSafetyKind.SharedApplicationData => "ProgramData can contain shared application state, caches and databases that change frequently. This may create a large amount of Timeline data.",
        SourceSafetyKind.Temporary => "This location mainly contains temporary or installation files and can create large, short-lived Timeline churn.",
        SourceSafetyKind.HardBlocked => "This location contains system metadata or Fluff-History working files and cannot be backed up.", _ => ""
    };
}
public sealed record SourceSafetyLocation(string Path, SourceSafetyKind Kind, bool File = false);
public interface ISourceFolderPolicy
{
    FolderPresentation Describe(string path);
    FolderSafety Classify(string path);
    ImmutableArray<SourceMapping> Prepare(string repository, IEnumerable<SourceMapping> sources, string? workspace = null);
    void ValidateAliases(string repository, IEnumerable<SourceMapping> sources, string? workspace = null);
    ImmutableArray<SourceSelection> Select(string repository, IEnumerable<SourceMapping> sources, string? workspace = null);
}

/// <summary>Pure policy over a Windows-resolved catalog. Classification never uses an arbitrary leaf name.</summary>
public class SourceFolderPolicy : ISourceFolderPolicy
{
    public static ISourceFolderPolicy Empty { get; } = new SourceFolderPolicy();
    private readonly (string Path, FolderPresentation Presentation)[] _personal;
    private readonly SourceSafetyLocation[] _locations;
    public SourceFolderPolicy(IEnumerable<(string Path, FolderPresentation Presentation)>? personal = null, IEnumerable<SourceSafetyLocation>? locations = null)
    { _personal = (personal ?? []).ToArray(); _locations = (locations ?? []).ToArray(); }
    public FolderPresentation Describe(string path)
    {
        try
        {
            var normalized = SourcePaths.Normalize(path);
            var leaf = normalized.TrimEnd('\\', '/').Split('\\', '/')[^1];
            return _personal.FirstOrDefault(p => SourcePaths.Same(p.Path, normalized)).Presentation
                ?? new(leaf.Length == 0 || leaf.EndsWith(':') ? normalized : leaf, PersonalFolderKind.Folder);
        }
        catch (InvalidDataException) { return new(path, PersonalFolderKind.Folder); }
    }
    public virtual FolderSafety Classify(string path)
    {
        var matched = _locations.Where(l => l.File ? SourcePaths.Same(path, l.Path) : SourcePaths.Within(path, l.Path)).OrderByDescending(l => l.Kind == SourceSafetyKind.HardBlocked).ThenByDescending(l => l.Path.Length).FirstOrDefault();
        return new(matched?.Kind ?? SourceSafetyKind.Ordinary);
    }
    public virtual void ValidateAliases(string repository, IEnumerable<SourceMapping> sources, string? workspace = null) { }
    public virtual ImmutableArray<SourceMapping> Prepare(string repository, IEnumerable<SourceMapping> sources, string? workspace = null)
    {
        var all = sources.Take(1025).ToArray(); if (all.Length > 1024) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderLimit, "Choose at most 1,024 folders.");
        var active = all.Where(s => s.IsProtected && s.RuntimePath is not null).ToArray();
        foreach (var s in active)
        {
            if (SourcePaths.Overlaps(s.RuntimePath!, repository)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.SourceRepositoryOverlap, SourcePaths.OverlapMessage);
            var safety = Classify(s.RuntimePath!);
            if (safety.Kind == SourceSafetyKind.HardBlocked || workspace is not null && SourcePaths.Within(s.RuntimePath!, workspace)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderBlocked, new FolderSafety(SourceSafetyKind.HardBlocked).Warning);
            if (safety.RequiresConfirmation && !s.ForceInclude) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.ConfirmWarning, "Confirm the warning before adding this folder.");
        }
        if (active.Select(s => SourcePaths.Normalize(s.RuntimePath!)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Length) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.DuplicateFolder, "A folder can only be selected once.");
        return all.Select(s =>
        {
            if (!s.IsProtected || s.RuntimePath is null) return s;
            var dirs = _locations.Where(l => !l.File && !SourcePaths.Same(l.Path, s.RuntimePath) && SourcePaths.Within(l.Path, s.RuntimePath))
                .Select(l => SourcePaths.Relative(s.RuntimePath, l.Path)).ToList();
            if (workspace is not null && SourcePaths.Within(workspace, s.RuntimePath)) dirs.Add(SourcePaths.Relative(s.RuntimePath, workspace));
            // Keep known child identities separate even after Remove from backup. Dropping this
            // barrier would silently start backing that folder up under its parent's different ID.
            dirs.AddRange(all.Where(child => child.RuntimePath is not null && child.SourceRootId != s.SourceRootId && SourcePaths.Within(child.RuntimePath, s.RuntimePath)).Select(child => SourcePaths.Relative(s.RuntimePath, child.RuntimePath!)));
            // A forced source deliberately traverses its discouraged location, including nested caches;
            // hard boundaries and independently selected children remain excluded.
            if (s.ForceInclude) dirs = dirs.Where(d => !_locations.Any(l => !l.File && l.Kind == Classify(s.RuntimePath).Kind && l.Kind != SourceSafetyKind.HardBlocked && SourcePaths.Same(l.Path, Combine(s.RuntimePath, d)) &&
                !all.Any(c => c.SourceRootId != s.SourceRootId && SourcePaths.Same(c.RuntimePath, l.Path)))).ToList();
            var normalized = dirs.Select(d => new HistoryPath(s.SourceRootId, d).CanonicalPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            return s with { AutomaticExcludedDirectories = normalized };
        }).ToImmutableArray();
    }
    public virtual ImmutableArray<SourceSelection> Select(string repository, IEnumerable<SourceMapping> sources, string? workspace = null)
        => Prepare(repository, sources, workspace).Where(s => s.RuntimePath is not null).Select(s =>
        {
            var files = _locations.Where(l => l.File && SourcePaths.Within(l.Path, s.RuntimePath!) && !SourcePaths.Same(l.Path, s.RuntimePath))
                .Select(l => SourcePaths.Relative(s.RuntimePath!, l.Path)).ToArray();
            return new SourceSelection(s.ToConfiguration(), s.RuntimePath!, files, s.AutomaticExcludedDirectories);
        }).ToImmutableArray();
    private static string Combine(string root, string relative) => root.TrimEnd('\\', '/') + (root.Contains('\\') ? "\\" : "/") + relative.Replace('/', root.Contains('\\') ? '\\' : '/');
}
