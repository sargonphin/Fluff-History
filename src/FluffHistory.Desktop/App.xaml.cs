using Microsoft.UI.Xaml;
namespace FluffHistory.Desktop;
public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await FluffHistory.Windows.WindowsShellRequests.CleanupStaleAsync();
        await Presentation.FolderIconConverter.InitializeAsync();
        _window = new MainWindow();
        Program.Attach(((MainWindow)_window).ReceiveActivation);
        _window.Activate();
        Program.ReleaseStartup();
    }
}
