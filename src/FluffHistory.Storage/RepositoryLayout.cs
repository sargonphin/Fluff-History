namespace FluffHistory.Storage;

internal static class RepositoryLayout
{
    internal const string WriterLock = "writer.lock";

    internal static string NormalizeDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        // Do not traverse repository paths through symlinks/junctions, including ancestor aliases.
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            _ = IsExistingDirectory(current);
        }

        return fullPath;
    }

    internal static bool IsExistingDirectory(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        RejectReparsePoint(path, attributes);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"A directory was required at '{path}'.");
        }

        return true;
    }

    internal static void RequireFile(string path)
    {
        var attributes = File.GetAttributes(path);
        RejectReparsePoint(path, attributes);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"A regular repository file was required at '{path}'.");
        }
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Repository paths must not be symlinks or reparse points: '{path}'.");
        }
    }
}
