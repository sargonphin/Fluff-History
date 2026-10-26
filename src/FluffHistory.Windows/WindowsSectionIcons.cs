using System.Runtime.InteropServices;

namespace FluffHistory.Windows;

/// <summary>Fixed configuration artwork from the installed Windows resources, loaded once.
/// Windows images are not redistributed as application assets.</summary>
public static class WindowsSectionIcons
{
    private static readonly Lazy<Task<IReadOnlyDictionary<string, byte[]>>> Cache = new(() => Task.Run(Load));
    public static Task<IReadOnlyDictionary<string, byte[]>> GetAsync() => Cache.Value;

    private static IReadOnlyDictionary<string, byte[]> Load()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Add("Drive", WindowsFolderIcons.ReadStock(8 /* SIID_DRIVEFIXED */));
        Add("Folders", Extract(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), 0));
        Add("ExcludedFolders", Resource("shell32.dll", 16715)); // Windows folder with red X.
        Add("AutomaticBackups", Extract(Path.Combine(Environment.SystemDirectory, "miguiresource.dll"), 1)); // Task Scheduler shortcut icon.
        Add("Advanced", Resource("shell32.dll", 16826) ?? Resource("imageres.dll", 114));
        return images;
        void Add(string key, byte[]? pixels) { if (pixels is not null) images.Add(key, pixels); }
    }

    private static byte[]? Resource(string library, int id)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var path in new[] { Path.Combine(windows, "SystemResources", library + ".mun"), Path.Combine(Environment.SystemDirectory, library) })
        {
            var module = LoadLibraryEx(path, 0, 0x22 /* DATAFILE | IMAGE_RESOURCE: never execute DLL entrypoints */);
            if (module == 0) continue;
            nint icon = 0;
            try
            {
                icon = LoadImage(module, id, 1 /* IMAGE_ICON */, WindowsFolderIcons.PixelSize, WindowsFolderIcons.PixelSize, 0);
                if (icon != 0) return WindowsFolderIcons.Pixels(icon);
            }
            finally { if (icon != 0) DestroyIcon(icon); FreeLibrary(module); }
        }
        return null;
    }

    private static byte[]? Extract(string path, int index)
    {
        nint large = 0, small = 0;
        try
        {
            var size = (uint)WindowsFolderIcons.PixelSize;
            return SHDefExtractIcon(path, index, 0, out large, out small, size | size << 16) == 0 && large != 0
                ? WindowsFolderIcons.Pixels(large) : null;
        }
        finally { if (large != 0) DestroyIcon(large); if (small != 0) DestroyIcon(small); }
    }
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint module, nint name, uint type, int width, int height, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)] private static extern int SHDefExtractIcon(string path, int index, uint flags, out nint large, out nint small, uint size);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
