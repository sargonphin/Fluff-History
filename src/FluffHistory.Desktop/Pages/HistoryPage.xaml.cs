using FluffHistory.Desktop.Presentation.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using FluffHistory.Desktop.Presentation;

namespace FluffHistory.Desktop.Pages;

public sealed partial class HistoryPage : UserControl
{
    private IAppStrings S => Model.Strings;
    private bool _unselecting;
    private bool _shellNavigation;
    private MenuFlyout? _contextMenu;
    private TimelineItem[] _contextItems = [];
    private VersionRow? _contextVersion;
    private RootChoice? _contextRoot;
    private DateTimeOffset? _contextTime;
    private bool _contextList;
    private long _contextGeneration;
    private readonly TimelineCleanupPresenter _cleanup;
    public TimelineCleanupViewModel Cleanup => _cleanup.Model;
    public HistoryViewModel Model { get; }
    public HistoryPage(HistoryViewModel model, TimelineCleanupPresenter cleanup)
    {
        Model = model; _cleanup = cleanup; InitializeComponent();
        // ListView handles Enter itself. Observe the routed key even after its class handler so a
        // keyboard-selected inferred folder has the same activation as a pointer double-click.
        ChildList.AddHandler(KeyDownEvent, new KeyEventHandler(ChildKeyDown), handledEventsToo: true);
        TileList.AddHandler(KeyDownEvent, new KeyEventHandler(ChildKeyDown), handledEventsToo: true);
        Model.PropertyChanged += (_, _) => { SynchronizeSelection(); CheckContextMenu(); };
        Unloaded += (_, _) => HideContextMenu();
        Model.SelectionCleared += () =>
        {
            _unselecting = true;
            try { ChildList.SelectedItems.Clear(); TileList.SelectedItems.Clear(); }
            finally { _unselecting = false; }
        };
    }
    private void SynchronizeSelection()
    {
        if (_unselecting || Model.BindingNavigation) return;
        _unselecting = true;
        try
        {
            foreach (var view in new ListViewBase[] { ChildList, TileList })
            {
                var selected = Model.SelectedItems.Where(view.Items.Contains).ToArray();
                if (view.SelectedItems.Count == selected.Length && selected.All(view.SelectedItems.Contains)) continue;
                view.SelectedItems.Clear(); foreach (var item in selected) view.SelectedItems.Add(item);
            }
        }
        finally { _unselecting = false; }
    }
    private async void PageLoaded(object sender, RoutedEventArgs args) { if (!_shellNavigation) await Model.ActivateAsync(); }
    internal void BeginShellNavigation() => _shellNavigation = true;
    internal void EndShellNavigation() => _shellNavigation = false;
    private async void BreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    { if (args.Item is BreadcrumbChoice crumb) { if (crumb.VirtualRoot) await Model.GoToVirtualRootAsync(); else await Model.ChangeFolderAsync(crumb.Folder); } }
    private async void ChildrenSelected(object sender, SelectionChangedEventArgs args)
    { if (!Model.BindingNavigation && !_unselecting && sender is ListViewBase view && view.Visibility == Visibility.Visible) await Model.SelectItemsAsync(view.SelectedItems.OfType<TimelineItem>().ToArray()); }
    private async void ChildDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    { if (sender is ListViewBase view && view.SelectedItems.Count == 1 && view.SelectedItem is TimelineItem row) await Model.OpenItemAsync(row); }
    private async void ChildKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && sender is ListViewBase view && view.SelectedItems.Count == 1 && view.SelectedItem is TimelineItem row &&
            (row is RootChoice || row is ChildRow { Child.IsFolder: true }))
        { args.Handled = true; await Model.OpenItemAsync(row); }
    }
    private static FrameworkElement? ContextContainer(ListViewBase view, ContextRequestedEventArgs args, bool pointer)
    {
        var element = pointer ? args.OriginalSource as DependencyObject : FocusManager.GetFocusedElement(view.XamlRoot) as DependencyObject;
        for (var current = element; current is not null && !ReferenceEquals(current, view); current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(current), view)) return current as FrameworkElement;
        return null; // Blank space never acts on an unrelated previous selection.
    }
    private async void ItemsContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        HideContextMenu();
        if (sender is not ListViewBase view) return;
        var pointer = args.TryGetPosition(view, out var position);
        var container = ContextContainer(view, args, pointer);
        if (container is null || view.ItemFromContainer(container) is not TimelineItem item) return;
        args.Handled = true;
        var generation = _contextGeneration;
        if (!await Model.SelectContextItemAsync(item) || generation != _contextGeneration || !IsLoaded || view.Visibility != Visibility.Visible) return;
        var menu = new MenuFlyout();
        if (Model.SelectedItems.Count == 1 && item is RootChoice or ChildRow { Child.IsFolder: true })
            AddContextAction(menu, S[TextId.Action_Open], Model.OpenSelected, Symbol.OpenFile);
        AddContextAction(menu, S[TextId.Action_Restore], Model.RestoreOriginal, Symbol.Download);
        AddContextAction(menu, S[TextId.Action_RestoreTo], Model.RestoreSelected, Symbol.Folder);
        if (Model.HasFileSelection) AddContextAction(menu, S[TextId.Action_Preview], Model.PreviewDisplayed, Symbol.ViewAll);
        ShowContextMenu(menu, view, container, pointer, position);
    }
    private void VersionsContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        HideContextMenu();
        if (sender is not ListViewBase view) return;
        var pointer = args.TryGetPosition(view, out var position);
        var container = ContextContainer(view, args, pointer);
        if (container is null || view.ItemFromContainer(container) is not VersionRow row) return;
        args.Handled = true;
        if (!Model.SelectContextVersion(row)) return;
        var menu = new MenuFlyout();
        AddContextAction(menu, S[TextId.Action_Restore], Model.RestoreExactOriginal, Symbol.Download);
        AddContextAction(menu, S[TextId.Action_RestoreTo], Model.RestoreExact, Symbol.Folder);
        AddContextAction(menu, S[TextId.Action_Preview], Model.Preview, Symbol.ViewAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddContextAction(menu, S[TextId.Action_DeleteVersion], Model.DeleteVersion, Symbol.Delete);
        _contextVersion = row;
        ShowContextMenu(menu, view, container, pointer, position);
    }
    private static void AddContextAction(MenuFlyout menu, string text, AsyncCommand command, Symbol icon)
        => menu.Items.Add(new MenuFlyoutItem { Text = text, Command = command, Icon = new SymbolIcon(icon) });
    private void ShowContextMenu(MenuFlyout menu, ListViewBase view, FrameworkElement container, bool pointer, global::Windows.Foundation.Point position)
    {
        _contextMenu = menu; _contextItems = Model.SelectedItems.ToArray();
        _contextRoot = Model.CurrentRoot; _contextTime = Model.AtUtc; _contextList = Model.IsList;
        menu.Closed += (_, _) => { if (ReferenceEquals(_contextMenu, menu)) { _contextMenu = null; _contextItems = []; _contextVersion = null; } };
        if (pointer) menu.ShowAt(view, position); else menu.ShowAt(container);
    }
    private void CheckContextMenu()
    {
        if (_contextMenu is null) return;
        if (!Model.CanNavigate || Model.NeedsRefresh || !ReferenceEquals(_contextRoot, Model.CurrentRoot) || _contextTime != Model.AtUtc ||
            _contextList != Model.IsList || _contextItems.Length != Model.SelectedItems.Count ||
            _contextItems.Where((item, index) => !ReferenceEquals(item, Model.SelectedItems[index])).Any() ||
            _contextVersion is not null && !ReferenceEquals(_contextVersion, Model.SelectedVersion)) HideContextMenu();
    }
    private void ContextMenuCanceled(UIElement sender, RoutedEventArgs args) => HideContextMenu();
    private void HideContextMenu()
    {
        _contextGeneration++; var menu = _contextMenu; _contextMenu = null; _contextItems = []; _contextVersion = null; menu?.Hide();
    }
    private async void UseAsOfClicked(object sender, RoutedEventArgs args)
    {
        if (AsOfDate.Date is not { } date || AsOfTime.Time < TimeSpan.Zero) return;
        await Model.UseLocalTimeAsync(date.Date, AsOfTime.Time);
        TimeFlyout.Hide();
    }
    private void ChooseTimeClicked(object sender, RoutedEventArgs args)
    {
        var local = (Model.AtUtc ?? DateTimeOffset.Now).ToLocalTime();
        AsOfDate.Date = local; AsOfTime.Time = local.TimeOfDay;
    }
    private async void CleanupClicked(object sender, RoutedEventArgs args) => await _cleanup.ShowAsync(XamlRoot);
}
