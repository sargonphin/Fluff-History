using System.Buffers.Binary;
using System.Collections.Immutable;
using FluffHistory.Application;
using FluffHistory.Core;
using FluffHistory.Engine;
using FluffHistory.Storage;
using FluffHistory.Tests.Infrastructure;
using FluffHistory.Windows;
using Xunit;
using FluffHistory.Desktop.Presentation.Models;

namespace FluffHistory.Tests;

public sealed class ShellIntegrationTests
{
    private static IShellHistoryText Text => new ShellHistoryText(TestAppStrings.English);
    private static SourceMapping Map(string path, bool active = true) => SourceMapping.New(path) with { IsProtected = active };
    [Theory]
    [InlineData(@"C:\", @"c:\Folder\File.txt", "Folder/File.txt")]
    [InlineData(@"C:\Data\", @"c:\data\Folder\File.txt", "Folder/File.txt")]
    [InlineData(@"\\server\share\", @"\\SERVER\SHARE\Folder\File.txt", "Folder/File.txt")]
    public void MappingUsesNormalizedComponentsWithoutFilesystemIo(string root, string path, string expected)
    {
        var source = Map(root); var settings = AppSettings.Empty with { Sources = [source] };
        var target = ShellPathResolver.Resolve(settings, path, ShellItemKind.File)!;
        Assert.Equal(source.SourceRootId, target.SourceRootId); Assert.Equal(expected, target.RelativePath); Assert.True(target.IsActive);
        Assert.Null(ShellPathResolver.Resolve(settings, @"\\server\share2\file", ShellItemKind.File));
    }
    [Fact]
    public void InactiveChildAndItsBarrierNeverBecomeTheParentIdentity()
    {
        var parent = Map(@"C:\Data") with { AutomaticExcludedDirectories = ["child"] };
        var child = Map(@"C:\Data\Child", false);
        var settings = AppSettings.Empty with { Sources = [parent, child] };
        var target = ShellPathResolver.Resolve(settings, @"c:\data\CHILD\gone.txt", ShellItemKind.File)!;
        Assert.Equal(child.SourceRootId, target.SourceRootId); Assert.False(target.IsActive); Assert.Equal("gone.txt", target.RelativePath);
        Assert.Null(ShellPathResolver.Resolve(settings with { Sources = [parent] }, @"C:\Data\Child\gone.txt", ShellItemKind.File));
        Assert.Null(ShellPathResolver.Resolve(settings with { Sources = [parent with { AutomaticExcludedDirectories = [] }, child] }, @"C:\Data\Child\gone.txt", ShellItemKind.File));
        var active = child with { IsProtected = true };
        Assert.True(ShellPathResolver.Resolve(settings with { Sources = [parent, active] }, @"C:\Data\Child", ShellItemKind.Folder)!.IsActive);
    }
    [Fact]
    public void OriginalPathsAndSimilarLeavesNeverSupplyLiveMappings()
    {
        var mapping = Map(@"C:\Current") with { OriginalPath = @"D:\Previous" };
        var settings = AppSettings.Empty with { Sources = [mapping] };
        Assert.Null(ShellPathResolver.Resolve(settings, @"D:\Previous\a", ShellItemKind.File));
        Assert.Null(ShellPathResolver.Resolve(settings, @"C:\Current2\a", ShellItemKind.File));
        Assert.Null(ShellPathResolver.Resolve(settings with { Sources = [mapping with { RuntimePath = null }] }, @"C:\Current\a", ShellItemKind.File));
        Assert.Null(ShellPathResolver.Resolve(settings, @"C:\Current", ShellItemKind.File));
        Assert.Equal("", ShellPathResolver.Resolve(settings, @"C:\Current\", ShellItemKind.Folder)!.RelativePath);
        Assert.Null(ShellPathResolver.Resolve(settings, @"C:\Current\bad:stream", ShellItemKind.File));
    }
    [Fact]
    public async Task RecentRowsKeepExactPublicationOrderAcrossClockRollbackAndCurrentCanRestore()
    {
        using var f = new HistoryFixture();
        for (var i = 0; i < 8; i++) f.Add("Folder/a", "content" + i, i == 7 ? -1 : i);
        var root = Directory.CreateDirectory(f.Temp.Child("live")).FullName;
        var mapping = Map(root) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        var path = Path.Combine(root, "Folder", "a"); // Never existed; no source traversal is needed.
        await using var session = await ShellPathSession.OpenAsync(settings, path, ShellItemKind.File, Text);
        Assert.Equal(ShellHistoryStatus.BackedUp, session.Summary.Status); Assert.Equal(8, session.Summary.Rows.Length);
        Assert.Contains("8", session.Summary.Summary); Assert.Contains("Oldest retained version:", session.Summary.Summary);
        var exact = await session.GetRestoreAsync(session.SessionId, session.Summary.Rows[0].Token, settings);
        Assert.True(exact.Version.IsCurrent); Assert.Equal(HistoryFixture.Time.AddDays(-1), exact.Version.StoredUtc);
        using var reader = f.Repository.OpenReader(); var authority = reader.Authority;
        var middle = await session.GetRestoreAsync(session.SessionId, session.Summary.Rows[2].Token, settings);
        Assert.Equal(HistoryFixture.Time.AddDays(5), middle.Version.StoredUtc);
        var outcome = await new RestoreService().RestoreExactAsync(settings.RepositoryPath, middle.Version, path, originalSource: middle.Mapping.ToSelection());
        Assert.Equal(1, outcome.Restored); Assert.Equal("content5", File.ReadAllText(path));
        using var after = f.Repository.OpenReader(); Assert.Equal(authority, after.Authority);
        Assert.Equal(8, after.GetPathHistoryPage(new(HistoryFixture.Root, "Folder/a")).Items.Count);
    }
    [Fact]
    public async Task IncompleteCountDoesNotInventOldestOrRetainMoreThanTwentyHandles()
    {
        using var f = new HistoryFixture(); for (var i = 0; i < 130; i++) f.Add(day: i);
        var mapping = Map(f.Temp.Child("not-created")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        await using var session = await ShellPathSession.OpenBoundedAsync(settings, Path.Combine(mapping.RuntimePath!, "a"), ShellItemKind.File, 128, Text);
        Assert.Equal(20, session.Summary.Rows.Length); Assert.Contains("128+", session.Summary.Summary);
        Assert.Contains("Oldest retained version: open Timeline", session.Summary.Summary);
        Assert.DoesNotContain(session.Summary.Rows, row => row.Oldest);
        Assert.Contains("at least", session.Summary.VersionCount);
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(20)] [InlineData(25)]
    public async Task TableUsesOriginalModifiedDatesAndExactPublicationMarkers(int count)
    {
        using var f = new HistoryFixture();
        for (var i = 0; i < count; i++) f.Add("Folder/report & 目录.txt", "content" + i, day: -i, mtime: HistoryFixture.Time.AddHours(i));
        var mapping = Map(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        var before = File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json"));
        await using var session = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "Folder", "report & 目录.txt"), ShellItemKind.File, Text);
        Assert.Equal(Math.Min(count, 20), session.Summary.Rows.Length);
        Assert.All(session.Summary.Rows, row => Assert.Equal("report & 目录.txt", row.Name));
        Assert.True(session.Summary.Rows[0].Latest);
        Assert.Single(session.Summary.Rows.Where(row => row.Latest));
        Assert.Equal(count > 1 && count <= 20 ? 1 : 0, session.Summary.Rows.Count(row => row.Oldest));
        Assert.DoesNotContain(session.Summary.Rows, row => row.Latest && row.Oldest);
        Assert.Equal(HistoryFixture.Time.AddHours(count - 1).UtcTicks, session.Summary.Rows[0].ModifiedUtcTicks);
        Assert.Contains(count.ToString(), session.Summary.VersionCount);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json")));
    }
    [WindowsFact]
    public async Task UserActionReopensExactSurvivingVersionAfterCaptureReleaseAndAuthorityAdvance()
    {
        using var f = new HistoryFixture(); var old = f.Add("a.txt", "old"); f.Add("a.txt", "new", 1);
        var mapping = Map(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        await using var session = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "a.txt"), ShellItemKind.File, Text);
        var selected = session.Summary.Rows[1];
        await session.ReleaseReadCaptureAsync(); await session.ReleaseReadCaptureAsync();
        f.Add("a.txt", "newer after page opened", 2);
        var before = File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json"));
        var exact = await session.RevalidateActionAsync(session.SessionId, selected.Token, settings);
        Assert.Equal(old.Identity, exact.Version.Identity); Assert.False(exact.Version.IsCurrent);
        var result = await new WindowsHistoryActions().RestoreExactAsync(settings.RepositoryPath, exact.Version, f.Temp.Child("output"), 0);
        Assert.Equal(1, result.Copied); Assert.Equal("old", File.ReadAllText(Path.Combine(f.Temp.Child("output"), "a.txt")));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json")));
        using (var writer = f.Repository.OpenWriter()) using (var reader = f.Repository.OpenReader()) writer.RemoveVersions([old.Identity], reader.Authority);
        var afterRemoval = File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json"));
        Assert.Equal(ApplicationFailure.RefreshRequired, (await Assert.ThrowsAsync<ApplicationOperationException>(() => session.RevalidateActionAsync(session.SessionId, selected.Token, settings))).Failure);
        Assert.Equal(afterRemoval, File.ReadAllBytes(Path.Combine(settings.RepositoryPath, "state.json")));
        Assert.Equal(2, session.Summary.Rows.Length); // No automatic list update, even after an action.
    }
    [Fact]
    public async Task OnDemandRevalidationRejectsChangedMappingOfflineAndReplacementRepository()
    {
        using var f = new HistoryFixture(); f.Add();
        var mapping = Map(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        await using var session = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "a"), ShellItemKind.File, Text);
        await session.ReleaseReadCaptureAsync(); var row = session.Summary.Rows[0];
        await Assert.ThrowsAsync<ApplicationOperationException>(() => session.RevalidateActionAsync(session.SessionId, row.Token, settings with { Sources = [mapping with { RuntimePath = f.Temp.Child("other") }] }));
        var directory = settings.RepositoryPath; var moved = directory + ".offline";
        Directory.Move(directory, moved);
        try
        {
            await Assert.ThrowsAsync<ApplicationOperationException>(() => session.RevalidateActionAsync(session.SessionId, row.Token, settings));
            using (Repository.Create(directory))
                Assert.Equal(ApplicationFailure.RefreshRequired, (await Assert.ThrowsAsync<ApplicationOperationException>(() => session.RevalidateActionAsync(session.SessionId, row.Token, settings))).Failure);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); Directory.Move(moved, directory); }
    }
    [Fact]
    public void TableAndPreviewWireAreBoundedAndOldRepliesKeepTheFiveRowLayout()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new ShellRecentVersion(i.ToString("x32"), "legacy label", "report.txt", "28/10/2026", 100 + i, i == 0, i == 19)).ToImmutableArray();
        var summary = new ShellHistorySummary(ShellHistoryStatus.BackedUp, new string('a', 32), "Select a saved version.", "private backup location", rows, true, "20 versions");
        var bytes = ShellWire.Encode(new ShellTableReply(summary));
        var table = Assert.IsType<ShellTableReply>(ShellWire.Decode(bytes)).Value;
        Assert.Equal(rows.ToArray(), table.Rows.Select(row => row with { Display = "legacy label" }).ToArray());
        Assert.Empty(table.BackupLocation); Assert.Equal("20 versions", table.VersionCount);
        Assert.Equal(5, Assert.IsType<ShellOpenReply>(ShellWire.Decode(ShellWire.Encode(new ShellOpenReply(summary)))).Value.Rows.Length);
        var open = new ShellOpenRequest(@"C:\a.txt", ShellItemKind.File, "en-US", VersionTable: true);
        Assert.Equal(open, ShellWire.Decode(ShellWire.Encode(open)));
        var preview = new ShellPreviewRequest(summary.SessionId, rows[19].Token, 123456789012);
        Assert.Equal(preview, ShellWire.Decode(ShellWire.Encode(preview)));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(new ShellTableReply(summary with { Rows = rows.Add(rows[0]) })));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(new ShellTableReply(summary with { Rows = [rows[0], rows[0]] })));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(new ShellTableReply(summary with { Rows = [rows[0] with { ModifiedUtcTicks = -1 }] })));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(preview with { VersionToken = "../bad" }));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(open with { Language = null }));
    }
    [Fact]
    public async Task InactiveHistoryAndFolderTimesAreTruthfulAndOutsideMappingsDoNotOpenRepository()
    {
        using var f = new HistoryFixture(); f.Add("Folder/a", day: 0); f.Add("Folder/b", day: 1); f.Remove("Folder/a"); f.Remove("Folder/b", true);
        var mapping = Map(f.Temp.Child("missing-live"), false) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        await using var file = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "Folder", "a"), ShellItemKind.File, Text);
        Assert.Equal(ShellHistoryStatus.Inactive, file.Summary.Status); Assert.True(file.Summary.CanRestore);
        await using var folder = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "Folder"), ShellItemKind.Folder, Text);
        Assert.Equal(ShellHistoryStatus.Inactive, folder.Summary.Status); Assert.Empty(folder.Summary.Rows); Assert.False(folder.Summary.CanRestore);
        Assert.Contains("assembled from saved file versions", folder.Summary.Summary); Assert.DoesNotContain("Retained versions:", folder.Summary.Summary);
        await using var outside = await ShellPathSession.OpenAsync(settings with { RepositoryPath = "invalid" }, f.Temp.Child("unprotected"), ShellItemKind.File, Text);
        Assert.Equal(ShellHistoryStatus.NotProtected, outside.Summary.Status);
    }
    [Fact]
    public async Task TokensExpireOnDisposalOrMappingChangesAndNeverSubstituteAnExactVersion()
    {
        using var f = new HistoryFixture(); f.Add();
        var mapping = Map(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        var session = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "a"), ShellItemKind.File, Text);
        var row = session.Summary.Rows.Single();
        Assert.Equal(ApplicationFailure.RefreshRequired, (await Assert.ThrowsAsync<ApplicationOperationException>(() => session.GetRestoreAsync(Guid.NewGuid().ToString("N"), row.Token, settings))).Failure);
        await Assert.ThrowsAsync<ApplicationOperationException>(() => session.GetRestoreAsync(session.SessionId, Guid.NewGuid().ToString("N"), settings));
        await Assert.ThrowsAsync<ApplicationOperationException>(() => session.GetRestoreAsync(session.SessionId, row.Token, settings with { Sources = [mapping with { RuntimePath = f.Temp.Child("other") }] }));
        var exact = await session.GetRestoreAsync(session.SessionId, row.Token, settings); f.Add(content: "new", day: 1);
        await Assert.ThrowsAsync<ApplicationOperationException>(() => new RestoreService().RestoreExactAsync(settings.RepositoryPath, exact.Version, f.Temp.Child("output")));
        await session.DisposeAsync(); await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetRestoreAsync(session.SessionId, row.Token, settings));
    }
    [Fact]
    public async Task PasswordOnlyRepositoryReturnsRecoveryWithoutPromptOrMutation()
    {
        using var f = new HistoryFixture(true); f.Add(); var before = TemporaryDirectory.Snapshot(f.Temp.Child());
        var mapping = Map(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        await using var session = await ShellPathSession.OpenAsync(AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] }, Path.Combine(mapping.RuntimePath!, "a"), ShellItemKind.File, Text);
        Assert.Equal(ShellHistoryStatus.RecoveryRequired, session.Summary.Status); Assert.False(session.Summary.CanRestore);
        Assert.Equal(before.OrderBy(p => p.Key), TemporaryDirectory.Snapshot(f.Temp.Child()).OrderBy(p => p.Key));
    }
    [WindowsFact]
    public async Task EnrolledWindowsRepositoryProvidesSameReadOnlySummary()
    {
        using var f = new HistoryFixture(true); f.Add(); f.Repository.EnrollCurrentWindowsUser();
        var mapping = Map(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        await using var session = await ShellPathSession.OpenAsync(AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] }, Path.Combine(mapping.RuntimePath!, "a"), ShellItemKind.File, Text);
        Assert.Equal(ShellHistoryStatus.BackedUp, session.Summary.Status); Assert.Single(session.Summary.Rows);
    }
    [Fact]
    public async Task WireRoundTripsUnicodeAndRejectsUnknownOperationsFieldsLengthsAndTokens()
    {
        var request = new ShellOpenRequest(@"C:\資料\report 'quoted' & (test).txt", ShellItemKind.File);
        var bytes = ShellWire.Encode(request); Assert.Equal(request, ShellWire.Decode(bytes));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8))); // Old clients retain their exact operation/body.
        var localized = request with { Language = "sr-Latn-RS" };
        var localizedBytes = ShellWire.Encode(localized);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(localizedBytes.AsSpan(8)));
        Assert.Equal(localized, ShellWire.Decode(localizedBytes));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(request with { Language = "de-DE/../../bad" }));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(request with { Language = "" }));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(request with { Language = new string('a', 86) }));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(request with { Language = "de--DE" }));
        Assert.Equal(request, await ShellWire.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
        var restore = new ShellRestoreRequest(new string('a',32),new string('b',32),123456789012);
        Assert.Equal(restore, ShellWire.Decode(ShellWire.Encode(restore)));
        var unknown = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(8),99); Assert.Throws<InvalidDataException>(() => ShellWire.Decode(unknown));
        var extra = bytes.Concat(new byte[] { 0 }).ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(12),(uint)(extra.Length-16)); Assert.Throws<InvalidDataException>(() => ShellWire.Decode(extra));
        var oversized = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(12),ShellWire.MaximumBodyBytes+1);
        await Assert.ThrowsAsync<InvalidDataException>(() => ShellWire.ReadAsync(new MemoryStream(oversized),CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(restore with { VersionToken = "../token" }));
        Assert.Throws<InvalidDataException>(() => ShellWire.Encode(request with { Path = @"C:\bad" + '\0' }));
    }
    [WindowsFact]
    public async Task ActivationFileIsConsumedOnceAndMalformedInputDoesNotBecomeNavigation()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(WindowsShellRequests.DirectoryPath);
        var token=Guid.NewGuid().ToString("N"); var path=Path.Combine(WindowsShellRequests.DirectoryPath,token+".request");
        var request=new ShellOpenRequest(@"C:\missing\a.txt",ShellItemKind.File);
        File.WriteAllBytes(path,ShellWire.Encode(request)); Assert.Equal(request,await WindowsShellRequests.ConsumeAsync(token)); Assert.False(File.Exists(path));
        Assert.Null(await WindowsShellRequests.ConsumeAsync(token)); Assert.Null(await WindowsShellRequests.ConsumeAsync("../outside"));
        File.WriteAllBytes(path,[1,2,3]); Assert.Null(await WindowsShellRequests.ConsumeAsync(token)); Assert.False(File.Exists(path));
    }
}
