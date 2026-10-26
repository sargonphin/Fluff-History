using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace FluffHistory.Localization;

internal static class PseudoCatalog
{
    private static readonly Regex Protected = new(@"\{\{|\}\}|\{[^{}]*\}|Fluff-History|Fish & Dog Software|Microsoft(?: [Aa]ccount)?|Windows(?: Credential Locker)?|AppData|ProgramData|EFS|KiB", RegexOptions.CultureInvariant);
    private const string Plain = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Accented = "áƀçďéƒğħíĵķĺḿńóṕɋŕśťúṽŵẋýžÁƁÇĎÉƑĞĦÍĴĶĹḾŃÓṔɊŔŚŤÚṼŴẊÝŽ";
    internal static string Transform(string value)
    {
        var leading = new string(value.TakeWhile(char.IsWhiteSpace).ToArray());
        var trailing = new string(value.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
        var core = value.Substring(leading.Length, value.Length - leading.Length - trailing.Length);
        var text = new StringBuilder(); var position = 0;
        void Accent(string part)
        {
            foreach (var c in part) { var index = Plain.IndexOf(c); text.Append(index < 0 ? c : Accented[index]); }
        }
        foreach (Match match in Protected.Matches(core))
        {
            Accent(core[position..match.Index]); text.Append(match.Value); position = match.Index + match.Length;
        }
        Accent(core[position..]);
        var padding = Math.Max(0, (int)Math.Ceiling(core.Length * .35) - 2);
        return leading + "[" + text + new string('~', padding) + "]" + trailing;
    }
    internal static void Generate(string root, string output)
    {
        root = Path.GetFullPath(root); output = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (output.Equals(root, comparison) || output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Pseudo-localization output must be outside the source repository and shipping resources.");
        var document = CatalogValidation.Load(Path.Combine(root, "src/FluffHistory.Desktop/Strings/en-US/Resources.resw"));
        var english = CatalogValidation.Read(document);
        foreach (var entry in document.Root!.Elements("data"))
        {
            var key = entry.Attribute("name")!.Value;
            entry.Element("value")!.Value = key is "UILanguage" or "Shell_ResourceLanguage" ? "qps-ploc" : Transform(english[key]);
        }
        var errors = CatalogValidation.Compare(english, CatalogValidation.Read(document), "qps-ploc");
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        var directory = Path.Combine(output, "Strings/qps-ploc"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Resources.resw");
        using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) document.Save(writer);
        Console.WriteLine("Nonshipping pseudo catalog generated: " + path);
    }
}
