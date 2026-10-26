namespace FluffHistory.Application;

/// <summary>Component-aware Windows path comparisons, also testable on a portable host. No filesystem identity claims.</summary>
public static class SourcePaths
{
    public const string OverlapMessage = "A folder inside the backup location, or a folder containing the backup location, cannot be backed up.";
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32760 || path.Any(char.IsControl)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.AbsolutePathRequired, "Choose an absolute folder path.");
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':') path = path[4..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.OrdinaryPathRequired, "Choose an ordinary drive or network folder.");
        var windows = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/') || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        if (!windows)
        {
            if (!Path.IsPathFullyQualified(path)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.AbsolutePathRequired, "Choose an absolute folder path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        path = path.Replace('/', '\\');
        string root; string tail;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Take(2).Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.CompleteShareRequired, "Choose a complete network share path.");
            root = @"\\" + parts[0] + "\\" + parts[1] + "\\"; tail = string.Join('\\', parts.Skip(2));
        }
        else { root = char.ToUpperInvariant(path[0]) + @":\"; tail = path[3..]; }
        var components = new List<string>();
        foreach (var part in tail.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (components.Count == 0) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.AmbiguousPath, "Path escapes its root."); components.RemoveAt(components.Count - 1); continue; }
            if (part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.AmbiguousPath, "Ambiguous folder path.");
            components.Add(part);
        }
        return root + string.Join('\\', components);
    }
    public static bool Same(string? a, string? b) => a is not null && b is not null && StringComparer.OrdinalIgnoreCase.Equals(Normalize(a), Normalize(b));
    public static bool Within(string path, string parent)
    {
        var p = Normalize(path); var r = Normalize(parent);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r.TrimEnd('\\', '/') + (r.Contains('\\') ? "\\" : "/"), StringComparison.OrdinalIgnoreCase);
    }
    public static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);
    public static string Relative(string parent, string child)
    {
        var p = Normalize(parent); var c = Normalize(child);
        if (Same(p, c) || !Within(c, p)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.AmbiguousPath, "An exact child folder is required.");
        return c[(p.TrimEnd('\\', '/').Length + 1)..].Replace('\\', '/');
    }
}
