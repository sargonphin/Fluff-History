using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FluffHistory.Application;
using FluffHistory.Engine;
using Microsoft.Win32.SafeHandles;

namespace FluffHistory.Windows;

/// <summary>Resolved Windows known-folder catalog and metadata-only final-path defense. No shell icon lookups.</summary>
public sealed class WindowsSourceFolders : SourceFolderPolicy
{
    private readonly Lazy<SourceFolderPolicy> _resolved;
    private SourceFolderPolicy _classified;
    public WindowsSourceFolders(IEnumerable<string>? ownedFolders = null) : this(Catalog(ownedFolders)) { }
    private WindowsSourceFolders((List<(string, FolderPresentation)> Personal, List<SourceSafetyLocation> Safety) catalog)
        : base(catalog.Personal, catalog.Safety)
    {
        _classified = new(locations: catalog.Safety);
        _resolved = new(() =>
        {
            var safety = catalog.Safety.Concat(RecoveryLocations()).ToArray();
            var resolved = safety.Select(l => Final(l.Path) is { } path ? l with { Path = path } : l);
            // Keep both the supported known path and proven final spelling. Redirected known
            // folders must retain their default barrier without classifying an arbitrary parent.
            var classified = new SourceFolderPolicy(locations: safety.Concat(resolved).Distinct());
            Volatile.Write(ref _classified, classified); return classified;
        });
    }
    public Task InitializeAsync(CancellationToken token = default) => Task.Run(() => { token.ThrowIfCancellationRequested(); _ = _resolved.Value; }, token);
    public override FolderSafety Classify(string path) => Volatile.Read(ref _classified).Classify(path);
    public override ImmutableArray<SourceMapping> Prepare(string repository, IEnumerable<SourceMapping> sources, string? workspace = null)
        => Volatile.Read(ref _classified).Prepare(repository, sources, workspace);
    public override void ValidateAliases(string repository, IEnumerable<SourceMapping> sources, string? workspace = null)
    {
        var destination = Final(repository); var active = sources.Where(s => s.IsProtected && s.RuntimePath is not null).Take(1025).ToArray();
        if (active.Length > 1024) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderLimit, "Choose at most 1,024 folders.");
        var working = workspace is null ? null : Final(workspace);
        var paths = active.Select(s => Final(s.RuntimePath!)).ToArray();
        for (var i = 0; i < active.Length; i++)
        {
            if (destination is not null && paths[i] is { } path && SourcePaths.Overlaps(path, destination)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.SourceRepositoryOverlap, SourcePaths.OverlapMessage);
            if (paths[i] is { } resolved)
            {
                if (working is not null && SourcePaths.Within(resolved, working)) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderBlocked, new FolderSafety(SourceSafetyKind.HardBlocked).Warning);
                var safety = _resolved.Value.Classify(resolved);
                if (safety.Kind == SourceSafetyKind.HardBlocked) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderBlocked, safety.Warning);
                if (safety.RequiresConfirmation && !active[i].ForceInclude) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.ResolvedWarning, "This resolves to a Windows system or application-data folder. Select its actual location and confirm the warning first.");
            }
            for (var j = 0; j < i; j++)
                if (paths[i] is { } a && paths[j] is { } b && SourcePaths.Overlaps(a, b) && !SourcePaths.Overlaps(active[i].RuntimePath!, active[j].RuntimePath!))
                    throw ConfigurationIssues.Invalid(ConfigurationIssueCode.ResolvedOverlap, "These folders resolve to overlapping Windows locations. Select each location once.");
        }
    }
    /// <summary>Worker-only production composition. Canonical runtime paths make default exclusions
    /// apply through supported drive/mount spellings too. Persisted logical IDs/original paths stay unchanged.</summary>
    public override ImmutableArray<SourceSelection> Select(string repository, IEnumerable<SourceMapping> sources, string? workspace = null)
    {
        var mappings = sources.Take(1025).ToArray(); if (mappings.Length > 1024) throw ConfigurationIssues.Invalid(ConfigurationIssueCode.FolderLimit, "Choose at most 1,024 folders."); ValidateAliases(repository, mappings, workspace);
        var canonical = mappings.Select(s => s.RuntimePath is null ? s : s with { RuntimePath = Final(s.RuntimePath) ?? s.RuntimePath });
        var selected = _resolved.Value.Select(Final(repository) ?? repository, canonical, workspace is null ? null : Final(workspace) ?? workspace);
        // Resolve native identities on this worker, not from a UI getter or a historical path.
        // Recent/network/printer shortcuts are Windows-managed; ordinary Desktop/Start Menu links remain user data.
        var shortcutFolders = new[] { Environment.SpecialFolder.Recent, Environment.SpecialFolder.NetworkShortcuts, Environment.SpecialFolder.PrinterShortcuts }
            .Select(Environment.GetFolderPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Final(p) ?? p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var oneDriveFolders = new[] { Known("A52BBA46-E9E1-435F-B3D9-28DAA648C0F6"), Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial") }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).Select(p => Final(p!) ?? p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        selected = selected.Select(s => s with { MetadataExclusions = new(shortcutFolders, oneDriveFolders) }).ToImmutableArray();
        var profile = Final(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile is null || !selected.Any(s => s.Configuration.IsProtected && SourcePaths.Within(profile, s.DirectoryPath))) return selected;
        // Only names at the actual current profile root. Never open hive contents and
        // never apply a recursive/prefix wildcard to ordinary user data elsewhere.
        var names = Directory.EnumerateFiles(profile, "NTUSER.*", SearchOption.TopDirectoryOnly).Take(1025).Select(Path.GetFileName).OfType<string>().ToArray();
        if (names.Length > 1024) throw new InvalidDataException("Too many profile metadata names to classify safely.");
        return selected.Select(s => s with { ExcludedFiles = (s.ExcludedFiles ?? []).Concat(ProfileHiveExclusions(profile, s.DirectoryPath, names)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }).ToImmutableArray();
    }
    internal static IEnumerable<string> ProfileHiveExclusions(string profile, string source, IEnumerable<string> names)
    {
        if (!SourcePaths.Within(profile, source)) return [];
        return names.Where(IsProfileHiveName).Select(name => SourcePaths.Relative(source, Path.Combine(profile, name)));
    }
    internal static bool IsProfileHiveName(string name)
    {
        if (name.Equals("NTUSER.INI", StringComparison.OrdinalIgnoreCase) || name.Equals("NTUSER.DAT", StringComparison.OrdinalIgnoreCase) || name.Equals("NTUSER.DAT.LOG1", StringComparison.OrdinalIgnoreCase) || name.Equals("NTUSER.DAT.LOG2", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length > 260 || !name.StartsWith("NTUSER.DAT{", StringComparison.OrdinalIgnoreCase)) return false;
        var end = name.IndexOf('}', 11);
        if (end < 0 || !Guid.TryParseExact(name.AsSpan(10, end - 9), "B", out _)) return false;
        var suffix = name[(end + 1)..];
        if (suffix.Equals(".TM.blf", StringComparison.OrdinalIgnoreCase)) return true;
        const string prefix = ".TMContainer", tail = ".regtrans-ms";
        return suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && suffix.EndsWith(tail, StringComparison.OrdinalIgnoreCase) &&
            suffix.Length > prefix.Length + tail.Length && suffix[prefix.Length..^tail.Length].All(char.IsAsciiDigit);
    }
    private static (List<(string, FolderPresentation)>, List<SourceSafetyLocation>) Catalog(IEnumerable<string>? owned)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var personal = new List<(string, FolderPresentation)>(); var safety = new List<SourceSafetyLocation>();
        Personal("B4BFCC3A-DB2C-424C-B029-7FE99A87C641", "Desktop", PersonalFolderKind.Desktop);
        Personal("FDD39AD0-238F-46AF-ADB4-6C85480369C7", "Documents", PersonalFolderKind.Documents);
        Personal("374DE290-123F-4565-9164-39C4925E467B", "Downloads", PersonalFolderKind.Downloads);
        Personal("33E28130-4E1E-4676-835A-98395C3BC3BB", "Pictures", PersonalFolderKind.Pictures);
        Personal("4BD8D571-6D19-48D3-BE97-422220080E43", "Music", PersonalFolderKind.Music);
        Personal("18989B1D-99B5-455B-841C-AB7C74E4DDFC", "Videos", PersonalFolderKind.Videos);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add(windows, SourceSafetyKind.System); Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), SourceSafetyKind.System);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), SourceSafetyKind.System);
        Add(Environment.GetEnvironmentVariable("ProgramW6432"), SourceSafetyKind.System);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), SourceSafetyKind.SharedApplicationData);
        var local = Known("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        Add(local, SourceSafetyKind.ApplicationData); Add(Known("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), SourceSafetyKind.ApplicationData);
        Add(Known("A520A1A4-1780-4FF6-BD18-167343C5AF16"), SourceSafetyKind.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) Add(Path.Combine(profile, "AppData"), SourceSafetyKind.ApplicationData);
        foreach (var temp in new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMP"), Path.Combine(windows, "Temp") })
            Add(temp, SourceSafetyKind.Temporary);
        Add(Path.Combine(Path.GetTempPath(), "Fluff-History"), SourceSafetyKind.HardBlocked);
        foreach (var folder in owned ?? []) Add(folder, SourceSafetyKind.HardBlocked);
        foreach (var drive in DriveInfo.GetDrives())
        {
            Add(Path.Combine(drive.RootDirectory.FullName, "System Volume Information"), SourceSafetyKind.HardBlocked);
            Add(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin"), SourceSafetyKind.HardBlocked);
            foreach (var file in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" }) safety.Add(new(Path.Combine(drive.RootDirectory.FullName, file), SourceSafetyKind.HardBlocked, true));
        }
        var systemRoot = Path.GetPathRoot(windows)!;
        foreach (var folder in new[] { "Windows.old", "$WINDOWS.~BT", "$WINDOWS.~WS", "$WinREAgent", "Recovery" })
        { var path = Path.Combine(systemRoot, folder); if (Directory.Exists(path)) Add(path, SourceSafetyKind.Temporary); }
        return (personal, safety);
        void Personal(string id, string label, PersonalFolderKind kind) { var path = Known(id); if (!string.IsNullOrEmpty(path)) personal.Add((path, new(label, kind))); }
        void Add(string? path, SourceSafetyKind kind) { if (!string.IsNullOrWhiteSpace(path)) safety.Add(new(SourcePaths.Normalize(path), kind)); }
    }
    private static string? Known(string id)
    {
        var initialized = CoInitializeEx(0, 2); nint pointer = 0;
        try { var guid = new Guid(id); return SHGetKnownFolderPath(in guid, 0, 0, out pointer) == 0 ? Marshal.PtrToStringUni(pointer) : null; }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); if (initialized >= 0) CoUninitialize(); }
    }
    private static IEnumerable<SourceSafetyLocation> RecoveryLocations()
    {
        // Supported read-only Windows query. No UAC/elevation or undocumented ReAgent.xml parsing.
        // A hidden partition/device path is deliberately not represented as an ordinary source.
        string output;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reagentc.exe"), "/info")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return [];
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch (InvalidOperationException) { } return []; }
            if (process.ExitCode != 0) return [];
            output = process.StandardOutput.ReadToEnd(); if (output.Length > 65536) return [];
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return []; }
        var paths = new List<SourceSafetyLocation>();
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"(?:^|\s)([A-Za-z]:\\[^\r\n]+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (match.Success && Directory.Exists(match.Groups[1].Value.Trim())) paths.Add(new(match.Groups[1].Value.Trim(), SourceSafetyKind.Temporary));
        }
        return paths;
    }
    private static string? Final(string path)
    {
        var suffix = new Stack<string>(); var candidate = path;
        while (true)
        {
            using var handle = CreateFile(candidate, 0, 7, 0, 3, 0x02000000, 0);
            if (!handle.IsInvalid)
            {
                var text = new StringBuilder(32768); var count = GetFinalPathNameByHandle(handle, text, text.Capacity, 0);
                if (count == 0 || count >= text.Capacity) return null;
                try { var result = SourcePaths.Normalize(text.ToString()); while (suffix.TryPop(out var part)) result = Path.Combine(result, part); return result; }
                catch (InvalidDataException) { return null; }
            }
            if (Marshal.GetLastPInvokeError() is not (2 or 3)) return null;
            var parent = Path.GetDirectoryName(candidate); if (parent is null) return null;
            suffix.Push(Path.GetFileName(candidate)); candidate = parent;
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}
