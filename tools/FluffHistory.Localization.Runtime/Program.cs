using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("Usage: runtime-probe <published-payload> <nonshipping-fixture.pri> <result.json>");
if (!Native.SetDllDirectoryW(Path.GetFullPath(args[0]))) throw new System.ComponentModel.Win32Exception();
WinRT.ComWrappersSupport.InitializeComWrappers();
// Validate the actual shipping index, including the default unpackaged context.
// A synthetic fallback fixture alone cannot detect a missing product PRI.
var productPri = Path.Combine(Path.GetFullPath(args[0]), "FluffHistory.Desktop.pri");
if (!File.Exists(productPri)) throw new InvalidDataException("Missing product resource index.");
var product = new ResourceManager(productPri);
var productResources = product.MainResourceMap.GetSubtree("Resources");
string ProductValue(string key, ResourceContext context) => productResources.GetValue(key, context).ValueAsString;
var defaultLanguage = ProductValue("UILanguage", product.CreateResourceContext());
if (string.IsNullOrWhiteSpace(defaultLanguage)) throw new InvalidDataException("Missing default product language.");
var catalogs = new[] { "en-US", "de-DE", "fr-FR", "ar-SA", "ja-JP" };
foreach (var tag in catalogs)
{
    var context = product.CreateResourceContext(); context.QualifierValues["Language"] = tag;
    if (ProductValue("UILanguage", context) != tag ||
        string.IsNullOrWhiteSpace(ProductValue("Shell_CommandTitle", context)) ||
        string.IsNullOrWhiteSpace(ProductValue("History_TextBlock_PageTitle/Text", context)))
        throw new InvalidDataException("Product PRI lookup failed: " + tag);
}
Console.WriteLine($"Unpackaged product PRI passed: default {defaultLanguage}, five representative locales.");
var manager = new ResourceManager(Path.GetFullPath(args[1]));
var resources = manager.MainResourceMap.GetSubtree("Resources");
var evidence = new List<object>();
foreach (var tag in new[] { "en-US", "de-DE", "de-AT", "is-IS" })
{
    var context = manager.CreateResourceContext(); context.QualifierValues["Language"] = tag;
    string Value(string key) => resources.GetValue(key, context).ValueAsString;
    var before = Value("Translated"); var fallback = Value("EnglishOnly");
    var formatted = string.Format(CultureInfo.GetCultureInfo("fr-FR"), Value("EnglishOnlyFormat"), 1234);
    var after = Value("Translated"); var language = Value("UILanguage");
    var german = tag is "de-DE" or "de-AT"; // Expected fixture results, not a product language-selection algorithm.
    if (before != (german ? "Bereit" : "Ready") || after != before || fallback != "English fallback" ||
        language != (german ? "de-DE" : "en-US") || formatted != "Files: " + 1234.ToString("N0", CultureInfo.GetCultureInfo("fr-FR")))
        throw new InvalidDataException("Windows per-key resource fallback changed locale or formatting: " + tag);
    evidence.Add(new { requested = tag, selected = language, before, fallback, after, formatted });
}
File.WriteAllText(args[2], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Windows MRT per-key fallback passed: selected, missing, selected again, unchanged language and formatted English fallback.");

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetDllDirectoryW(string path);
}
