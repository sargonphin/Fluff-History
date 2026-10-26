using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>Bounded association-resource icons, without file handlers, thumbnails, source paths or source reads.</summary>
public static class WindowsFileTypeIcons
{
    public const int MaximumExtensions = 128;
    private static readonly Lazy<Worker> State = new(() => new());
    public static Task<byte[]?> GetAsync(string extension)
    {
        var key = FileTypeIconKey.Extension("file" + extension);
        if (!string.Equals(key, extension, StringComparison.OrdinalIgnoreCase)) key = "";
        return State.Value.Get(key);
    }
    private sealed class Worker
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Task<byte[]?>> _tasks = new(StringComparer.Ordinal);
        private readonly BlockingCollection<(string Key, TaskCompletionSource<byte[]?> Completion)> _queue = new(MaximumExtensions + 1);
        private byte[]? _generic;
        internal Worker()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            _ = Get("");
            var thread = new Thread(Run) { IsBackground = true, Name = "Fluff-History file icons" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        internal Task<byte[]?> Get(string key)
        {
            lock (_gate)
            {
                if (_tasks.TryGetValue(key, out var found)) return found;
                if (_tasks.Count >= MaximumExtensions + 1) return _tasks[""];
                var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tasks.Add(key, completion.Task); _queue.Add((key, completion)); return completion.Task;
            }
        }
        private void Run()
        {
            var initialized = CoInitializeEx(0, 2);
            try
            {
                foreach (var (key, completion) in _queue.GetConsumingEnumerable())
                {
                    byte[]? pixels = null;
                    try { if (initialized >= 0) pixels = key.Length == 0 ? Generic() : ReadAssociation(key) ?? Generic(); }
                    catch (Exception) { /* Presentation fallback must not interrupt backup or browsing. */ }
                    completion.SetResult(pixels);
                }
            }
            finally { if (initialized >= 0) CoUninitialize(); }
        }
        private byte[]? Generic()
        {
            if (_generic is not null) return _generic;
            var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
            if (SHGetStockIconInfo(0 /* DOCNOASSOC */, 0x100, ref info) < 0 || info.Icon == 0) return null;
            try { return _generic = WindowsFolderIcons.Pixels(info.Icon); } finally { DestroyIcon(info.Icon); }
        }
    }
    private static byte[]? ReadAssociation(string extension)
    {
        // Query the default ICON RESOURCE string, not a Shell extension/verb/handler.
        // Never pass a historical or live source path to the Shell.
        uint length = 2048; var value = new StringBuilder((int)length);
        if (AssocQueryString(0, 15 /* DEFAULTICON */, extension, null, value, ref length) != 0) return null;
        var location = new StringBuilder(Environment.ExpandEnvironmentVariables(value.ToString()).TrimStart('@'));
        var index = PathParseIconLocation(location); var path = location.ToString().Trim('"');
        if (!LocalOrdinaryResource(path)) return null;
        nint icon = 0, small = 0;
        try
        {
            return SHDefExtractIcon(path, index, 0, out icon, out small, WindowsFolderIcons.PixelSize) >= 0 && icon != 0
                ? WindowsFolderIcons.Pixels(icon) : null;
        }
        finally { if (icon != 0) DestroyIcon(icon); if (small != 0) DestroyIcon(small); }
    }
    private static bool LocalOrdinaryResource(string path)
    {
        if (path.Length is < 3 or > 2048 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('%')) return false;
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) return false;
        // Check top-down before descending so a junction cannot redirect icon reads to a network resource.
        var ancestors = new Stack<string>(); for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
        foreach (var parent in ancestors) if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
        return (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StockIconInfo
    { public uint Size; public nint Icon; public int SystemIndex, IconIndex; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path; }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode)] private static extern int AssocQueryString(uint flags, uint kind, string association, string? extra, StringBuilder value, ref uint length);
    [DllImport("shlwapi.dll", EntryPoint = "PathParseIconLocationW", CharSet = CharSet.Unicode)] private static extern int PathParseIconLocation(StringBuilder location);
    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)] private static extern int SHDefExtractIcon(string path, int index, uint flags, out nint large, out nint small, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
