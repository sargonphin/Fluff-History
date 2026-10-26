using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace FluffHistory.Localization;

internal static class NativeResources
{
    // Stable IDs: append new identities; never renumber existing entries.
    private static readonly string[] Keys =
    ["CommandTitle", "Loading", "BackedUp", "Inactive", "NotProtected", "RecoveryRequired", "Unavailable", "Expired",
     "UnavailableRefresh", "ExpiredRefresh", "BackupLocation", "BrowseFolder", "OpenApp", "BrowseVersions", "FolderFooter",
     "FileFooter", "AppUnavailable", "RestoreConfirmation", "RestoreTitle", "Restoring", "UnavailableContinue", "Restore", "Refresh",
     "FolderSummary", "NoFolderVersions", "VersionRow", "NoFileVersions", "FileSummary", "FileSummaryPartial", "EmptyNotProtected",
     "EmptyRecovery", "EmptyUnavailable", "RestoreAttention", "RestoreCancelled", "Restored", "RestoreNothing", "RestoreRefresh",
     "RestoreUnavailable", "RestoreCancelledInspect", "ResourceLanguage"];
    internal static void Generate(string root, bool check)
    {
        var strings = Path.Combine(root, "src/FluffHistory.Desktop/Strings");
        var source = CatalogValidation.Read(CatalogValidation.Load(Path.Combine(strings, "en-US/Resources.resw")));
        var names = Keys.Select(k => "Shell_" + k).ToArray();
        if (!source.Keys.Where(k => k.StartsWith("Shell_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Native resource identities must explicitly cover canonical Shell keys.");
        var header = new StringBuilder("// Generated from the canonical RESW by FluffHistory.Localization.\n#pragma once\nnamespace fh {\nenum class TextId : unsigned {\n");
        for (var i = 0; i < names.Length; i++) header.Append(" ").Append(names[i]).Append(" = ").Append(3000 + i).Append(",\n");
        header.Append("};\ninline constexpr unsigned NativeTextFirst=3000, NativeTextLast=").Append(2999 + names.Length).Append(";\n}\n");
        var table = new StringBuilder("// Generated; edit the shared RESW catalogs, not this file.\n#pragma code_page(65001)\n");
        var dialogs = new StringBuilder("// Generated; language-specific dialog templates include RTL styles.\n#pragma code_page(65001)\n");
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal); var languages = new List<string>();
        foreach (var tag in CatalogValidation.ShippingLanguages)
        {
            var file = Path.Combine(strings, tag, "Resources.resw"); if (!File.Exists(file)) continue;
            var values = CatalogValidation.Read(CatalogValidation.Load(file));
            // During migration a catalog may be incomplete. Canonical delivery separately requires ALL44
            // complete catalogs; generation never silently fills native translations with English copies.
            if (names.Any(n => !values.ContainsKey(n))) continue;
            var locale = tag switch { "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", _ => tag };
            var culture = CultureInfo.GetCultureInfo(locale); var lang = culture.LCID & 0xffff;
            if (lang == 0x1000) throw new InvalidDataException("No standard native language ID: " + tag);
            var directive = $"LANGUAGE 0x{lang & 0x3ff:x}, 0x{lang >> 10:x}\n";
            table.Append("// ").Append(tag).Append('\n').Append(directive).Append("STRINGTABLE\nBEGIN\n");
            for (var i = 0; i < names.Length; i++) table.Append(' ').Append(3000 + i).Append(" \"").Append(Escape(values[names[i]])).Append("\"\n");
            table.Append("END\n");
            dialogs.Append("// ").Append(tag).Append('\n').Append(directive).Append("201 DIALOGEX 0, 0, 260, 232\nSTYLE DS_SETFONT | DS_CONTROL | WS_CHILD\n");
            if (culture.TextInfo.IsRightToLeft) dialogs.Append("EXSTYLE WS_EX_LAYOUTRTL | WS_EX_RTLREADING\n");
            dialogs.Append("FONT 9, \"Segoe UI\", 400, 0, 1\nBEGIN\n")
                .Append(" LTEXT \"").Append(Escape(values["Shell_Loading"])).Append("\",2101,10,8,240,20,SS_NOPREFIX\n")
                .Append(" LTEXT \"\",2102,10,31,240,77,SS_NOPREFIX\n LISTBOX 2103,10,112,240,65,LBS_NOTIFY | LBS_NOINTEGRALHEIGHT | WS_VSCROLL | WS_TABSTOP\n")
                .Append(" PUSHBUTTON \"").Append(Escape(values["Shell_Restore"])).Append("\",2104,10,184,58,15\n")
                .Append(" PUSHBUTTON \"").Append(Escape(values["Shell_BrowseVersions"])).Append("\",2105,75,184,106,15\n")
                .Append(" PUSHBUTTON \"").Append(Escape(values["Shell_Refresh"])).Append("\",2106,188,184,62,15\n")
                .Append(" LTEXT \"").Append(Escape(values["Shell_FileFooter"])).Append("\",2107,10,207,240,20,SS_NOPREFIX\nEND\n");
            languages.Add(tag); inputs[Path.GetRelativePath(root, file).Replace('\\', '/')] = Hash(string.Join("\0", names.SelectMany(n => new[] { n, values[n] })));
        }
        var directory = Path.Combine(root, "native/Shell/Generated"); Directory.CreateDirectory(directory);
        var outputs = new Dictionary<string, string> { ["TextIds.h"] = header.ToString(), ["Strings.rc"] = table.ToString(), ["PropertiesDialogs.rc"] = dialogs.ToString() };
        foreach (var (file, contents) in outputs)
        {
            var path = Path.Combine(directory, file);
            if (check) { if (!File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n") != contents) throw new InvalidDataException("Stale native resources: " + file); }
            else File.WriteAllText(path, contents, new UTF8Encoding(false));
        }
        var manifest = JsonSerializer.Serialize(new
        { languages, keys = names, inputs, outputs = outputs.Keys.ToDictionary(k => k, k => Hash(outputs[k])) }, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n");
        var manifestPath = Path.Combine(directory, "catalog-manifest.json");
        if (check)
        {
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath).Replace("\r\n", "\n") != manifest)
                throw new InvalidDataException("Stale native resource manifest. Run generate-native.");
        }
        else File.WriteAllText(manifestPath, manifest);
        Console.WriteLine($"Native resources {(check ? "current" : "generated")}: {languages.Count} locales, {names.Length} stable IDs.");
    }
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\"\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));
}
