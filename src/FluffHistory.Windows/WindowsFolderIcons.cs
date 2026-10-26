using System.Runtime.InteropServices;
using FluffHistory.Application;

namespace FluffHistory.Windows;

/// <summary>One bounded, process-local load of Explorer's known-folder icons. No per-path/render lookup.</summary>
public static class WindowsFolderIcons
{
    public const int PixelSize = 64;
    private static readonly Lazy<Task<IReadOnlyDictionary<PersonalFolderKind, byte[]>>> Cache = new(LoadAsync);
    public static Task<IReadOnlyDictionary<PersonalFolderKind, byte[]>> GetAsync() => Cache.Value;

    private static Task<IReadOnlyDictionary<PersonalFolderKind, byte[]>> LoadAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<IReadOnlyDictionary<PersonalFolderKind, byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = new Dictionary<PersonalFolderKind, byte[]>();
            var initialized = CoInitializeEx(0, 2);
            try
            {
                if (initialized >= 0)
                    foreach (var kind in Enum.GetValues<PersonalFolderKind>())
                        if (Read(kind) is { } pixels) result.Add(kind, pixels);
                completion.SetResult(result);
            }
            catch (Exception error) { completion.SetException(error); }
            finally { if (initialized >= 0) CoUninitialize(); }
        }) { IsBackground = true, Name = "Fluff-History folder icons" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private static byte[]? Read(PersonalFolderKind kind)
    {
        nint icon = 0, pidl = 0;
        try
        {
            if (kind == PersonalFolderKind.Folder)
            {
                return ReadStock(3 /* SIID_FOLDER */);
            }
            else
            {
                var id = new Guid(kind switch
                {
                    PersonalFolderKind.Desktop => "B4BFCC3A-DB2C-424C-B029-7FE99A87C641",
                    PersonalFolderKind.Documents => "FDD39AD0-238F-46AF-ADB4-6C85480369C7",
                    PersonalFolderKind.Downloads => "374DE290-123F-4565-9164-39C4925E467B",
                    PersonalFolderKind.Pictures => "33E28130-4E1E-4676-835A-98395C3BC3BB",
                    PersonalFolderKind.Music => "4BD8D571-6D19-48D3-BE97-422220080E43",
                    _ => "18989B1D-99B5-455B-841C-AB7C74E4DDFC"
                });
                if (SHGetKnownFolderIDList(in id, 0x4000 /* DONT_VERIFY */, 0, out pidl) >= 0 && pidl != 0 &&
                    SHGetFileInfo(pidl, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x4108 /* ICON | PIDL | SYSICONINDEX */) != 0)
                { icon = info.Icon; ReplaceWithLarge(info.IconIndex, ref icon); }
            }
            return icon == 0 ? null : Pixels(icon);
        }
        finally { if (icon != 0) DestroyIcon(icon); if (pidl != 0) ILFree(pidl); }
    }

    internal static byte[]? ReadStock(uint id)
    {
        var stock = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() }; nint icon = 0;
        try
        {
            if (SHGetStockIconInfo(id, 0x4100 /* ICON | SYSICONINDEX */, ref stock) < 0) return null;
            icon = stock.Icon; ReplaceWithLarge(stock.SystemIndex, ref icon);
            return icon == 0 ? null : Pixels(icon);
        }
        finally { if (icon != 0) DestroyIcon(icon); }
    }

    internal static byte[]? Pixels(nint icon)
    {
        var dc = CreateCompatibleDC(0); if (dc == 0) return null;
        nint bitmap = 0, previous = 0;
        try
        {
            var header = new BitmapInfoHeader { Size = 40, Width = PixelSize, Height = -PixelSize, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, in header, 0, out var address, 0, 0);
            if (bitmap == 0 || address == 0) return null;
            previous = SelectObject(dc, bitmap);
            var pixels = new byte[PixelSize * PixelSize * 4];
            Marshal.Copy(pixels, 0, address, pixels.Length);
            if (!DrawIconEx(dc, 0, 0, icon, PixelSize, PixelSize, 0, 0, 3 /* DI_NORMAL */)) return null;
            Marshal.Copy(address, pixels, 0, pixels.Length); return pixels;
        }
        finally { if (previous != 0) SelectObject(dc, previous); if (bitmap != 0) DeleteObject(bitmap); DeleteDC(dc); }
    }
    private static void ReplaceWithLarge(int index, ref nint icon)
    {
        var id = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        if (SHGetImageList(4 /* JUMBO */, in id, out var list) < 0 || list == 0) return;
        try
        {
            // Windows documents IImageList/HIMAGELIST interchange for this API.
            var large = ImageList_GetIcon(list, index, 1);
            if (large != 0) { if (icon != 0) DestroyIcon(icon); icon = large; }
        }
        finally { Marshal.Release(list); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size; public nint Icon; public int SystemIndex, IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderIDList(in Guid id, uint flags, nint token, out nint pidl);
    [DllImport("shell32.dll")] private static extern void ILFree(nint pidl);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(nint pidl, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);
    [DllImport("shell32.dll")] private static extern int SHGetImageList(int size, in Guid id, out nint list);
    [DllImport("comctl32.dll")] private static extern nint ImageList_GetIcon(nint list, int index, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, in BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
