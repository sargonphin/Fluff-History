using FluffHistory.Windows;
using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using FluffHistory.Tests.Infrastructure;
using Xunit;

namespace FluffHistory.Tests;

public sealed class HistoricalPreviewTests
{
    [WindowsFact]
    public async Task PropertiesPreviewRevalidatesTheSelectedVersionWithoutUpdatingTheList()
    {
        using var f = new HistoryFixture(); var old = f.Add("report.txt", "selected historical bytes"); f.Add("report.txt", "newer bytes", 1);
        var mapping = SourceMapping.New(f.Temp.Child("live")) with { SourceRootId = HistoryFixture.Root };
        var settings = AppSettings.Empty with { RepositoryPath = f.Temp.Child(), Sources = [mapping] };
        await using var session = await ShellPathSession.OpenAsync(settings, Path.Combine(mapping.RuntimePath!, "report.txt"),
            ShellItemKind.File, new ShellHistoryText(TestAppStrings.English));
        var selected = session.Summary.Rows[1]; await session.ReleaseReadCaptureAsync(); f.Add("report.txt", "another backup", 2);
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath);
        var exact = await session.RevalidateActionAsync(session.SessionId, selected.Token, settings); var launcher = new FakeLauncher();
        using (var lease = await new WindowsHistoryActions { PreviewLauncher = launcher }.PreviewAsync(settings.RepositoryPath, exact.Version, (nint)123))
        {
            Assert.Equal("selected historical bytes", File.ReadAllText(lease.FilePath));
            Assert.Equal(old.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(lease.FilePath));
            Assert.Equal((nint)123, launcher.Owner);
        }
        Assert.Equal(2, session.Summary.Rows.Length); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void PreviewUsesExactBytesHistoricalNameTimeAndNormalOpenWithFallback(bool noAssociation, bool encrypted)
    {
        using var f = new HistoryFixture(encrypted); var old = f.Add("Folder/old.name.txt", "historical"); f.Add("Folder/old.name.txt", "newer", 2);
        var before = TemporaryDirectory.Snapshot(f.Repository.DirectoryPath); var launcher = new FakeLauncher { NoAssociation = noAssociation };
        var root = f.Temp.Child("previews"); var service = new HistoricalPreview(f.Repository);
        string directory;
        using (var lease = service.Open(old.Identity, launcher, root, (nint)123))
        {
            Assert.Equal("historical", File.ReadAllText(lease.FilePath)); Assert.Equal("old.name.txt", Path.GetFileName(lease.FilePath));
            Assert.Equal(old.Metadata.LastWriteTimeUtc.UtcDateTime, File.GetLastWriteTimeUtc(lease.FilePath));
            directory = Path.GetDirectoryName(lease.FilePath)!; Assert.Equal(root, Path.GetDirectoryName(directory)); Assert.True(Guid.TryParseExact(Path.GetFileName(directory), "N", out _));
            Assert.Equal(lease.FilePath, launcher.Path); Assert.Equal((nint)123, launcher.Owner); Assert.Equal(1, launcher.Opens); Assert.Equal(noAssociation ? 1 : 0, launcher.Choosers);
        }
        Assert.False(Directory.Exists(directory)); Assert.Equal(before, TemporaryDirectory.Snapshot(f.Repository.DirectoryPath));
    }

    [Fact]
    public void CleanupPreservesInUseAndUnrelatedContentAndLaunchFailureCleansOwnedFiles()
    {
        using var f = new HistoryFixture(); var v = f.Add("preview.txt"); var root = f.Temp.Child("previews");
        var preview = new HistoricalPreview(f.Repository);
        using var lease = preview.Open(v.Identity, new FakeLauncher(), root); var directory = Path.GetDirectoryName(lease.FilePath)!;
        var unrelated = Path.Combine(directory, "application-output.txt"); File.WriteAllText(unrelated, "keep");
        lease.Dispose(); Assert.Equal("keep", File.ReadAllText(unrelated)); Assert.True(File.Exists(lease.FilePath));
        File.Delete(unrelated);
        using (var locked = new FileStream(lease.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            lease.Dispose(); if (OperatingSystem.IsWindows()) Assert.True(File.Exists(lease.FilePath));
        }
        lease.Dispose(); Assert.False(Directory.Exists(directory));
        Assert.Throws<IOException>(() => preview.Open(v.Identity, new FakeLauncher { Fail = true }, root)); Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void WrongExactIdentityDoesNotLaunchOrSubstitute()
    {
        using var f = new HistoryFixture(); var v = f.Add(); var launcher = new FakeLauncher();
        Assert.Throws<FileNotFoundException>(() => new HistoricalPreview(f.Repository).Open(v.Identity with { VersionId = Guid.NewGuid() }, launcher, f.Temp.Child("preview")));
        Assert.Equal(0, launcher.Opens);
    }

    private sealed class FakeLauncher : IHistoricalFileLauncher
    {
        internal bool NoAssociation, Fail; internal int Opens, Choosers; internal string? Path; internal nint Owner;
        public bool Open(string path, nint owner) { Path = path; Owner = owner; Opens++; if (Fail) throw new IOException("Launch failed."); return !NoAssociation; }
        public void OpenWith(string path, nint owner) { Assert.Equal(Path, path); Assert.Equal(Owner, owner); Choosers++; }
    }
}
