using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluffHistory.Localization;

internal static class InstallerResources
{
    internal static void Generate(string root, bool check)
    {
        var installer = Path.Combine(root, "installer");
        using var sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(installer, "LanguageSources.json")));
        var catalogs = Path.Combine(root, "src/FluffHistory.Desktop/Strings");
        var english = CatalogValidation.Read(CatalogValidation.Load(Path.Combine(catalogs, "en-US/Resources.resw")));
        var keys = english.Keys.Where(k => k.StartsWith("Installer_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) throw new InvalidDataException("Missing canonical installer messages.");
        var languages = new StringBuilder("; Generated from pinned Inno bases and product RESW catalogs.\n[Languages]\n");
        var messages = new StringBuilder("\n[CustomMessages]\n");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new List<string>();
        foreach (var entry in sources.RootElement.GetProperty("files").EnumerateArray())
        {
            var tag = entry.GetProperty("language").GetString()!;
            var file = entry.GetProperty("file").GetString()!;
            if (!CatalogValidation.ShippingLanguages.Contains(tag, StringComparer.Ordinal) || !seen.Add(tag) ||
                file != Path.GetFileName(file) || file.Contains('\\') || file.Contains('/'))
                throw new InvalidDataException("Invalid/duplicate installer language source.");
            var baseText = Normalize(File.ReadAllText(Path.Combine(installer, "Languages", file)));
            if (Hash(baseText) != entry.GetProperty("textSha256").GetString())
                throw new InvalidDataException("Pinned Inno language source changed: " + tag);
            var path = Path.Combine(catalogs, tag, "Resources.resw");
            if (!File.Exists(path)) continue;
            var values = CatalogValidation.Read(CatalogValidation.Load(path));
            // Migration checkpoints can emit a partial matrix. Shipping validation separately
            // requires every Desktop catalog; no missing custom message is filled with English.
            if (keys.Any(k => !values.ContainsKey(k))) continue;
            ValidateAccelerators(values);
            var name = tag.Replace("-", "", StringComparison.Ordinal);
            languages.Append("Name: \"").Append(name).Append("\"; MessagesFile: \"Languages\\").Append(file).Append("\"\n");
            foreach (var key in keys)
            {
                var value = values[key];
                if (value.Contains('{') || value.Contains('}')) throw new InvalidDataException("Unexpected Inno constant in " + key);
                messages.Append(name).Append('.').Append(key).Append('=').Append(value.Replace("%", "%%").Replace("\r\n", "\n").Replace("\n", "%n")).Append('\n');
            }
            emitted.Add(tag);
        }
        var exceptions = sources.RootElement.GetProperty("exceptions").EnumerateArray().Select(e => e.GetProperty("language").GetString()!).ToArray();
        if (seen.Intersect(exceptions, StringComparer.OrdinalIgnoreCase).Any() ||
            !seen.Concat(exceptions).Order(StringComparer.Ordinal).SequenceEqual(CatalogValidation.ShippingLanguages.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Installer base sources/exceptions must account for every shipping language exactly once.");
        var output = languages.Append(messages).ToString();
        var directory = Path.Combine(installer, "Generated"); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "Localization.iss");
        if (check)
        {
            if (!File.Exists(target) || Normalize(File.ReadAllText(target)) != output)
                throw new InvalidDataException("Stale installer resources. Run generate-installer.");
        }
        else File.WriteAllText(target, output, new UTF8Encoding(true));
        Console.WriteLine($"Installer resources {(check ? "current" : "generated")}: {emitted.Count} languages, {keys.Length} custom messages; {exceptions.Length} documented base exception.");
    }

    private static void ValidateAccelerators(IReadOnlyDictionary<string, string> values)
    {
        var accelerators = new List<char>();
        foreach (var key in new[] { "Installer_DesktopShortcut", "Installer_StartMenuShortcut" })
        {
            var text = values[key]; var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '&') continue;
                if (i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1])) throw new InvalidDataException("Invalid installer accelerator: " + key);
                count++; accelerators.Add(char.ToUpperInvariant(text[++i]));
            }
            if (count != 1) throw new InvalidDataException("Exactly one installer task accelerator required: " + key);
        }
        if (accelerators[0] == accelerators[1]) throw new InvalidDataException("Duplicate installer task accelerators.");
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
