using FluffHistory.Application;
using FluffHistory.Windows;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FluffHistory.Desktop.Presentation;

/// <summary>UI-thread image cache; conversion only creates a view over already loaded pixels.</summary>
public sealed class FolderIconConverter : IValueConverter
{
    private static readonly Dictionary<PersonalFolderKind, ImageSource> Images = [];
    private static readonly Dictionary<string, ImageSource> SectionImages = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<ImageSource?>> FileImages = new(StringComparer.Ordinal);
    internal static Task<ImageSource?> SourceAsync(string key)
    {
        if (key.StartsWith("section:", StringComparison.Ordinal)) return Task.FromResult(SectionImages.GetValueOrDefault(key[8..]));
        if (key.StartsWith("known:", StringComparison.Ordinal) && Enum.TryParse<PersonalFolderKind>(key[6..], out var kind))
            return Task.FromResult(Images.GetValueOrDefault(kind) ?? Images.GetValueOrDefault(PersonalFolderKind.Folder));
        var extension = key.StartsWith("file:", StringComparison.Ordinal) ? key[5..] : "";
        if (FileImages.TryGetValue(extension, out var existing)) return existing;
        if (FileImages.Count >= WindowsFileTypeIcons.MaximumExtensions && extension.Length != 0) extension = "";
        if (FileImages.TryGetValue(extension, out existing)) return existing;
        return FileImages[extension] = LoadFileAsync(extension);
    }
    private static async Task<ImageSource?> LoadFileAsync(string extension)
    {
        var pixels = await WindowsFileTypeIcons.GetAsync(extension);
        if (pixels is null) return null;
        var image = new WriteableBitmap(WindowsFolderIcons.PixelSize, WindowsFolderIcons.PixelSize);
        using (var stream = image.PixelBuffer.AsStream()) stream.Write(pixels);
        image.Invalidate(); return image;
    }
    public static async Task InitializeAsync()
    {
        foreach (var (key, pixels) in await WindowsSectionIcons.GetAsync())
        {
            var image = new WriteableBitmap(WindowsFolderIcons.PixelSize, WindowsFolderIcons.PixelSize);
            using (var stream = image.PixelBuffer.AsStream()) stream.Write(pixels);
            image.Invalidate(); SectionImages[key] = image;
        }
        foreach (var (kind, pixels) in await WindowsFolderIcons.GetAsync())
        {
            // Own the pixels for the full UI lifetime. SoftwareBitmapSource can retain its
            // SoftwareBitmap; closing that input before a later render can raise E_CLOSED.
            var image = new WriteableBitmap(WindowsFolderIcons.PixelSize, WindowsFolderIcons.PixelSize);
            using (var stream = image.PixelBuffer.AsStream()) stream.Write(pixels);
            image.Invalidate(); Images[kind] = image;
        }
    }
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var kind = value is PersonalFolderKind folder ? folder : PersonalFolderKind.Folder;
        return Images.TryGetValue(kind, out var image) || Images.TryGetValue(PersonalFolderKind.Folder, out image)
            ? new ImageIcon { Source = image, Width = 24, Height = 24 }
            : new FontIcon { Glyph = new FolderPresentation("", kind).Glyph, FontSize = 20, Width = 24, Height = 24 };
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
