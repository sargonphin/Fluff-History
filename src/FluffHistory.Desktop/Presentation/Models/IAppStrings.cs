using System.Globalization;

namespace FluffHistory.Desktop.Presentation.Models;

/// <summary>Presentation only. UI language and the user's number/date culture are independent.</summary>
public interface IAppStrings
{
    string Language { get; }
    CultureInfo FormatCulture { get; }
    string this[TextId id] { get; }
    string Format(TextId id, params object?[] arguments) => string.Format(FormatCulture, this[id], arguments);
    bool IsRightToLeft => CultureInfo.GetCultureInfo(Language).TextInfo.IsRightToLeft;
}

