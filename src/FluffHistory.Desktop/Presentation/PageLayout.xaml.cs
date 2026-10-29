using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FluffHistory.Desktop.Presentation;

/// <summary>One page frame: shared title, bounded body, optional footer and viewport-safe scrolling.</summary>
public sealed partial class PageLayout : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(UIElement), typeof(PageLayout), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(nameof(Body), typeof(UIElement), typeof(PageLayout), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(UIElement), typeof(PageLayout), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ScrollableProperty = DependencyProperty.Register(nameof(Scrollable), typeof(bool), typeof(PageLayout), new PropertyMetadata(true, Changed));
    public UIElement? Header { get => (UIElement?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public UIElement? Body { get => (UIElement?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public UIElement? Footer { get => (UIElement?)GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public bool Scrollable { get => (bool)GetValue(ScrollableProperty); set => SetValue(ScrollableProperty, value); }
    private readonly ContentPresenter _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24, 0, 24, 20) };
    private readonly Grid _viewport = new();
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private bool _ready;
    public PageLayout() { InitializeComponent(); _body.MaxWidth = (double)Microsoft.UI.Xaml.Application.Current.Resources["PageContentMaxWidth"]; _ready = true; Apply(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) { if (sender is PageLayout { _ready: true } layout) layout.Apply(); }
    private void Apply()
    {
        HeaderHost.Content = Header; FooterHost.Content = Footer;
        BodyHost.Children.Clear(); _scroll.Content = null; _viewport.Children.Clear();
        _body.Content = Body;
        if (Scrollable)
        {
            _viewport.SetBinding(WidthProperty, new Binding { Source = _scroll, Path = new PropertyPath(nameof(ScrollViewer.ViewportWidth)), Mode = BindingMode.OneWay });
            _viewport.Children.Add(_body); _scroll.Content = _viewport; BodyHost.Children.Add(_scroll);
        }
        else BodyHost.Children.Add(_body);
    }
}
