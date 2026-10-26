using System.Collections.Immutable;

namespace FluffHistory.Application;

public enum ShellRestoreStatus { NeedsAttention, Cancelled, Restored, NothingRestored, RefreshRequired, Unavailable, CancelledInspect }

/// <summary>Presentation boundary for the out-of-process page broker. Implemented with product
/// resources by Desktop; no localization framework, language setting or repository logic here.</summary>
public interface IShellHistoryText
{
    string Empty(ShellHistoryStatus status);
    string Folder(ImmutableArray<DateTimeOffset> times);
    string File(DateTimeOffset? latest, DateTimeOffset? oldest, int count, bool complete);
    string Version(BrowserVersion version);
    string Restore(ShellRestoreStatus status);
}
