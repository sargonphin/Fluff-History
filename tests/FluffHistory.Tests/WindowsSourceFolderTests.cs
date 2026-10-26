using FluffHistory.Application;
using FluffHistory.Tests.Infrastructure;
using FluffHistory.Windows;
using FluffHistory.Engine;
using Xunit;

namespace FluffHistory.Tests;

public sealed class WindowsSourceFolderTests
{
    [Fact]
    public void MetadataPolicyPreservesUserShortcutsDotfilesAndPreviouslySavedHistory()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed("desktop.ini");
        foreach (var name in new[] { "Thumbs.db", "Desktop/user.lnk", "Start Menu/custom.lnk", "Recent/managed.lnk", ".gitignore",
            "OneDrive/.gitignore", "OneDrive/.user", "OneDrive/.849C9593-D756-4E56-8D6E-42412F2A707B", ".849C9593-D756-4E56-8D6E-42412F2A707B",
            "OneDrive/system-state", "elsewhere/system-state", "system.lnk", "ordinary.ini", "NTUSER.DAT-notes.txt" })
        {
            f.File(name);
        }
        f.Access.Metadata = (path, metadata) => Path.GetFileName(path) is "system-state" or "system.lnk" ? metadata with { Attributes = FileAttributes.Hidden | FileAttributes.System } : metadata;
        var policy = new SourceMetadataExclusions([Path.Combine(f.Source, "Recent")], [Path.Combine(f.Source, "OneDrive")]);
        var result = new FileHistoryEngine(f.Repository, new(), f.Access).Scan([f.Selection with { MetadataExclusions = policy }]);
        Assert.Equal(0, result.SourceSkips); Assert.Equal(0, result.Removals);
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
        var names = reader.EnumerateCurrent().Select(v => v.Metadata.RelativePath).ToArray();
        foreach (var kept in new[] { "Desktop/user.lnk", "Start Menu/custom.lnk", ".gitignore", "OneDrive/.gitignore", "OneDrive/.user", ".849C9593-D756-4E56-8D6E-42412F2A707B", "elsewhere/system-state", "ordinary.ini", "NTUSER.DAT-notes.txt" }) Assert.Contains(kept, names);
        foreach (var excluded in new[] { "Thumbs.db", "Recent/managed.lnk", "OneDrive/.849C9593-D756-4E56-8D6E-42412F2A707B", "OneDrive/system-state", "system.lnk" }) Assert.DoesNotContain(excluded, names);
    }

    [Fact]
    public void OnlyDocumentedCompatibilityJunctionSignatureIsSilentlyBlockedWithoutTraversal()
    {
        using var f = new LiveCapacityFixture(); f.File("legacy/never-read"); f.File("ordinary-link/never-read"); f.File("desktop.ini/user.txt");
        f.Access.Metadata = (path, metadata) => Path.GetFileName(path) switch
        {
            "legacy" => metadata with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System },
            "ordinary-link" => metadata with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint },
            _ => metadata
        };
        f.Access.BeforeOpen = path => Assert.DoesNotContain("never-read", path);
        var result = new FileHistoryEngine(f.Repository, new(), f.Access).Scan([f.Selection with { MetadataExclusions = new([], []) }]);
        Assert.Equal("ordinary-link", Assert.Single(result.Problems).RelativePath); Assert.Equal(1, result.VersionsCreated);
        using var reader = f.Repository.OpenReader(); Assert.Equal("desktop.ini/user.txt", Assert.Single(reader.EnumerateCurrent()).Metadata.RelativePath);
    }

    [WindowsFact]
    public void LockedOneDriveMarkerAndShellMetadataAreNeverOpened()
    {
        using var f = new LiveCapacityFixture(); var marker = f.File(".849C9593-D756-4E56-8D6E-42412F2A707B"); var desktop = f.File("desktop.ini"); f.File("user.lnk");
        using var markerLock = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
        using var desktopLock = new FileStream(desktop, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = new FileHistoryEngine(f.Repository).Scan([f.Selection with { MetadataExclusions = new([], [f.Source]) }]);
        Assert.Empty(result.Problems); Assert.Equal(1, result.VersionsCreated);
    }

    [Fact]
    public void ProfileHiveNamesAreExactRootMetadataAndNeverArbitraryPrefixFiles()
    {
        var transaction = "NTUSER.DAT{12345678-1234-1234-1234-123456789ABC}";
        var names = new[] { "NTUSER.DAT", "ntuser.dat.LOG1", "NTUSER.DAT.LOG2", transaction + ".TM.blf", transaction + ".TMContainer00000000000000000001.regtrans-ms", "NTUSER.DAT-notes.txt", "NTUSER.DAT{not-a-guid}.TM.blf" };
        var excluded = WindowsSourceFolders.ProfileHiveExclusions(@"C:\Users\Alice", @"C:\Users", names).ToArray();
        Assert.Equal(5, excluded.Length); Assert.All(excluded, path => Assert.StartsWith("Alice/", path, StringComparison.Ordinal));
        Assert.Empty(WindowsSourceFolders.ProfileHiveExclusions(@"C:\Users\Alice", @"D:\Data", names));
        Assert.False(WindowsSourceFolders.IsProfileHiveName("NTUSER.DAT-notes.txt"));
        Assert.False(WindowsSourceFolders.IsProfileHiveName("nested/NTUSER.DAT"));
    }

    [Fact]
    public void AddingFutureScanFileBarrierDoesNotDeleteExistingHistory()
    {
        using var f = new LiveCapacityFixture(); var old = f.Seed("NTUSER.DAT");
        var selection = f.Selection with { ExcludedFiles = WindowsSourceFolders.ProfileHiveExclusions(f.Source, f.Source, ["NTUSER.DAT"]).ToArray() };
        var result = new FluffHistory.Engine.FileHistoryEngine(f.Repository).Scan([selection]);
        Assert.Equal(0, result.VersionsCreated); Assert.Equal(0, result.Removals);
        using var reader = f.Repository.OpenReader(); Assert.NotNull(reader.GetVersion(old.Identity));
    }

    [WindowsFact]
    public void LockedSourceProblemRetainsTheExactRelativePathAndNativeErrorCode()
    {
        using var f = new LiveCapacityFixture(); f.File("locked.txt");
        using var locked = new FileStream(Path.Combine(f.Source, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = new FluffHistory.Engine.FileHistoryEngine(f.Repository).Scan([f.Selection]);
        Assert.Equal(1, result.SourceSkips); var problem = Assert.Single(result.Problems);
        Assert.Equal("locked.txt", problem.RelativePath); Assert.Contains("(32)", problem.Reason); Assert.True(problem.Reason.Length > "Cannot open source (32): ".Length);
        Assert.Equal(0, result.VersionsCreated);
    }

    [WindowsFact]
    public async Task ExplorerKnownFolderIconsLoadOnceWithDistinctVisiblePixels()
    {
        var first = WindowsFolderIcons.GetAsync(); Assert.Same(first, WindowsFolderIcons.GetAsync());
        var icons = await first;
        Assert.Equal(Enum.GetValues<PersonalFolderKind>().Length, icons.Count);
        foreach (var pixels in icons.Values)
        {
            Assert.Equal(WindowsFolderIcons.PixelSize * WindowsFolderIcons.PixelSize * 4, pixels.Length);
            Assert.Contains(pixels.Where((_, i) => i % 4 == 3), alpha => alpha != 0);
        }
        Assert.Equal(icons.Count, icons.Values.Select(Convert.ToHexString).Distinct().Count());
    }

    [WindowsFact]
    public async Task FileTypeIconsUseBoundedExtensionKeysAndCacheWithoutSourcePaths()
    {
        var first = WindowsFileTypeIcons.GetAsync(".txt"); Assert.Same(first, WindowsFileTypeIcons.GetAsync(".TXT"));
        var pixels = await first; Assert.NotNull(pixels); Assert.Equal(64 * 64 * 4, pixels.Length);
        var generic = WindowsFileTypeIcons.GetAsync("");
        Assert.Same(generic, WindowsFileTypeIcons.GetAsync(@"C:\must-not-read\source.txt"));
        for (var i = 0; i < 140; i++) await WindowsFileTypeIcons.GetAsync(".fh" + i);
        Assert.Same(generic, WindowsFileTypeIcons.GetAsync(".overflow"));
        Assert.Equal(".jpg", FileTypeIconKey.Extension("saved/picture.JPG")); Assert.Equal("", FileTypeIconKey.Extension("file.bad%1"));
    }

    [WindowsFact]
    public void ActualWindowsCatalogResolvesPersonalFoldersAndSystemBoundaries()
    {
        var policy = new WindowsSourceFolders(); var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.Equal("Desktop", policy.Describe(desktop).Label); Assert.Equal(PersonalFolderKind.Desktop, policy.Describe(desktop).Kind);
        Assert.Equal(PersonalFolderKind.Folder, policy.Describe(Path.Combine(Path.GetTempPath(), "unrelated", "Desktop")).Kind);
        Assert.Equal(SourceSafetyKind.System, policy.Classify(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Kind);
        Assert.Equal(SourceSafetyKind.System, policy.Classify(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).Kind);
        Assert.Equal(SourceSafetyKind.SharedApplicationData, policy.Classify(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Kind);
        var appData = Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!.FullName;
        Assert.Equal(SourceSafetyKind.ApplicationData, policy.Classify(appData).Kind);
        var volume = Path.GetPathRoot(desktop)!;
        Assert.Equal(SourceSafetyKind.HardBlocked, policy.Classify(Path.Combine(volume, "System Volume Information")).Kind);
        Assert.Equal(SourceSafetyKind.HardBlocked, policy.Classify(Path.Combine(volume, "$Recycle.Bin")).Kind);
    }

    [WindowsFact]
    public void FinalPathRejectsExistingRepositoryAliasBeforeProductionComposition()
    {
        using var temp = new TemporaryDirectory(); var repository = Directory.CreateDirectory(temp.Child()).FullName;
        var alias = temp.Child("alias"); Directory.CreateSymbolicLink(alias, repository);
        try
        {
            Assert.False(SourcePaths.Overlaps(repository, alias));
            Assert.Throws<InvalidDataException>(() => new WindowsSourceFolders().ValidateAliases(repository, [SourceMapping.New(alias)]));
        }
        finally { Directory.Delete(alias); } // Remove only this exact link, never its target.
    }

    [WindowsFact]
    public void BroadProfileAliasStillGetsExactAppDataDefaultExclusionWithoutScanningIt()
    {
        using var temp = new TemporaryDirectory(); var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var alias = temp.Child("profile-alias"); Directory.CreateSymbolicLink(alias, profile);
        try
        {
            var repository = Path.Combine(Path.GetPathRoot(profile)!, "FluffHistory-uncreated-" + Guid.NewGuid().ToString("N"));
            var mapping = SourceMapping.New(alias); var selected = new WindowsSourceFolders().Select(repository, [mapping]).Single();
            Assert.Equal(mapping.SourceRootId, selected.Configuration.SourceRootId); Assert.True(SourcePaths.Same(profile, selected.DirectoryPath));
            Assert.Contains("APPDATA", selected.Configuration.ExcludedDirectories);
        }
        finally { Directory.Delete(alias); }
    }

    [WindowsFact]
    public void BroadVolumePlanExcludesExactPagingFilesAndMetadataWithoutRunningAVolumeBackup()
    {
        var volume = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var otherDrive = char.ToUpperInvariant(volume[0]) == 'Z' ? @"Y:\UncreatedBackup" : @"Z:\UncreatedBackup";
        var selection = new WindowsSourceFolders().Select(otherDrive, [SourceMapping.New(volume)]).Single();
        Assert.Equal(new[] { "hiberfil.sys", "pagefile.sys", "swapfile.sys" }, selection.ExcludedFiles!.Where(f => !f.Contains('/')).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(selection.ExcludedFiles!, f => f.EndsWith("/NTUSER.DAT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SYSTEM VOLUME INFORMATION", selection.Configuration.ExcludedDirectories);
        Assert.Contains("$RECYCLE.BIN", selection.Configuration.ExcludedDirectories);
    }
}
