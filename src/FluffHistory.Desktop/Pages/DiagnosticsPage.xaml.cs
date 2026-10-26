using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
namespace FluffHistory.Desktop.Pages;
public sealed partial class DiagnosticsPage : UserControl
{
    public DiagnosticsViewModel Model { get; }
    public DiagnosticsPage(DiagnosticsViewModel model) { Model = model; InitializeComponent(); }
}
