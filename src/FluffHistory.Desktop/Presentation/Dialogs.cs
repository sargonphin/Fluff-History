using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using FluffHistory.Desktop.Presentation.Models;

namespace FluffHistory.Desktop.Presentation;

internal static class Dialogs
{
    private static IAppStrings S => MrtAppStrings.Instance;
    internal static StackPanel Location(string path) => Ui.Stack(
        Ui.Text(S[TextId.Dialog_BackupLocation]), new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
    internal static async Task<string?> FolderAsync(Window window)
    {
        var picker = new FolderPicker(window.AppWindow.Id);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    internal static async Task<IReadOnlyList<string>> FoldersAsync(Window window)
    {
        var picker = new FolderPicker(window.AppWindow.Id) { CommitButtonText = S[TextId.Dialog_AddFolders], Title = S[TextId.Dialog_SourceFolders] };
        return (await picker.PickMultipleFoldersAsync()).Select(folder => folder.Path).ToArray();
    }
    internal static async Task<bool?> ProtectBackupAsync(Window window, string path)
    {
        var protect = new CheckBox { Content = S[TextId.Dialog_ProtectOption] };
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot, Title = S[TextId.Dialog_SetupTitle], PrimaryButtonText = S[TextId.Action_Continue],
            CloseButtonText = S[TextId.Action_Cancel], DefaultButton = ContentDialogButton.Primary,
            Content = Ui.Stack(Location(path), Ui.Text(S[TextId.Dialog_EmptyLocationExplanation]), protect) };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? protect.IsChecked == true : null;
    }
    internal static async Task<char[]?> PasswordAsync(Window window, bool creation, bool changing = false, bool migration = false)
    {
        var password = new PasswordBox { Header = S[TextId.Dialog_RecoveryPassword], MaxLength = 1024 };
        var confirmation = new PasswordBox { Header = S[TextId.Dialog_ConfirmPassword], MaxLength = 1024 };
        var dialog = new ContentDialog { XamlRoot = window.Content.XamlRoot,
            Title = changing ? S[TextId.Dialog_ChangePasswordTitle] : migration ? S[TextId.Dialog_ResumeEncryptionTitle] : creation ? S[TextId.Dialog_ProtectTitle] : S[TextId.Dialog_RecoveryTitle],
            PrimaryButtonText = changing ? S[TextId.Dialog_ChangePasswordAction] : migration ? S[TextId.Action_Resume] : creation ? S[TextId.Dialog_ProtectedSetupAction] : S[TextId.Dialog_RecoverAction],
            CloseButtonText = S[TextId.Action_Cancel], DefaultButton = ContentDialogButton.Primary,
            Content = Ui.Stack(Ui.Text(changing ? S[TextId.Dialog_ChangePasswordExplanation]
                : migration ? S[TextId.Dialog_ResumeEncryptionExplanation]
                : creation ? S[TextId.Dialog_ProtectExplanation]
                : S[TextId.Dialog_RecoveryExplanation]), password) };
        if (creation) ((StackPanel)dialog.Content).Children.Add(confirmation);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (password.Password.Length == 0 || creation && password.Password != confirmation.Password)
            { args.Cancel = true; dialog.Title = S[TextId.Dialog_PasswordMismatch]; }
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary ? password.Password.ToCharArray() : null; }
        finally { password.Password = ""; confirmation.Password = ""; }
    }
}
