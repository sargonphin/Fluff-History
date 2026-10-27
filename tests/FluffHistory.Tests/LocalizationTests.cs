using System.Globalization;
using System.Xml.Linq;
using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Engine;
using FluffHistory.Localization;
using Xunit;
using System.Runtime.CompilerServices;

namespace FluffHistory.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void ReleaseBrandingGuardProtectsNormalAboutWithoutBanningDevelopmentTooling()
    {
        var normal = new Dictionary<string, string>
        {
            ["Diagnostics_TextBlock_ProductTitle.Text"] = "Fluff-History",
            ["Diagnostics_TextBlock_Capabilities.Text"] = "Scheduled backups continue when the window is closed.",
            ["DevelopmentBuildNotice"] = "Fluff-History Developer Preview"
        };
        Assert.Empty(CatalogValidation.ReleaseBranding(normal));
        foreach (var title in new[] { "Fluff-History Developer Preview", "Fluff-History Entwicklervorschau" })
            Assert.NotEmpty(CatalogValidation.ReleaseBranding(new Dictionary<string, string>(normal)
            { ["Diagnostics_TextBlock_ProductTitle.Text"] = title }));
        var missing = new Dictionary<string, string>(normal); missing.Remove("Diagnostics_TextBlock_ProductTitle.Text");
        Assert.NotEmpty(CatalogValidation.ReleaseBranding(missing));
        foreach (var key in new[] { "Diagnostics_TextBlock_PreviewTitle.Text", "Diagnostics_TextBlock_PreviewCapabilities.Text" })
            Assert.NotEmpty(CatalogValidation.ReleaseBranding(new Dictionary<string, string>(normal) { [key] = "Fluff-History" }));
        Assert.NotEmpty(CatalogValidation.ReleaseBranding(new Dictionary<string, string>(normal)
        { ["Diagnostics_TextBlock_Capabilities.Text"] = "Developer Preview features" }));
    }

    [Fact]
    public void CanonicalCatalogCoversEveryTypedIdentifierAndCountCategory()
    {
        var english = CatalogValidation.Read(CatalogValidation.Load(Path.Combine(AppContext.BaseDirectory, "Localization/en-US/Resources.resw")));
        Assert.Equal(Enum.GetNames<TextId>().Order(StringComparer.Ordinal), english.Keys.Where(k => !k.Contains('.')).Order(StringComparer.Ordinal));
        foreach (var kind in Enum.GetValues<CountKind>())
        foreach (var category in Enum.GetValues<CardinalCategory>())
            Assert.True(english.ContainsKey($"Count_{kind}_{category}"));
        Assert.Equal(44, CatalogValidation.ShippingLanguages.Count);
        Assert.Equal(44, CatalogValidation.ShippingLanguages.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
    [Fact]
    public void ManifestLanguagesFollowTheClosedShippingSetAndKeepSparseApplicationSeparation()
    {
        var root = SourceRoot();
        foreach (var file in new[] { "src/FluffHistory.Desktop/Package.appxmanifest", "installer/Identity.appxmanifest" })
        {
            var document = XDocument.Load(Path.Combine(root, file)); var ns = document.Root!.Name.Namespace;
            Assert.Equal(CatalogValidation.ShippingLanguages,
                document.Root.Element(ns + "Resources")!.Elements(ns + "Resource").Select(e => e.Attribute("Language")!.Value));
            Assert.Equal("Sargonphin.FluffHistory.DeveloperPreview", document.Root.Element(ns + "Identity")!.Attribute("Name")!.Value);
        }
        var sparse = XDocument.Load(Path.Combine(root, "installer/Identity.appxmanifest")); var schema = sparse.Root!.Name.Namespace;
        Assert.Equal(new[] { "App", "Shell" }, sparse.Root.Element(schema + "Applications")!.Elements(schema + "Application").Select(e => e.Attribute("Id")!.Value));
        Assert.Equal("en-US", XDocument.Load(Path.Combine(root, "src/FluffHistory.Desktop/FluffHistory.Desktop.csproj")).Root!.Element("PropertyGroup")!.Element("DefaultLanguage")!.Value);
    }
    [Fact]
    public void StaticXamlUsesValidResourceIdentifiersIncludingAccessibility()
    {
        var root = SourceRoot(); var desktop = Path.Combine(root, "src/FluffHistory.Desktop");
        var files = Directory.EnumerateFiles(Path.Combine(desktop, "Pages"), "*.xaml", SearchOption.AllDirectories)
            .Append(Path.Combine(desktop, "MainWindow.xaml")).Append(Path.Combine(desktop, "App.xaml"))
            .Append(Path.Combine(desktop, "Presentation/Styles.xaml"));
        var english = TestAppStrings.Read("en-US");
        var properties = new HashSet<string>(StringComparer.Ordinal)
        { "Text", "Content", "Header", "Label", "PlaceholderText", "Title", "Message", "AutomationProperties.Name", "AutomationProperties.HelpText", "ToolTipService.ToolTip" };
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in files)
        foreach (var element in XDocument.Load(file).Descendants())
        {
            if (((string?)element.Attribute(x + "Uid") ?? (string?)element.Attribute(XName.Get("Localize.Uid", "using:FluffHistory.Desktop.Presentation"))) is { } uid)
                Assert.Contains(english.Keys, key => key.StartsWith(uid + ".", StringComparison.Ordinal));
            foreach (var attribute in element.Attributes().Where(a => properties.Contains(a.Name.LocalName)))
                Assert.True(attribute.Value.Length == 0 || attribute.Value.StartsWith('{') || attribute.Value == "Fluff-History" || !attribute.Value.Any(char.IsLetter),
                    $"Localizable literal in {Path.GetFileName(file)}: {attribute.Name}={attribute.Value}");
        }
    }
    [Fact]
    public void ValidatorRejectsMissingObsoleteDuplicateEmptyAndUnsafePlaceholders()
    {
        var english = new Dictionary<string, string> { ["Caption"] = "Caption", ["Count"] = "Count: {0:N0}" };
        Assert.Empty(CatalogValidation.Compare(english, new Dictionary<string, string> { ["Caption"] = "Titel", ["Count"] = "Anzahl: {0:N0}" }, "de-DE"));
        Assert.Contains("Missing: Count", CatalogValidation.Compare(english, new Dictionary<string, string> { ["Caption"] = "Titel" }, "de-DE"));
        Assert.Contains("Obsolete/unknown: Gone", CatalogValidation.Compare(english, new Dictionary<string, string>(english) { ["Gone"] = "Alt" }, "de-DE"));
        Assert.Contains("Placeholder mismatch: Count", CatalogValidation.Compare(english, new Dictionary<string, string>(english) { ["Count"] = "{1:N0}" }, "de-DE"));
        Assert.Contains("Placeholder mismatch: Count", CatalogValidation.Compare(english, new Dictionary<string, string>(english) { ["Count"] = "{0}" }, "de-DE"));
        Assert.Throws<InvalidDataException>(() => CatalogValidation.Read(XDocument.Parse("<root><data name='A'><value>a</value></data><data name='a'><value>b</value></data></root>")));
        Assert.Throws<InvalidDataException>(() => CatalogValidation.Read(XDocument.Parse("<root><data name='A'><value> </value></data></root>")));
        Assert.Throws<FormatException>(() => CatalogValidation.Read(XDocument.Parse("<root><data name='A'><value>{0</value></data></root>")));
        Assert.Throws<InvalidDataException>(() => CatalogValidation.Read(XDocument.Parse("<root><data name='A'><value>Text\u202e</value></data></root>")));
        Assert.Equal("متن\u200c", CatalogValidation.Read(XDocument.Parse("<root><data name='A'><value>متن\u200c</value></data></root>"))["A"]);
        var escaped = new Dictionary<string, string> { ["Value"] = "{{{0:N0}}}" };
        Assert.Empty(CatalogValidation.Compare(escaped, new Dictionary<string, string> { ["Value"] = "Number: {{{0:N0}}}" }, "de-DE"));
        Assert.Contains("Placeholder mismatch: Value", CatalogValidation.Compare(escaped, new Dictionary<string, string> { ["Value"] = "{{0:N0}}" }, "de-DE"));
    }
    [Fact]
    public void TranslationReviewExceptionsAreNarrowAndCannotMaskMissingTranslations()
    {
        var english = new Dictionary<string, string> { ["Caption"] = "Back up now", ["Confirm"] = "OK" };
        var translated = new Dictionary<string, string> { ["Caption"] = "Jetzt sichern", ["Confirm"] = "OK" };
        Assert.Contains("Unreviewed English-identical translation: Confirm", TranslationHygiene.Check(english, translated, new Dictionary<string, string>()));
        var reviewed = new Dictionary<string, string> { ["Confirm"] = "Standard Windows label." };
        Assert.Empty(TranslationHygiene.Check(english, translated, reviewed));
        Assert.Contains("Unreviewed English-identical translation: Caption", TranslationHygiene.Check(english,
            new Dictionary<string, string>(english), reviewed));
        translated["Caption"] = "\u202eText";
        Assert.Contains("Unexpected bidi formatting control: Caption", TranslationHygiene.Check(english, translated, reviewed));
        translated.Remove("Confirm");
        Assert.Contains("Obsolete/invalid identical-translation exception: Confirm", TranslationHygiene.Check(english, translated, reviewed));
    }
    [Fact]
    public void TestProviderFallbackIsPerKeyAndPreservesLanguageAndFormatting()
    {
        IAppStrings strings = new TestAppStrings("de-DE", CultureInfo.GetCultureInfo("fr-FR"), new Dictionary<string, string>
        { [nameof(TextId.Status_Ready)] = "Bereit", [nameof(TextId.Action_Cancel)] = "Abbrechen" });
        Assert.Equal("Bereit", strings[TextId.Status_Ready]);
        Assert.Equal("Backup occupies 1,5 kB", strings.Format(TextId.Storage_BackupSize, PresentationText.Size(strings, 1500)));
        Assert.Equal("Abbrechen", strings[TextId.Action_Cancel]); Assert.Equal("de-DE", strings.Language);
        Assert.Throws<KeyNotFoundException>(() => strings[(TextId)(-1)]);
    }
    [Theory]
    [InlineData("en-US", 0, CardinalCategory.Other)]
    [InlineData("en-US", 1, CardinalCategory.One)]
    [InlineData("fr-FR", 0, CardinalCategory.One)]
    [InlineData("fr-FR", 1000000, CardinalCategory.Many)]
    [InlineData("pt-BR", 0, CardinalCategory.One)]
    [InlineData("pt-PT", 0, CardinalCategory.Other)]
    [InlineData("pl-PL", 1, CardinalCategory.One)]
    [InlineData("pl-PL", 22, CardinalCategory.Few)]
    [InlineData("pl-PL", 12, CardinalCategory.Many)]
    [InlineData("ru-RU", 21, CardinalCategory.One)]
    [InlineData("ru-RU", 24, CardinalCategory.Few)]
    [InlineData("ru-RU", 111, CardinalCategory.Many)]
    [InlineData("ar-SA", 0, CardinalCategory.Zero)]
    [InlineData("ar-SA", 1, CardinalCategory.One)]
    [InlineData("ar-SA", 2, CardinalCategory.Two)]
    [InlineData("ar-SA", 3, CardinalCategory.Few)]
    [InlineData("ar-SA", 11, CardinalCategory.Many)]
    [InlineData("ar-SA", 100, CardinalCategory.Other)]
    [InlineData("he-IL", 2, CardinalCategory.Two)]
    [InlineData("sl-SI", 102, CardinalCategory.Two)]
    [InlineData("cs-CZ", 3, CardinalCategory.Few)]
    [InlineData("hr-HR", 21, CardinalCategory.One)]
    [InlineData("sr-Cyrl-RS", 23, CardinalCategory.Few)]
    [InlineData("lt-LT", 11, CardinalCategory.Other)]
    [InlineData("lv-LV", 11, CardinalCategory.Zero)]
    [InlineData("ro-RO", 101, CardinalCategory.Few)]
    [InlineData("fa-IR", 0, CardinalCategory.One)]
    [InlineData("hi-IN", 0, CardinalCategory.One)]
    [InlineData("bn-BD", 1, CardinalCategory.One)]
    [InlineData("fil-PH", 4, CardinalCategory.Other)]
    [InlineData("fil-PH", 5, CardinalCategory.One)]
    [InlineData("zh-Hans", 1, CardinalCategory.Other)]
    [InlineData("ja-JP", 2, CardinalCategory.Other)]
    public void IntegerCardinalRulesDoNotAssumeEnglish(string tag, long count, CardinalCategory expected)
        => Assert.Equal(expected, LocalizedCounts.Category(tag, count));
    [Fact]
    public void TypedSourceProblemKeepsDiagnosticsOutOfPresentation()
    {
        var result = new ScanResult();
        result.Skip(Guid.NewGuid(), "busy.txt", new IOException("Developer-only detail", new System.ComponentModel.Win32Exception(32)), SourceProblemKind.Access);
        var problem = Assert.Single(ScanSnapshot.FromResult(result).Problems);
        Assert.Equal(32, problem.NativeError); Assert.Equal(SourceProblemKind.Access, problem.Kind);
        Assert.Equal(TestAppStrings.English[TextId.SourceProblem_FileInUse], PresentationText.SourceProblem(TestAppStrings.English, problem));
        Assert.DoesNotContain("Developer-only", PresentationText.SourceProblem(TestAppStrings.English, problem));
    }
    [Fact]
    public void ValidationUsesTypedReasonWithoutChangingTheExceptionContract()
    {
        var error = Assert.Throws<InvalidDataException>(() => FolderExclusions.ValidateSource(@"C:\Data", [@"C:\Data"]));
        Assert.Equal(FolderExclusions.ConflictMessage, error.Message);
        Assert.Equal(ConfigurationIssueCode.ExclusionConflict, ConfigurationIssues.Code(error));
        Assert.Equal(TestAppStrings.English[TextId.Configuration_ExclusionConflict], PresentationText.Validation(TestAppStrings.English, error));
    }
    [Fact]
    public void RtlDirectionAndIsolationFollowUiLanguageRatherThanRegion()
    {
        IAppStrings strings = new TestAppStrings("ar-SA", CultureInfo.GetCultureInfo("de-DE"), new Dictionary<string, string>());
        Assert.True(strings.IsRightToLeft); Assert.Equal("\u2066C:\\Data\\File.txt\u2069", strings.Technical(@"C:\Data\File.txt"));
        Assert.Equal("1,5 kB", PresentationText.Size(strings, 1500));
    }
    private static string SourceRoot([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
}
