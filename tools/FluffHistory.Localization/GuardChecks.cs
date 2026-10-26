using System.Text.Json;

namespace FluffHistory.Localization;

internal static class GuardChecks
{
    internal static void Run()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "FluffHistory-Localization-Guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var desktop = Path.Combine(temporary, "src/FluffHistory.Desktop");
            var strings = Path.Combine(desktop, "Strings/en-US"); Directory.CreateDirectory(strings);
            var tool = Path.Combine(temporary, "tools/FluffHistory.Localization"); Directory.CreateDirectory(tool);
            var exceptions = Path.Combine(tool, "PresentationLiteralExceptions.json"); File.WriteAllText(exceptions, "[]");
            File.WriteAllText(Path.Combine(strings, "Resources.resw"), "<root><data name='Title.Text'><value>Title</value></data></root>");
            var code = Path.Combine(desktop, "Example.cs"); var xaml = Path.Combine(desktop, "Example.xaml");
            var checks = 0;
            void Pass()
            {
                PresentationGuard.Check(temporary); checks++;
            }
            void Reject(string detail)
            {
                try { PresentationGuard.Check(temporary); }
                catch (InvalidDataException error) when (error.Message.Contains(detail, StringComparison.Ordinal)) { checks++; return; }
                throw new InvalidDataException("Guard regression did not reject: " + detail);
            }
            File.WriteAllText(code, "class Example { void Show() { } }"); Pass();
            File.WriteAllText(code, "class Example { string Caption => \"Ready\"; }"); Reject("Unclassified presentation literal");
            File.WriteAllText(code, "class Example { string Caption(int n) => $\"Saved {n} files\"; }"); Reject("Unclassified presentation literal");
            File.WriteAllText(code, "class Example { void Show() { throw new InvalidDataException(\"Developer diagnostic\"); } }"); Pass();
            File.WriteAllText(code, "class Example { void Show(Exception error) { label.Text = error.Message; } }"); Reject("Raw exception text");
            File.WriteAllText(code, "class Example { string Route => \"Overview\"; }");
            File.WriteAllText(exceptions, JsonSerializer.Serialize(new[] { new PresentationGuard.ExceptionEntry(
                "src/FluffHistory.Desktop/Example.cs", "Route", "Overview", "Stable internal route for this fixture.") })); Pass();
            File.WriteAllText(code, "class Example { string Route => \"History\"; }"); Reject("Stale presentation exception");
            File.WriteAllText(code, "class Example { }"); File.WriteAllText(exceptions, "[]");
            File.WriteAllText(xaml, "<Page><TextBlock Text='English text'/></Page>"); Reject("XAML presentation literal");
            File.WriteAllText(xaml, "<Page xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><TextBlock x:Uid='Missing'/></Page>"); Reject("Unknown x:Uid");
            File.WriteAllText(xaml, "<Page xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><TextBlock x:Uid='Title'/></Page>"); Pass();
            Console.WriteLine($"Presentation guard regressions passed: {checks} positive/negative cases.");
            File.WriteAllText(xaml, "<Page xmlns:l='using:FluffHistory.Desktop.Presentation'><TextBlock l:Localize.Uid='Title'/></Page>"); Pass();
            var liveCatalog = new Dictionary<string, string>
            {
                ["Title.Text"] = "Title",
                ["Title.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"] = "Accessible title"
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temporary, LiveXamlResources.GeneratedPath))!);
            LiveXamlResources.Generate(temporary, liveCatalog, check: false);
            var generated = File.ReadAllText(Path.Combine(temporary, LiveXamlResources.GeneratedPath));
            if (!generated.Contains("TextBlock.TextProperty, \"Title/Text\"", StringComparison.Ordinal) ||
                !generated.Contains("AutomationProperties.NameProperty, \"Title/[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name\"", StringComparison.Ordinal))
                throw new InvalidDataException("Live bindings lost a dependency property or PRI subtree separator.");
            LiveXamlResources.Generate(temporary, liveCatalog, check: true);
            File.AppendAllText(Path.Combine(temporary, LiveXamlResources.GeneratedPath), "// stale");
            try { LiveXamlResources.Generate(temporary, liveCatalog, check: true); throw new Exception("Stale live bindings accepted."); }
            catch (InvalidDataException error) when (error.Message.Contains("stale", StringComparison.Ordinal)) { }
            Console.WriteLine("Live XAML bindings passed: resource IDs, ordinary/attached PRI paths, deterministic generation and stale rejection.");
            var pseudoChecks = 0;
            foreach (var original in new[] { "Ready", "Fluff-History: {0:N0} files", "Stored {0:f}; {{literal}}", "\n Some Windows text.\n", "Fish & Dog Software: Microsoft Account", "Microsoft account: Windows Credential Locker, AppData, ProgramData, EFS, KiB" })
            {
                var transformed = PseudoCatalog.Transform(original);
                var english = new Dictionary<string, string> { ["Text"] = original };
                var result = new Dictionary<string, string> { ["Text"] = transformed };
                if (CatalogValidation.Compare(english, result, "en-US").Count != 0 || !transformed.Contains('[') ||
                    transformed.Length < original.Length || transformed == original)
                    throw new InvalidDataException("Pseudo-localization changed a placeholder/whitespace contract.");
                foreach (var brand in new[] { "Fluff-History", "Fish & Dog Software", "Microsoft Account", "Microsoft account", "Windows Credential Locker", "Windows", "AppData", "ProgramData", "EFS", "KiB" })
                    if (original.Contains(brand, StringComparison.Ordinal) && !transformed.Contains(brand, StringComparison.Ordinal))
                        throw new InvalidDataException("Pseudo-localization changed invariant branding.");
                pseudoChecks++;
            }
            Console.WriteLine($"Pseudo-localization transform checks passed: {pseudoChecks}.");
        }
        finally
        {
            // The generated private root has no externally supplied content or links.
            if (!Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe localization fixture path.");
            Directory.Delete(temporary, recursive: true);
        }
    }
}
