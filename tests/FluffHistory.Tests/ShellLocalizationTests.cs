using System.Globalization;
using FluffHistory.Application;
using FluffHistory.Desktop.Presentation.Models;
using Xunit;

namespace FluffHistory.Tests;

public sealed class ShellLocalizationTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("ja-JP")]
    public void BrokerUsesCapturedUiLanguageWithIndependentRegionalFormatting(string language)
    {
        var region = CultureInfo.GetCultureInfo("fr-FR");
        IAppStrings strings = new TestAppStrings(language, region);
        var text = new ShellHistoryText(strings);
        var latest = new DateTimeOffset(2026, 10, 24, 13, 17, 0, TimeSpan.Zero);
        var oldest = latest.AddYears(-2);
        var summary = text.File(latest, oldest, 12345, complete: true);
        Assert.Equal(strings.Format(TextId.Shell_FileSummary, latest.ToLocalTime(), 12345, oldest.ToLocalTime()), summary);
        Assert.Contains(12345.ToString("N0", region), summary);
        Assert.Equal(strings.Format(TextId.Shell_FileSummaryPartial, latest.ToLocalTime(), 12345),
            text.File(latest, null, 12345, complete: false));
        Assert.Equal(strings[TextId.Shell_NoFileVersions], text.File(null, null, 0, complete: true));
        Assert.Equal(strings.Format(TextId.Shell_FolderSummary, strings[TextId.Shell_NoFolderVersions]), text.Folder([]));
        Assert.Equal(strings.Format(TextId.Shell_FolderSummary, latest.ToLocalTime().ToString("f", region)), text.Folder([latest]));
        Assert.Equal(strings[TextId.Shell_EmptyRecovery], text.Empty(ShellHistoryStatus.RecoveryRequired));
        Assert.Equal(strings[TextId.Shell_ExpiredRefresh], text.Empty(ShellHistoryStatus.Expired));
        Assert.Equal(strings[TextId.Shell_Restored], text.Restore(ShellRestoreStatus.Restored));
        Assert.Equal(strings[TextId.Shell_RestoreCancelled], text.Restore(ShellRestoreStatus.Cancelled));
        Assert.Equal(strings[TextId.Shell_RestoreRefresh], text.Restore(ShellRestoreStatus.RefreshRequired));
        Assert.Equal(strings[TextId.Shell_RestoreUnavailable], text.Restore(ShellRestoreStatus.Unavailable));
        if (language != "en-US")
        {
            Assert.NotEqual(TestAppStrings.English[TextId.Shell_EmptyRecovery], text.Empty(ShellHistoryStatus.RecoveryRequired));
            Assert.NotEqual(TestAppStrings.English[TextId.Shell_Restored], text.Restore(ShellRestoreStatus.Restored));
        }
    }

    [Fact]
    public void BrokerCannotPresentAnExactOldestTimeForAnIncompleteTraversal()
    {
        var text = new ShellHistoryText(TestAppStrings.English);
        var time = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => text.File(time, null, 1, complete: true));
        Assert.Throws<ArgumentException>(() => text.File(null, time, 1, complete: false));
        Assert.Throws<ArgumentException>(() => text.File(time, time, -1, complete: true));
    }
}
