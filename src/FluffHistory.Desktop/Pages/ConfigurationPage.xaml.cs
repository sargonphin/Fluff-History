using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using FluffHistory.Desktop.Presentation;
namespace FluffHistory.Desktop.Pages;
public sealed partial class ConfigurationPage : UserControl
{
    public ConfigurationViewModel Model { get; }
    public ConfigurationPage(ConfigurationViewModel model)
    {
        Model = model; InitializeComponent();
        Loaded += async (_, _) =>
        {
            await Model.Storage.ActivateAsync(Model.RepositoryPath);
            await Model.RefreshSecurityAsync();
        };
        Unloaded += (_, _) => Model.Storage.Deactivate();
    }
}
