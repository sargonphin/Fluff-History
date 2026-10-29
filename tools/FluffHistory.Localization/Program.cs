using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using FluffHistory.Localization;

if (args.Length < 2) throw new ArgumentException("Usage: localization <audit|guard|guard-inventory|test-guards|pseudo|generate|generate-native|generate-installer|validate> <repository> [output|--check|locale]");
var root = Path.GetFullPath(args[1]);
if (args[0] == "generate-native") NativeResources.Generate(root, args.Length > 2 && args[2] == "--check");
else if (args[0] == "generate-installer") InstallerResources.Generate(root, args.Length > 2 && args[2] == "--check");

else if (args[0] == "pseudo") PseudoCatalog.Generate(root, args[2]);
else if (args[0] == "guard") PresentationGuard.Check(root);
else if (args[0] == "test-guards") GuardChecks.Run();
else if (args[0] == "guard-inventory")
    File.WriteAllText(args[2], JsonSerializer.Serialize(PresentationGuard.Candidates(root), new JsonSerializerOptions { WriteIndented = true }));
else if (args[0] == "validate")
{
    var strings = Path.Combine(root, "src/FluffHistory.Desktop/Strings");
    var english = CatalogValidation.Read(CatalogValidation.Load(Path.Combine(strings, "en-US/Resources.resw")));
    var exceptions = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(
        Path.Combine(root, "tools/FluffHistory.Localization/IdenticalTranslations.json")))!;
    var errors = new List<string>();
    var tags = args.Length > 2 ? new[] { args[2] } : CatalogValidation.ShippingLanguages;
    if (tags.Any(t => !CatalogValidation.ShippingLanguages.Contains(t, StringComparer.Ordinal)))
        throw new ArgumentException("Validation language must be an exact shipping tag.");
    foreach (var directory in Directory.EnumerateDirectories(strings))
        if (!CatalogValidation.ShippingLanguages.Contains(Path.GetFileName(directory), StringComparer.Ordinal))
            errors.Add("Unknown/noncanonical shipping resource directory: " + Path.GetFileName(directory));
    foreach (var tag in tags)
    {
        var path = Path.Combine(strings, tag, "Resources.resw");
        if (!File.Exists(path)) { errors.Add(tag + ": missing shipping catalog"); continue; }
        try
        {
            var translated = CatalogValidation.Read(CatalogValidation.Load(path));
            errors.AddRange(CatalogValidation.ReleaseBranding(translated).Select(e => tag + ": " + e));
            errors.AddRange(CatalogValidation.Compare(english, translated, tag).Select(e => tag + ": " + e));
            if (tag != "en-US") errors.AddRange(TranslationHygiene.Check(english, translated,
                exceptions.GetValueOrDefault(tag) ?? []).Select(e => tag + ": " + e));
        }
        catch (Exception error) when (error is XmlException or InvalidDataException or FormatException) { errors.Add(tag + ": " + error.Message); }
    }
    if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
    Console.WriteLine($"Catalog validation passed: {tags.Count} languages, {english.Count} keys.");
}
else if (args[0] == "audit")
{
    var entries = new List<object>();
    foreach (var directory in new[] { "src/FluffHistory.Desktop", "src/FluffHistory.Application", "src/FluffHistory.Windows" })
    foreach (var file in PresentationGuard.SourceFiles(Path.Combine(root, directory), "*.cs"))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        foreach (var node in tree.GetRoot().DescendantNodes().Where(n => n is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax))
        {
            var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax);
            var name = member switch { MethodDeclarationSyntax m => m.Identifier.Text, ConstructorDeclarationSyntax c => c.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text, FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)), _ => "" };
            entries.Add(new { file = Path.GetRelativePath(root, file).Replace('\\', '/'), member = name, line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
                value = node is LiteralExpressionSyntax l ? l.Token.ValueText : node.ToString(), expression = node.ToString() });
        }
    }
    File.WriteAllText(args[2], JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"Audit inventory: {entries.Count} literals/format expressions. Classification requires presentation review.");
}
else if (args[0] == "generate")
{
    var catalog = CatalogValidation.Read(CatalogValidation.Load(Path.Combine(root, "src/FluffHistory.Desktop/Strings/en-US/Resources.resw")));
    var keys = catalog.Keys.Where(k => !k.Contains('.')).Order(StringComparer.Ordinal).ToArray();
    if (keys.Any(k => !SyntaxFacts.IsValidIdentifier(k))) throw new InvalidDataException("Imperative resource names must be valid typed identifiers.");
    var source = "// Generated by tools/FluffHistory.Localization; edit the canonical RESW.\nnamespace FluffHistory.Desktop.Presentation.Models;\n\npublic enum TextId\n{\n" +
        string.Join("\n", keys.Select(k => "    " + k + ",")) + "\n}\n";
    var path = Path.Combine(root, "src/FluffHistory.Desktop/Presentation/Models/TextId.g.cs");
    if (args.Length > 2 && args[2] == "--check")
    {
        if (File.ReadAllText(path).Replace("\r\n", "\n") != source) throw new InvalidDataException("Generated resource identifiers are stale. Run generate.");
        Console.WriteLine($"Generated resource identifiers current: {keys.Length}.");
    }
    else { File.WriteAllText(path, source); Console.WriteLine($"Generated {keys.Length} typed resource identifiers."); }
    LiveXamlResources.Generate(root, catalog, args.Length > 2 && args[2] == "--check");
}
else throw new ArgumentException("Unknown localization tool command.");
