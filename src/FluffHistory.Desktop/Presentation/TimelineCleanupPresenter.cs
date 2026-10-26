using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Desktop.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Window-owned Timeline preview and a separate native confirmation; never stack ContentDialogs.</summary>
public sealed class TimelineCleanupPresenter(TimelineCleanupViewModel model)
{
    private IAppStrings S => Model.Strings;
    public TimelineCleanupViewModel Model { get; } = model;
    private bool _open;
    public async Task ShowAsync(XamlRoot root)
    {
        if (_open || !Model.CanOpen) return;
        _open = true;
        var execution = Task.CompletedTask;
        try
        {
            Model.Reset();
            while (true)
            {
                var dialog = new ContentDialog { XamlRoot = root, Title = S[TextId.Cleanup_Title], CloseButtonText = S[TextId.Action_Close],
                    PrimaryButtonText = S[TextId.Cleanup_ExecuteAction], DefaultButton = ContentDialogButton.None, Content = new TimelineCleanupView(Model) };
                void UpdatePreviewButton(object? sender, EventArgs args) => dialog.IsPrimaryButtonEnabled = Model.Execute.CanExecute(null);
                Model.Execute.CanExecuteChanged += UpdatePreviewButton;
                dialog.Closing += (_, args) => { if (Model.Busy) { args.Cancel = true; Model.Cancel.Execute(null); } };
                ContentDialogResult result;
                try { UpdatePreviewButton(null, EventArgs.Empty); result = await dialog.ShowAsync(); }
                finally { Model.Execute.CanExecuteChanged -= UpdatePreviewButton; }
                if (result != ContentDialogResult.Primary) return;
                await Model.Execute.ExecuteAsync();
                if (!Model.ConfirmRequested) continue;

                var confirmation = DestructiveDialog.Create(root, S[TextId.Cleanup_ConfirmationTitle], Model.Confirmation, S[TextId.Action_Confirm]);
                void UpdateConfirmButton(object? sender, EventArgs args) => confirmation.IsPrimaryButtonEnabled = Model.ConfirmExecution.CanExecute(null);
                Model.ConfirmExecution.CanExecuteChanged += UpdateConfirmButton;
                try { UpdateConfirmButton(null, EventArgs.Empty); result = await confirmation.ShowAsync(); }
                finally { Model.ConfirmExecution.CanExecuteChanged -= UpdateConfirmButton; }
                if (result == ContentDialogResult.Primary) execution = Model.ConfirmExecution.ExecuteAsync();
                else await Model.CancelConfirmation.ExecuteAsync();
            }
        }
        finally { await execution; _open = false; }
    }
}
