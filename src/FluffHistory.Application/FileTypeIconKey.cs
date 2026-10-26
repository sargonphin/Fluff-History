namespace FluffHistory.Application;

/// <summary>Presentation-only type key. Never a source path or a request to open file content.</summary>
public static class FileTypeIconKey
{
    public static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || name.Length - dot > 32) return "";
        var extension = name[dot..];
        return extension.Length > 1 && extension[1..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? extension.ToLowerInvariant() : "";
    }
}
