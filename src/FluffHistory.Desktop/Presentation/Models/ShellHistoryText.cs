using System.Collections.Immutable;
using FluffHistory.Application;

namespace FluffHistory.Desktop.Presentation.Models;

/// <summary>Resource-backed presentation for the isolated Shell broker; no XAML or repository operations.</summary>
public sealed class ShellHistoryText(IAppStrings strings) : IShellHistoryText
{
    public string Empty(ShellHistoryStatus status) => strings[status switch
    {
        ShellHistoryStatus.NotProtected => TextId.Shell_EmptyNotProtected,
        ShellHistoryStatus.RecoveryRequired => TextId.Shell_EmptyRecovery,
        ShellHistoryStatus.Expired => TextId.Shell_ExpiredRefresh,
        _ => TextId.Shell_EmptyUnavailable
    }];
    public string Folder(ImmutableArray<DateTimeOffset> times) => strings.Format(TextId.Shell_FolderSummary,
        times.Length == 0 ? strings[TextId.Shell_NoFolderVersions] : string.Join("\n", times.Select(t => t.ToLocalTime().ToString("f", strings.FormatCulture))));
    public string File(DateTimeOffset? latest, DateTimeOffset? oldest, int count, bool complete)
    {
        if (count == 0) return strings[TextId.Shell_NoFileVersions];
        if (count < 0 || latest is null || complete && oldest is null) throw new ArgumentException("Invalid Shell summary data.");
        return complete ? strings.Format(TextId.Shell_FileSummary, latest.Value.ToLocalTime(), count, oldest!.Value.ToLocalTime()) :
            strings.Format(TextId.Shell_FileSummaryPartial, latest.Value.ToLocalTime(), count);
    }
    public string Version(BrowserVersion version) => strings.Format(TextId.Shell_VersionRow,
        version.StoredUtc.ToLocalTime(), strings.Count(CountKind.Bytes, version.Length), version.LastWriteUtc.ToLocalTime());
    public string Restore(ShellRestoreStatus status) => strings[status switch
    {
        ShellRestoreStatus.NeedsAttention => TextId.Shell_RestoreAttention, ShellRestoreStatus.Cancelled => TextId.Shell_RestoreCancelled,
        ShellRestoreStatus.Restored => TextId.Shell_Restored, ShellRestoreStatus.NothingRestored => TextId.Shell_RestoreNothing,
        ShellRestoreStatus.RefreshRequired => TextId.Shell_RestoreRefresh, ShellRestoreStatus.CancelledInspect => TextId.Shell_RestoreCancelledInspect,
        _ => TextId.Shell_RestoreUnavailable
    }];
}
