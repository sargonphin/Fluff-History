using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml.Controls;
namespace FluffHistory.Desktop.Pages;
public sealed partial class OverviewPage : UserControl
{
    public OverviewViewModel Model { get; }
    public OverviewPage(OverviewViewModel model)
    {
        Model = model; InitializeComponent();


    }
}
