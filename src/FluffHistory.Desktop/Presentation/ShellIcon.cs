using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Recycling-safe async view over the bounded native/UI caches.</summary>
public sealed class ShellIcon : Grid
{
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly FontIcon _fallback = new() { Glyph = "\uE8A5" };
    private long _generation;
    public ShellIcon() { Children.Add(_fallback); Children.Add(_image); IsHitTestVisible = false; }
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key), typeof(string), typeof(ShellIcon), new PropertyMetadata("", Changed));
    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    private static async void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (ShellIcon)sender; var generation = ++view._generation;
        var section = ((string)args.NewValue).StartsWith("section:", StringComparison.Ordinal);
        view._image.Source = null; view._fallback.Visibility = section ? Visibility.Collapsed : Visibility.Visible;
        var source = await FolderIconConverter.SourceAsync((string)args.NewValue);
        if (generation != view._generation) return;
        view._image.Source = source; view._fallback.Visibility = source is null && !section ? Visibility.Visible : Visibility.Collapsed;
    }
}
