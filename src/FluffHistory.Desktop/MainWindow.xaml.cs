using FluffHistory.Application;
using FluffHistory.Desktop.Pages;
using FluffHistory.Desktop.Presentation;
using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using FluffHistory.Windows;
using System.Runtime.InteropServices;

namespace FluffHistory.Desktop;

public sealed partial class MainWindow : Window
{
    private static IAppStrings S => MrtAppStrings.Instance;
    private readonly DesktopLifetime _lifetime;
    private readonly Dictionary<string, FrameworkElement> _pages;
    private readonly HistoryViewModel _history;
    private readonly StorageUsageViewModel _storage;
    private bool _closing, _closeReady;
    private bool _ready, _routing;
    private long _pendingActivation, _appliedActivation;
    private string? _pendingToken;
    public MainWindow()
    {
        InitializeComponent(); Title = "Fluff-History";
        RootLayout.FlowDirection = S.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RootLayout.Language = S.Language;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        // The unpackaged UI uses its installed product icon.
        AppTitleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-400.png"))) };
        AppTitleBar.Loaded += (_, _) => UpdateCaptionForeground();
        AppTitleBar.ActualThemeChanged += (_, _) => UpdateCaptionForeground();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FluffHistory.ico"));
        AppWindow.Resize(new(1120, 800));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = 760; presenter.PreferredMinimumHeight = 560; }
        _lifetime = new(DispatcherQueue, UiLanguageStartup.Preferences) { OwnerWindow = WinRT.Interop.WindowNative.GetWindowHandle(this) };
        var dialogs = new WindowServices(this, _lifetime, Navigate);
        _history = new(_lifetime, dialogs);
        var cleanup = new TimelineCleanupPresenter(new(_lifetime, dialogs));
        var overview = new OverviewViewModel(_lifetime, dialogs);
        _storage = overview.Storage;
        _pages = new()
        {
            ["Overview"] = new OverviewPage(overview), ["History"] = new HistoryPage(_history, cleanup),
            ["Configuration"] = new ConfigurationPage(new(_lifetime, dialogs, _storage)), ["Diagnostics"] = new DiagnosticsPage(new(_lifetime, dialogs)),
            ["Settings"] = new SettingsPage(new(_lifetime, dialogs))
        };
        _lifetime.Changed += UpdateStatus;
        Navigate("Overview");
        Navigation.Loaded += async (_, _) =>
        {
            if (Navigation.SettingsItem is NavigationViewItem settingsItem)
            { settingsItem.Content = MrtAppStrings.Instance[TextId.Navigation_Settings]; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, MrtAppStrings.Instance[TextId.Navigation_Settings]); }
            await _lifetime.RunAsync(_lifetime.InitializeAsync);
            _ = _storage.ActivateAsync(_lifetime.Settings.RepositoryPath);
            Navigation.IsEnabled = !_closing;
            _ready = true; await RouteActivationAsync();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (_closeReady) return;
            args.Cancel = true; if (_closing) return;
            _closing = true; Navigation.IsEnabled = false;
            GlobalNotice.Title = S[TextId.Status_Closing]; GlobalNotice.Message = S[TextId.Status_ClosingWait]; GlobalNotice.IsOpen = true;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try { _storage.Deactivate(); _history.Deactivate(); await _lifetime.DisposeAsync(); _closeReady = true; Close(); }
                catch (Exception) { GlobalNotice.Title = S[TextId.Status_ShutdownAttention]; GlobalNotice.Message = S[TextId.Status_ShutdownAdvice]; GlobalNotice.Severity = InfoBarSeverity.Error; GlobalNotice.IsOpen = true; _closing = false; Navigation.IsEnabled = true; }
            });
        };
    }
    internal void ReceiveActivation(long revision, string? token) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_closing || revision <= _pendingActivation) return;
        _pendingActivation = revision; _pendingToken = token;
        _history.CancelQuery();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate();
        if (token is not null) SetForegroundWindow(_lifetime.OwnerWindow);
        await RouteActivationAsync();
    });
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    private async Task RouteActivationAsync()
    {
        if (!_ready || _routing || _closing) return;
        _routing = true;
        try
        {
            while (_pendingActivation > _appliedActivation && !_closing)
            {
                var revision = _pendingActivation; var token = _pendingToken; _appliedActivation = revision;
                if (token is null) continue; // Ordinary cold launch remains Overview; warm launch focuses current page.
                var request = await WindowsShellRequests.ConsumeAsync(token);
                if (revision != _pendingActivation || request is null) continue;
                var page = (HistoryPage)_pages["History"]; page.BeginShellNavigation();
                try
                {
                    Navigate("History"); await Task.Yield();
                    await _history.OpenShellAsync(request, () => revision == _pendingActivation && !_closing);
                }
                finally { page.EndShellNavigation(); }
            }
        }
        finally { _routing = false; }
    }
    private void Navigate(string page)
    {
        var updates = page == "Updates";
        Navigation.SelectedItem = updates ? Navigation.SettingsItem : Navigation.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == page);
        if (updates) DispatcherQueue.TryEnqueue(() => ((SettingsPage)_pages["Settings"]).ShowUpdates());
    }
    private void ToggleNavigationPane(TitleBar sender, object args)
    {
        if (Navigation.IsEnabled) Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    }
    private void UpdateCaptionForeground()
    {
        // Use the control's resolved theme brush, including Windows contrast themes.
        AppWindow.TitleBar.ButtonForegroundColor = (AppTitleBar.Foreground as SolidColorBrush)?.Color;
    }
    private void NavigateChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_pages is null || !args.IsSettingsSelected && args.SelectedItem is not NavigationViewItem) return;
        if (_lifetime.CurrentPage == "History") _history.Deactivate(); else { _lifetime.CancelHistory(); _lifetime.ReleasePreview(); }
        var page = args.IsSettingsSelected ? "Settings" : (string)((NavigationViewItem)args.SelectedItem).Tag;
        _lifetime.ClearSavedNotice();
        PageHost.Children.Clear(); PageHost.Children.Add(_pages[page]); _lifetime.CurrentPage = page; _lifetime.Notify();
    }
    private void UpdateStatus()
    {
        RootLayout.Language = S.Language;
        RootLayout.FlowDirection = S.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (Navigation.SettingsItem is NavigationViewItem settings)
        {
            settings.Content = S[TextId.Navigation_Settings];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settings, S[TextId.Navigation_Settings]);
        }
        RootLayout.RequestedTheme = _lifetime.Preferences.Appearance switch
        { AppearancePreference.Light => ElementTheme.Light, AppearancePreference.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        GlobalNotice.IsOpen = true; GlobalNotice.Severity = InfoBarSeverity.Informational;
        if (_lifetime.HasError) { GlobalNotice.Title = S[TextId.Status_ActionAttention]; GlobalNotice.Message = _lifetime.LastOutcome; GlobalNotice.Severity = InfoBarSeverity.Error; }
        else if (_lifetime.Inspection.State == RepositoryReadiness.RecoveryRequired) { GlobalNotice.Title = S[TextId.Status_RecoveryRequired]; GlobalNotice.Message = S[TextId.Status_RecoveryAdvice]; GlobalNotice.Severity = InfoBarSeverity.Warning; }
        else if (_lifetime.Settings.RepositoryPath.Length == 0) { GlobalNotice.Title = S[TextId.Status_NotConfigured]; GlobalNotice.Message = S[TextId.Status_SetupAdvice]; }
        else GlobalNotice.IsOpen = false;
    }
}
