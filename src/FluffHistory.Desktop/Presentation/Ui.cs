using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluffHistory.Desktop.Presentation;

internal static class Ui
{
    internal static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    internal static StackPanel Stack(params UIElement[] children)
    { var panel = new StackPanel { Spacing = 12 }; foreach (var child in children) panel.Children.Add(child); return panel; }
}
