using System.Text;

namespace FluffHistory.Desktop.Presentation.Models;

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly IDesktopContext _owner;
    private readonly IDesktopDialogs _dialogs;
    public DiagnosticsViewModel(IDesktopContext owner, IDesktopDialogs dialogs)
    {
        _owner = owner; _dialogs = dialogs;
        Copy = new(() => { dialogs.CopyText(Summary); owner.LastOutcome = owner.Strings[TextId.Diagnostics_Copied]; owner.Notify(); return Task.CompletedTask; });
        owner.Changed += () => Changed(nameof(Summary));
    }
    public AsyncCommand Copy { get; }
    public string Summary
    {
        get
        {
            static string Safe(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).Take(4096).ToArray());
            var text = new StringBuilder(_dialogs.DiagnosticsHeader).AppendLine()
                .AppendLine($"Page: {_owner.CurrentPage}")
                .AppendLine($"Repository: {Safe(_owner.Settings.RepositoryPath)} · {_owner.Inspection.State} · encrypted {_owner.Inspection.Encrypted}")
                .AppendLine($"Coordinator: {_owner.Status?.Lifecycle} · scheduling {_owner.Status?.ScheduleEnabled} · phase {_owner.Status?.Progress.Phase}")
                .AppendLine($"Next UTC: {_owner.Status?.NextScheduledUtc:O} · active {_owner.Status?.IsAttemptActive} · cancel requested {_owner.Status?.CancellationRequested}")
                .AppendLine($"Last outcome: {_owner.Status?.LastActivityOutcome} · impact {_owner.Status?.LastAttempt?.Result.Impact}")
                .AppendLine($"Scan: versions {_owner.Status?.LastAttempt?.Result.Scan?.VersionsCreated} · source skips {_owner.Status?.LastAttempt?.Result.Scan?.SourceSkips} · partial {_owner.Status?.LastAttempt?.Result.Scan?.Partial} · reconciliation {_owner.Status?.LastAttempt?.Result.Scan?.ReconciliationMayBeRequired}")
                .AppendLine($"Safeguards: {_owner.Settings.Safeguards} · last reason {_owner.Status?.LastSafeguard.Reason}")
                .AppendLine($"Retention: {_owner.Settings.RetentionPolicyName} · capacity checks enabled · maintenance {_owner.Status?.LastAttempt?.Result.Maintenance}")
                .AppendLine($"Metadata maintenance: {_owner.Status?.LastAttempt?.Result.MetadataMaintenance}")
                .AppendLine($"Folders: {_owner.Settings.Sources.Count(s => s.IsProtected && s.RuntimePath is not null)} active · {_owner.Settings.Sources.Count(s => !s.IsProtected || s.RuntimePath is null)} inactive · validation {Safe(_owner.ConfigurationProblem)}")
                .AppendLine($"Browser: {Safe(_owner.BrowserActivity)}")
                .AppendLine($"Latest application failure: {_owner.LatestFailure} · error {_owner.HasError}")
                .AppendLine($"Latest safe result: {Safe(_owner.LastOutcome)}")
                .AppendLine($"Settings schema: {_owner.Settings.Schema} · {Safe(_dialogs.SettingsPath)}");
            foreach (var source in _owner.Settings.Sources.Take(32)) text.AppendLine($"Root {source.SourceRootId}: {Safe(source.OriginalPath)} → {Safe(source.RuntimePath ?? "Unmapped")} · protected {source.IsProtected}");
            return text.ToString()[..Math.Min(text.Length, 24000)];
        }
    }
}
