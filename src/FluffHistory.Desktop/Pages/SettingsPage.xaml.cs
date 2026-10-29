using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml.Controls;
namespace FluffHistory.Desktop.Pages;
public sealed partial class SettingsPage : UserControl
{
    public SettingsViewModel Model { get; }
    public SettingsPage(SettingsViewModel model) { Model = model; InitializeComponent(); }
    public void ShowUpdates() => UpdatesSection.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true });
    private async void CheckUpdates_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Model.CheckUpdatesAsync();
    private async void InstallUpdate_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Model.InstallUpdateAsync();
    private async void ReleaseNotes_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // The model exposes only the exact validated GitHub release page from the typed snapshot.
        if (Model.ReleaseNotesUrl is { } url) await global::Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }
}
