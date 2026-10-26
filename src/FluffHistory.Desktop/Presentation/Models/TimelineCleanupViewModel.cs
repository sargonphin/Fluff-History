using FluffHistory.Application;
using System.Collections.Immutable;

namespace FluffHistory.Desktop.Presentation.Models;

public sealed class TimelineCleanupViewModel : ObservableObject
{
    public IAppStrings Strings => _owner.Strings;
    private IAppStrings S => Strings;
    private readonly IDesktopContext _owner;
    private TimelineCleanupChoice? _choice;
    private TimelineCleanupPreview? _preview;
    private long _previewRevision;
    private TimelineMaintenanceService? _service;
    private string? _path;
    private CancellationTokenSource? _cancel;
    private string InitialSummary => S[TextId.Cleanup_Initial];
    private string _summary = "";
    private string _result = "";
    private bool _confirm;
    public TimelineCleanupViewModel(IDesktopContext owner, IDesktopDialogs dialogs)
    {
        _owner = owner; _summary = InitialSummary;
        Choices = ProductConfiguration.CleanupChoices.Select(c => c with { Label = PresentationText.Cleanup(S, c.Mode) }).ToImmutableArray();
        Preview = new(() => RunAsync(async token =>
        {
            var choice = SelectedChoice; if (choice is null) return;
            _preview = null; _result = ""; _service = owner.Maintenance; _path = owner.Settings.RepositoryPath;
            _summary = S[TextId.Cleanup_Checking]; Update();
            if (_service is null) return;
            _previewRevision = owner.HistoryRevision;
            var service = _service; var path = _path;
            var preview = await service.PreviewAsync(path, choice.Mode, DateTimeOffset.UtcNow, token);
            if (_previewRevision != owner.HistoryRevision || SelectedChoice != choice || !ReferenceEquals(service, owner.Maintenance) ||
                !SourcePaths.Same(path, owner.Settings.RepositoryPath))
            { _summary = S[TextId.Cleanup_Changed]; return; }
            _preview = preview;
            _summary = $"{S.Count(CountKind.Files, preview.AffectedPaths)} · {S.Count(CountKind.SavedVersions, preview.Candidates)}\n" +
                (preview.Complete ? S.Format(TextId.Cleanup_SpaceEstimate, PresentationText.Size(S, preview.LogicalBytes)) : "") +
                (preview.Complete ? preview.Candidates == 0 ? S[TextId.Cleanup_Empty] : S[TextId.Cleanup_Review] : preview.Cancelled ? S[TextId.Cleanup_PreviewCancelled] : S[TextId.Cleanup_PreviewIncomplete]);
        }), () => IsReady && SelectedChoice is not null && !owner.StatusIsActive());
        Execute = new(() => { _confirm = true; Update(); return Task.CompletedTask; }, () => CanExecutePreview);
        ConfirmExecution = new(async () =>
        {
            if (_preview is not { Complete: true } preview || _service is null) return;
            _confirm = false; _summary = S[TextId.Cleanup_Removing];
            await RunAsync(async token =>
            {
                var service = _service; _preview = null;
                if (service is null) return;
                var result = await service.ExecuteAsync(preview, token);
                owner.TimelineChanged();
                _result = result.State == MaintenanceState.Stale ? S[TextId.Cleanup_Stale] :
                    S.Format(TextId.Cleanup_RemovedSummary, S.Count(CountKind.FileVersions, result.Removed), PresentationText.Size(S, result.LogicalBytes)) +
                    (result.CleanupDeferred > 0 || result.CleanupFailed > 0 ? S[TextId.Cleanup_PayloadDeferred] : "") +
                    (result.State == MaintenanceState.Completed ? S[TextId.Cleanup_Completed] : result.State == MaintenanceState.Cancelled ? S[TextId.Cleanup_Cancelled] : S[TextId.Cleanup_Attention]);
            });
        }, () => _confirm && CanExecutePreview);
        CancelConfirmation = new(() => { _confirm = false; Update(); return Task.CompletedTask; });
        Cancel = new(() => { _cancel?.Cancel(); return Task.CompletedTask; }, () => Busy);
        CommandGroup.Attach(owner, Update, Preview, Execute, ConfirmExecution);
        owner.Changed += () => { if (_path is not null && (!SourcePaths.Same(_path, owner.Settings.RepositoryPath) || _previewRevision != owner.HistoryRevision)) { _preview = null; _confirm = false; _choice = null; _path = null; _summary = S[TextId.Cleanup_Changed]; } Update(); };
    }
    public ImmutableArray<TimelineCleanupChoice> Choices { get; private set; }
    public TimelineCleanupChoice? SelectedChoice
    {
        get => _choice;
        set
        {
            if (Busy || value is not null && !Choices.Contains(value) || !Set(ref _choice, value)) return;
            _preview = null; _result = ""; _confirm = false; _summary = InitialSummary; Update();
            PreviewCompletion = value is null ? Task.CompletedTask : Preview.ExecuteAsync();
        }
    }
    internal Task PreviewCompletion { get; private set; } = Task.CompletedTask;
    public void Reset()
    {
        if (Busy) return;
        Choices = ProductConfiguration.CleanupChoices.Select(c => c with { Label = PresentationText.Cleanup(S, c.Mode) }).ToImmutableArray();
        _choice = null; _preview = null; _service = null; _path = null; _confirm = false;
        _summary = InitialSummary; _result = ""; Update();
    }
    public AsyncCommand Preview { get; }
    public AsyncCommand Execute { get; }
    public AsyncCommand ConfirmExecution { get; }
    public AsyncCommand CancelConfirmation { get; }
    public AsyncCommand Cancel { get; }
    public bool Busy => Preview.IsBusy || Execute.IsBusy || ConfirmExecution.IsBusy;
    public bool ConfirmRequested => _confirm;
    public string Confirmation => S.Format(TextId.Cleanup_Confirmation, S.Count(CountKind.SavedVersions, _preview?.Candidates ?? 0), S.Count(CountKind.Files, _preview?.AffectedPaths ?? 0));
    public bool CanEdit => !Busy && !_owner.StatusIsActive() && _owner.Maintenance?.IsBusy != true;
    public bool CanOpen => IsReady && !Busy && !_owner.StatusIsActive() && _owner.Maintenance?.IsBusy != true;
    public string Summary => _summary;
    public string Result => _result;
    public string Explanation => SelectedChoice is null ? "" : SelectedChoice == Choices[^1] ? S[TextId.Cleanup_KeepRecentExplanation] :
        S[TextId.Cleanup_AgeExplanation];
    public string SpaceExplanation => S[TextId.Cleanup_EstimateExplanation];
    public bool HasSpaceEstimate => _preview is { Complete: true } && _previewRevision == _owner.HistoryRevision;
    private bool IsReady => _owner.Settings.RepositoryPath.Length != 0 && _owner.Inspection.State == RepositoryReadiness.Ready && !_owner.ConfigurationNeedsReset;
    private bool CanExecutePreview => IsReady && !_owner.StatusIsActive() && _preview is { Complete: true, Candidates: > 0 } &&
        _previewRevision == _owner.HistoryRevision && SourcePaths.Same(_path, _owner.Settings.RepositoryPath) && ReferenceEquals(_service, _owner.Maintenance);
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        using var cancel = new CancellationTokenSource(); _cancel = cancel;
        try { await _owner.RunAsync(async token => { using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel.Token); await action(linked.Token); }); }
        finally { _cancel = null; Update(); }
    }
    private void Update() { Changed(""); Preview.Refresh(); Execute.Refresh(); ConfirmExecution.Refresh(); Cancel.Refresh(); }
}

internal static class DesktopContextState
{ internal static bool StatusIsActive(this IDesktopContext owner) => owner.Status?.IsAttemptActive == true; }
