using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
namespace FluffHistory.Desktop.Presentation;
public sealed class RetentionSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
