using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml.Controls;
namespace FluffHistory.Desktop.Pages;
public sealed partial class TimelineCleanupView : UserControl
{
    public TimelineCleanupViewModel Model { get; }
    public TimelineCleanupView(TimelineCleanupViewModel model) { Model = model; InitializeComponent(); }
}
