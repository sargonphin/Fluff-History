using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FluffHistory.Localization;

internal static class ManifestResources
{
    internal static void Generate(string root, bool check)
    {
        var resources = "<Resources>\n" + string.Join("\n", CatalogValidation.ShippingLanguages.Select(t => $"  <Resource Language=\"{t}\" />")) + "\n </Resources>";
        foreach (var relative in new[] { "src/FluffHistory.Desktop/Package.appxmanifest", "installer/Identity.appxmanifest" })
        {
            var path = Path.Combine(root, relative); var source = File.ReadAllText(path).Replace("\r\n", "\n");
            var document = XDocument.Parse(source); var ns = document.Root!.Name.Namespace;
            if (document.Root.Elements(ns + "Resources").Count() != 1) throw new InvalidDataException("Ambiguous manifest language declaration: " + relative);
            var expression = new Regex("<Resources>.*?</Resources>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (expression.Matches(source).Count != 1) throw new InvalidDataException("Unexpected manifest resource template: " + relative);
            // Change only the language block. Identity, App/Shell application separation,
            // COM declarations and deployment placeholders retain their exact source text.
            var generated = expression.Replace(source, resources);
            if (check)
            {
                if (source != generated) throw new InvalidDataException("Stale manifest languages: " + relative);
            }
            else File.WriteAllText(path, generated, new UTF8Encoding(false));
        }
        Console.WriteLine($"Manifest language declarations {(check ? "current" : "generated")}: {CatalogValidation.ShippingLanguages.Count}, en-US first/default.");
    }
}
