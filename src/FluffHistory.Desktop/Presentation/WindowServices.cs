using System.Reflection;
using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;

namespace FluffHistory.Desktop.Presentation;

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
internal sealed class WindowServices(Window window, DesktopLifetime lifetime, Action<string> navigate) : IDesktopDialogs
{
    public IAppStrings Strings => lifetime.Strings;
    private IAppStrings S => Strings;
    public Task<string?> FolderAsync() => Dialogs.FolderAsync(window);
    public Task<IReadOnlyList<string>> FoldersAsync() => Dialogs.FoldersAsync(window);
    public Task<bool?> ProtectBackupAsync(string path) => Dialogs.ProtectBackupAsync(window, path);
    public Task<char[]?> PasswordAsync(bool creation) => Dialogs.PasswordAsync(window, creation);
    public Task<char[]?> NewRecoveryPasswordAsync() => Dialogs.PasswordAsync(window, true, changing: true);
    public Task<char[]?> MigrationRecoveryPasswordAsync() => Dialogs.PasswordAsync(window, false, migration: true);
    public async Task NoticeAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = title,
            Content = Ui.Text(message), CloseButtonText = S[TextId.Action_OK], DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
    }
    public async Task<bool> ConfirmLocationAsync(BackupLocationConfirmation confirmation)
    {
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = S[TextId.Dialog_ExistingBackupTitle],
            Content = Ui.Stack(Dialogs.Location(confirmation.Path), Ui.Text(confirmation.Status), Ui.Text(confirmation.Explanation)),
            PrimaryButtonText = confirmation.Primary ?? S[TextId.Action_UseBackupLocation], CloseButtonText = S[TextId.Action_Cancel], DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = primary, CloseButtonText = S[TextId.Action_Cancel], DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public void Navigate(string page) => navigate(page);
    public async Task<bool> ConfirmDeleteAsync(string title, string message)
    {
        var dialog = DestructiveDialog.Create(window.Content.XamlRoot, title, message, S[TextId.Action_Delete]);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public string SettingsPath => lifetime.SettingsStore.FilePath;
    public string DiagnosticsHeader
    {
        get
        {
            var identity = "none (unpackaged)";
            try
            {
                var id = Package.Current.Id; var v = id.Version;
                identity = $"{id.Name} {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            var sha = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GitCommit")?.Value ?? "local development";
            var release = FluffHistory.Application.InstalledReleaseIdentity.FromAssembly(typeof(App).Assembly)?.ToString() ?? "unpublished development";
            var warning = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluff-History", "Installer", "integration-warning.txt");
            var integration = File.Exists(warning) ? "unavailable (Setup reported sparse registration failure; run Setup again to retry)" : identity.StartsWith("none", StringComparison.Ordinal) ? "unavailable (no package identity)" : "registered";
            return $"Fluff-History\nRelease: {release}\nIdentity: {identity}\nModern Explorer command: {integration}\nGit SHA: {sha}\nWindows App SDK: 2.5.1 · x64 · self-contained\n.NET: {Environment.Version} · Windows: {Environment.OSVersion.Version}";
        }
    }
    public void CopyText(string text) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
}
