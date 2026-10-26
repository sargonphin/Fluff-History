using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FluffHistory.Desktop.Presentation.Models;

namespace FluffHistory.Desktop.Presentation;

internal static class DestructiveDialog
{
    private static IAppStrings S => MrtAppStrings.Instance;
    internal static ContentDialog Create(XamlRoot root, string title, string message, string primary)
    {
        var dialog = new ContentDialog { XamlRoot = root, Title = title, Content = Ui.Text(message),
            PrimaryButtonText = primary, CloseButtonText = S[TextId.Action_Cancel], DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        if (new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) return dialog;
        foreach (var (key, color) in new[] { ("AccentButtonBackground", 0xC42B1Cu), ("AccentButtonBackgroundPointerOver", 0xB82618u), ("AccentButtonBackgroundPressed", 0xA32116u) })
            dialog.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
        foreach (var key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" })
            dialog.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        return dialog;
    }
}
