using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FluffHistory.Localization;

internal static class PresentationGuard
{
    internal sealed record ExceptionEntry(string File, string Member, string Value, string Reason);
    internal sealed record Literal(string File, string Member, string Value, int Line);
    internal static IEnumerable<string> SourceFiles(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern)) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in SourceFiles(child, pattern)) yield return file;
        }
    }
    internal static IEnumerable<Literal> Candidates(string root)
    {
        foreach (var file in SourceFiles(Path.Combine(root, "src/FluffHistory.Desktop"), "*.cs"))
        {
            // Verified against canonical RESW and XAML by generate --check.
            if (Path.GetRelativePath(root, file).Replace('\\', '/') == LiveXamlResources.GeneratedPath) continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var value = node switch
                {
                    LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal => literal.Token.ValueText,
                    InterpolatedStringExpressionSyntax text => string.Concat(text.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
                    _ => null
                };
                if (value is null || !value.Any(char.IsLetter) || value == "Fluff-History") continue;
                var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax);
                var name = member switch
                {
                    MethodDeclarationSyntax method => method.Identifier.Text, ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
                    PropertyDeclarationSyntax property => property.Identifier.Text,
                    FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text)), _ => ""
                };
                // These are programming/diagnostic exceptions, never normal UI prose. Displaying
                // exception.Message directly is separately rejected below.
                if (node.Ancestors().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax)) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative == "src/FluffHistory.Desktop/Presentation/Models/DiagnosticsViewModel.cs" && name == "Summary" ||
                    relative == "src/FluffHistory.Desktop/Presentation/WindowServices.cs" && name == "DiagnosticsHeader" ||
                    relative == "src/FluffHistory.Desktop/Presentation/Models/HistoryViewModel.cs" && name == "UpdateDiagnostics") continue;
                yield return new(relative, name, value, tree.GetLineSpan(node.Span).StartLinePosition.Line + 1);
            }
        }
    }
    internal static void Check(string root)
    {
        var source = Path.Combine(root, "src/FluffHistory.Desktop");
        var exceptions = JsonSerializer.Deserialize<ExceptionEntry[]>(File.ReadAllText(Path.Combine(root,
            "tools/FluffHistory.Localization/PresentationLiteralExceptions.json")))!;
        if (exceptions.Any(e => string.IsNullOrWhiteSpace(e.Reason)) ||
            exceptions.Select(e => (e.File, e.Member, e.Value)).Distinct().Count() != exceptions.Length)
            throw new InvalidDataException("Invalid/duplicate presentation literal exception.");
        var candidates = Candidates(root).ToArray(); var errors = new List<string>();
        foreach (var literal in candidates)
            if (!exceptions.Any(e => e.File == literal.File && e.Member == literal.Member && e.Value == literal.Value))
                errors.Add($"Unclassified presentation literal: {literal.File}:{literal.Line} [{literal.Member}] {literal.Value}");
        foreach (var exception in exceptions)
            if (!candidates.Any(c => c.File == exception.File && c.Member == exception.Member && c.Value == exception.Value))
                errors.Add($"Stale presentation exception: {exception.File} [{exception.Member}] {exception.Value}");
        foreach (var file in SourceFiles(source, "*.cs"))
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            var exceptionNames = syntax.DescendantNodes().OfType<CatchDeclarationSyntax>().Select(c => c.Identifier.ValueText)
                .Concat(syntax.DescendantNodes().OfType<ParameterSyntax>()
                    .Where(p => p.Type?.ToString().EndsWith("Exception", StringComparison.Ordinal) == true).Select(p => p.Identifier.ValueText))
                .Where(n => n.Length != 0).ToHashSet(StringComparer.Ordinal);
            foreach (var member in syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                if (member.Name.Identifier.ValueText == "Message" && member.Expression is IdentifierNameSyntax name && exceptionNames.Contains(name.Identifier.ValueText))
                    errors.Add("Raw exception text in presentation: " + Path.GetRelativePath(root, file) + " " + member);
        }
        var english = CatalogValidation.Read(CatalogValidation.Load(Path.Combine(source, "Strings/en-US/Resources.resw")));
        var properties = new HashSet<string>(StringComparer.Ordinal)
        { "Text", "Content", "Header", "Label", "PlaceholderText", "Title", "Message", "AutomationProperties.Name", "AutomationProperties.HelpText", "ToolTipService.ToolTip" };
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in SourceFiles(source, "*.xaml"))
        foreach (var element in XDocument.Load(file).Descendants())
        {
            if (((string?)element.Attribute(x + "Uid") ?? (string?)element.Attribute(XName.Get("Localize.Uid", "using:FluffHistory.Desktop.Presentation"))) is { } uid && !english.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)))
                errors.Add("Unknown x:Uid: " + uid);
            foreach (var attribute in element.Attributes().Where(a => properties.Contains(a.Name.LocalName)))
                if (attribute.Value.Length != 0 && !attribute.Value.StartsWith('{') && attribute.Value != "Fluff-History" && attribute.Value.Any(char.IsLetter))
                    errors.Add($"XAML presentation literal: {Path.GetRelativePath(root, file)} {attribute.Name}={attribute.Value}");
        }
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        Console.WriteLine($"Presentation literal/reference guard passed: {candidates.Length} classified technical literals; ordinary text uses resources.");
    }
}
