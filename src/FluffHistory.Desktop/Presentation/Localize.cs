using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FluffHistory.Desktop.Presentation;

/// <summary>Live bindings for canonical XAML resource properties. Cached sources retain
/// keys, never controls or pages. All access is on the UI thread.</summary>
public static partial class Localize
{
    private static readonly Dictionary<string, ResourceText> Texts = new(StringComparer.Ordinal);
    public static readonly DependencyProperty UidProperty = DependencyProperty.RegisterAttached(
        "Uid", typeof(string), typeof(Localize), new PropertyMetadata(null, OnUidChanged));
    public static string GetUid(DependencyObject target) => (string)target.GetValue(UidProperty);
    public static void SetUid(DependencyObject target, string value) => target.SetValue(UidProperty, value);
    private static void OnUidChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is string uid) BindResources(target, uid);
    }
    private static void Bind(DependencyObject target, DependencyProperty property, string key)
    {
        if (!Texts.TryGetValue(key, out var source)) Texts.Add(key, source = new(key));
        BindingOperations.SetBinding(target, property, new Binding
        { Source = source, Path = new PropertyPath(nameof(ResourceText.Value)), Mode = BindingMode.OneWay });
    }
    internal static void Refresh()
    {
        foreach (var text in Texts.Values.ToArray()) text.Refresh();
    }
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class ResourceText(string key) : INotifyPropertyChanged
    {
        public string Value => MrtAppStrings.Instance.Resource(key);
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Value)));
    }
}
