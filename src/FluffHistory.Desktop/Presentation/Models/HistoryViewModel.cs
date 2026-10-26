using System.Collections.ObjectModel;
using System.ComponentModel;
using FluffHistory.Application;

namespace FluffHistory.Desktop.Presentation.Models;

public abstract record TimelineItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new(""));
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string IconKey { get; }
    public virtual string ToolTip => Name;
    public virtual string TileDescription => Description;
    public virtual string AccessibleName => $"{Name}, {Description}";
}
public sealed record RootChoice(Guid Id, string Label, PersonalFolderKind IconKind = PersonalFolderKind.Folder,
    string Path = "", bool IsActive = false, bool HasHistory = true) : TimelineItem
{
    public string Subtitle { get; init; } = "";
    public IAppStrings? Strings { get; init; }
    public FolderPresentation? Presentation { get; init; }
    public string? ParentPath { get; init; }
    public bool Disambiguate { get; init; }
    public override string Name => Strings is not null && Presentation is not null ? PresentationText.FolderName(Strings, Presentation) : Label;
    public override string Description => Strings is null ? Subtitle : ParentPath is not null ?
        ParentPath + (IsActive ? "" : Strings[TextId.History_InactiveSuffix]) : IsActive ? "" : Strings[TextId.History_Inactive];
    public override string IconKey => "known:" + IconKind;
    public override string ToolTip => Disambiguate ? Path : Name;
    public override string ToString() => Name;
}
public sealed record BreadcrumbChoice(string Label, string Folder, bool VirtualRoot = false) { public override string ToString() => Label; }
public sealed record TimeChoice(DateTimeOffset? Utc, IAppStrings Strings) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public string Label => Utc is { } time ? time.ToLocalTime().ToString("G", Strings.FormatCulture) : Strings[TextId.History_Latest];
    public override string ToString() => Label;
}
public sealed record ChildRow(BrowserChild Child, IAppStrings Strings, PersonalFolderKind FolderKind = PersonalFolderKind.Folder) : TimelineItem
{
    private IAppStrings S => Strings;
    public override string Name => Child.Name;
    public string Glyph => Child.IsFolder ? "\uE8B7" : "\uE8A5";
    public override string Description => Child.IsFolder ? S[TextId.History_Folder] : S.Format(TextId.History_FileDescription,
        S.Count(CountKind.Bytes, Child.Version!.Length), Child.Version.StoredUtc.ToLocalTime());
    public override string IconKey => Child.IsFolder ? "known:" + FolderKind : "file:" + FileTypeIconKey.Extension(Name);
    public override string TileDescription => "";
    public override string AccessibleName => Child.IsFolder ? S.Format(TextId.History_FolderAccessible, Name) : S.Format(TextId.History_FileAccessible, Name, Description);
    public override string ToString() => AccessibleName;
}
public sealed record VersionRow(BrowserVersion Version, IAppStrings Strings) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new(""));
    private IAppStrings S => Strings;
    public string Stored => Version.StoredUtc.ToLocalTime().ToString("G", S.FormatCulture);
    public string Description => S.Format(TextId.History_VersionDescription, S.Count(CountKind.Bytes, Version.Length), Version.LastWriteUtc.ToLocalTime());
    public override string ToString() => S.Format(TextId.History_VersionAccessible, Stored, Description);
}

/// <summary>Bounded UI state for one captured browser. Every superseding intent advances a generation
/// before queuing; stale completions cannot publish rows, continuations, errors or loading state.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    public IAppStrings Strings => _owner.Strings;
    private IAppStrings S => Strings;
    public const int PageSize = 128, MaximumChildren = 4096, MaximumTimes = 1024, MaximumVersions = 1024;
    private readonly IDesktopContext _owner;
    private readonly IDesktopDialogs _dialogs;
    private string _language;
    private BrowserContinuation? _childrenNext, _timesNext, _versionsNext;
    private BrowserVersion? _historyPath;
    private ChildRow[] _selection = [];
    private RootChoice[] _selectedMounts = [];
    private Guid? _capture, _repositoryId;
    private string? _repositoryPath;
    private long _generation, _historyRevision;
    private bool _busy, _refreshRequired;
    private string _folder = "", _message = "";
    private DateTimeOffset? _atUtc;
    private bool _earlierExhausted, _laterExhausted;
    private RootChoice? _root;
    private TimeChoice? _time;
    private VersionRow? _version;
    private ApplicationFailure? _failure;
    public ObservableCollection<RootChoice> Roots { get; } = [];
    public ObservableCollection<BreadcrumbChoice> Breadcrumbs { get; } = [];
    public ObservableCollection<TimeChoice> Times { get; } = [];
    public ObservableCollection<ChildRow> Children { get; } = [];
    public ObservableCollection<VersionRow> Versions { get; } = [];
    public HistoryViewModel(IDesktopContext owner, IDesktopDialogs dialogs)
    {
        _owner = owner; _dialogs = dialogs; _language = S.Language; Times.Add(new(null, S));
        Setup = new(() => { dialogs.Navigate("Configuration"); return Task.CompletedTask; });
        Refresh = new(() => RefreshAsync(), () => IsConfigured);
        Parent = new(GoToParentAsync, () => CanNavigate && !IsAtVirtualRoot);
        LargeIcons = new(() => SetViewModeAsync(TimelineViewMode.LargeIcons));
        ListView = new(() => SetViewModeAsync(TimelineViewMode.List));
        Latest = new(() => ChangeTimeAsync(null), () => CanNavigate && !Busy && _atUtc is not null);
        Older = new(() => StepTimeAsync(true), () => CanStepTime(true));
        Newer = new(() => StepTimeAsync(false), () => CanStepTime(false));
        LoadChildren = new(() => ContinueNavigationAsync(false), () => CanBrowse && !Busy && !_refreshRequired && _childrenNext is not null && Children.Count < MaximumChildren);
        LoadTimes = new(() => ContinueNavigationAsync(true), () => CanBrowse && !Busy && !_refreshRequired && CanLoadTimes);
        LoadVersions = new(() => QueryAsync(LoadVersionsAsync), () => CanBrowse && !Busy && !NeedsRefresh && _versionsNext is not null && Versions.Count < MaximumVersions && _historyPath is not null);
        Cancel = new(() => { CancelQuery(); return Task.CompletedTask; }, () => Busy);
        Clear = new(async () => { await SelectItemsAsync([]); SelectionCleared?.Invoke(); }, () => CanNavigate && SelectedItems.Count != 0);
        OpenSelected = new(() => OpenItemAsync(SelectedItems[0]), () => CanNavigate && SelectedItems.Count == 1 &&
            SelectedItems[0] is RootChoice or ChildRow { Child.IsFolder: true });
        Preview = new(() => ActionAsync(async token => { if (SelectedExact is { } v) await owner.PreviewAsync(v, token); }), () => CanUseExact);
        PreviewDisplayed = new(() =>
        {
            var version = _selection[0].Child.Version!;
            return ActionAsync(token => owner.PreviewAsync(version, token));
        }, () => CanBrowse && !Busy && !NeedsRefresh && HasFileSelection);
        RestoreExact = new(() => RestoreExactAsync(false), () => CanUseExact);
        RestoreExactOriginal = new(() => RestoreExactAsync(true), () => CanUseExact && HasOriginalMapping);
        DeleteVersion = new(DeleteVersionAsync, () => CanUseExact && SelectedExact?.IsCurrent == false && owner.VersionRemoval is { IsBusy: false } && !owner.StatusIsActive());
        RestoreSelected = new(() => RestoreAsync(false), () => CanRestoreSelection);
        RestoreOriginal = new(() => RestoreAsync(true), () => CanRestoreSelection && owner.Settings.Sources.Any(s => s.SourceRootId == _root?.Id && s.IsProtected && s.RuntimePath is not null));
        CommandGroup.Attach(owner, () => { Changed(""); RefreshCommands(); }, Preview, PreviewDisplayed, RestoreExact, RestoreExactOriginal, DeleteVersion, RestoreSelected, RestoreOriginal);
        owner.Changed += OwnerChanged;
    }
    /// <summary>Requests the view to clear its native selected-items state after logical unselection.</summary>
    public event Action? SelectionCleared;
    public AsyncCommand Setup { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand Parent { get; }
    public AsyncCommand LargeIcons { get; }
    public AsyncCommand ListView { get; }
    public AsyncCommand Latest { get; }
    public AsyncCommand Older { get; }
    public AsyncCommand Newer { get; }
    public AsyncCommand LoadChildren { get; }
    public AsyncCommand LoadTimes { get; }
    public AsyncCommand LoadVersions { get; }
    public AsyncCommand Cancel { get; }
    public AsyncCommand Clear { get; }
    public AsyncCommand OpenSelected { get; }
    public AsyncCommand Preview { get; }
    public AsyncCommand PreviewDisplayed { get; }
    public AsyncCommand RestoreExact { get; }
    public AsyncCommand RestoreExactOriginal { get; }
    public AsyncCommand DeleteVersion { get; }
    public AsyncCommand RestoreSelected { get; }
    public AsyncCommand RestoreOriginal { get; }
    public bool Busy { get => _busy; private set { _busy = value; Changed(""); RefreshCommands(); } }
    public bool IsConfigured => !_owner.EncryptionBusy && !_owner.ConfigurationNeedsReset && _owner.Settings.RepositoryPath.Length > 0 && _owner.Inspection.State == RepositoryReadiness.Ready;
    public bool EncryptionBlocked => _owner.EncryptionBusy || _owner.Inspection.State == RepositoryReadiness.EncryptionMigrationRequired;
    public bool IsAtVirtualRoot => _root is null;
    public object Items => IsAtVirtualRoot ? Roots : Children;
    public IReadOnlyList<TimelineItem> SelectedItems => IsAtVirtualRoot ? _selectedMounts : _selection;
    public bool IsLargeIcons => _owner.Preferences.TimelineViewMode == TimelineViewMode.LargeIcons;
    public bool IsList => !IsLargeIcons;
    public bool IsEmpty => !Busy && (IsAtVirtualRoot ? Roots.Count : Children.Count) == 0;
    public string EmptyMessage => IsAtVirtualRoot ? S[TextId.History_EmptyRoot] :
        _root?.HasHistory == false ? S[TextId.History_NoVersions] : S[TextId.History_EmptyTime];
    public bool NeedsRefresh => _refreshRequired || _capture is not null && _historyRevision < _owner.HistoryRevision;
    public bool ShowRefreshNotice => _refreshRequired;
    public string RefreshMessage => S[TextId.History_RefreshAdvice];
    public string Message => _message;
    public string TimeLabel => _atUtc is { } time ? time.ToLocalTime().ToString("G", S.FormatCulture) : S[TextId.History_Latest];
    public string AsOfLabel => S.Format(TextId.History_AsOf, TimeLabel);
    public DateTimeOffset? AtUtc => _atUtc;
    public bool HasFileSelection => _selection.Length == 1 && _selection[0].Child.Version is not null;
    public string CountText => !IsAtVirtualRoot && Children.Count >= MaximumChildren && _childrenNext is not null ? S[TextId.History_ChildrenLimit] :
        S.Format(TextId.History_SelectionCounts, S.Count(CountKind.Items, IsAtVirtualRoot ? Roots.Count : Children.Count), SelectedItems.Count);
    public string TimeLimit => Times.Count - 1 >= MaximumTimes && _timesNext is not null ? S[TextId.History_TimesLimit] : "";
    public string VersionLimit => Versions.Count >= MaximumVersions && _versionsNext is not null ? S[TextId.History_VersionsLimit] : "";
    public string Context => $"{_root?.Name ?? S[TextId.History_NoFolders]} / {S.Technical(_folder)}\n{(_atUtc is null ? S[TextId.History_LatestVersions] : S.Format(TextId.History_AsOfDate, _atUtc.Value.ToLocalTime()))}";
    public RootChoice? CurrentRoot => _root;
    public TimeChoice? SelectedTime { get => _time; set => Set(ref _time, value); }
    public VersionRow? SelectedVersion { get => _version; set { Set(ref _version, value); Changed(nameof(VersionDeletionNotice)); RefreshCommands(); } }
    public bool BindingNavigation { get; private set; }
    public bool CanNavigate => CanBrowse && !RestoreSelected.IsBusy && !RestoreOriginal.IsBusy && !RestoreExact.IsBusy && !RestoreExactOriginal.IsBusy && !DeleteVersion.IsBusy && !Preview.IsBusy && !PreviewDisplayed.IsBusy;
    public string VersionDeletionNotice => SelectedExact?.IsCurrent == true ? S[TextId.History_ProtectedVersion] : "";
    private bool HasOriginalMapping => _owner.Settings.Sources.Any(s => s.SourceRootId == _root?.Id && s.IsProtected && s.RuntimePath is not null);
    public string DetailsTitle => SelectedItems.Count == 1 ? SelectedItems[0].Name : SelectedItems.Count > 1 ? S.Format(TextId.History_SelectedCount, SelectedItems.Count) : S[TextId.History_SelectItem];
    public string Details => IsAtVirtualRoot ? _selectedMounts.Length == 1 ? S[TextId.History_OpenFolder] + _selectedMounts[0].Description : S[TextId.History_VirtualRootExplanation] :
        _selection.Length != 1 ? S[TextId.History_SelectionExplanation] :
        _selection[0].Child.IsFolder ? S[TextId.History_FolderExplanation] + Context :
        S.Format(TextId.History_FileDetails, S.Technical(_selection[0].Child.Version!.RelativePath), S.Count(CountKind.Bytes, _selection[0].Child.Version!.Length),
            _selection[0].Child.Version!.StoredUtc.ToLocalTime(), _selection[0].Child.Version!.LastWriteUtc.ToLocalTime());
    private int TimeIndex => _atUtc is null ? 0 : Times.ToList().FindIndex(t => t.Utc == _atUtc);
    private BrowserVersion? SelectedExact => _selection.Length == 1 && !_selection[0].Child.IsFolder ? _version?.Version ?? _selection[0].Child.Version : null;
    private bool CanBrowse => IsConfigured && _capture is not null && _capture == _owner.Browser?.State.CaptureId &&
        StringComparer.OrdinalIgnoreCase.Equals(_repositoryPath, _owner.Settings.RepositoryPath);
    private bool CanUseExact => CanBrowse && !Busy && !NeedsRefresh && SelectedExact is not null;
    private bool CanRestoreSelection => CanBrowse && !Busy && !NeedsRefresh && _selection.Length is > 0 and <= MaximumChildren;

    private void OwnerChanged()
    {
        if (_language != S.Language)
        {
            _language = S.Language;
            foreach (var row in Roots.Cast<TimelineItem>().Concat(Children)) row.RefreshLanguage();
            foreach (var row in Times) row.RefreshLanguage();
            foreach (var row in Versions) row.RefreshLanguage();
            UpdateBreadcrumbs();
        }
        if (_owner.LatestFailure == ApplicationFailure.RefreshRequired) _refreshRequired = true;
        if (!IsConfigured || _repositoryPath is not null &&
            (!StringComparer.OrdinalIgnoreCase.Equals(_repositoryPath, _owner.Settings.RepositoryPath) ||
             _capture is not null && _capture != _owner.Browser?.State.CaptureId))
            InvalidateHistory();
        Changed(""); RefreshCommands();
    }
    private void InvalidateHistory()
    {
        _generation++; _owner.CancelHistory();
        _capture = _repositoryId = null; _repositoryPath = null; _historyRevision = 0;
        _root = null; _folder = ""; _atUtc = null; _refreshRequired = false; _failure = null;
        ResetRows(resetTimes: true); Roots.Clear(); Breadcrumbs.Clear();
        _owner.BrowserActivity = "Closed";
        _message = !IsConfigured ? _owner.Settings.RepositoryPath.Length == 0 ? S[TextId.History_ConfigureTimeline] :
            PresentationText.Readiness(S, _owner.Inspection.State) + S[TextId.History_ConfigureContinue] : S[TextId.History_LoadAdvice];
        Busy = false;
    }

    public Task ActivateAsync()
    {
        if (!IsConfigured) { InvalidateHistory(); return Task.CompletedTask; }
        if (CanBrowse) { Changed(""); return NeedsRefresh ? RefreshAsync() : Task.CompletedTask; }
        InvalidateHistory();
        return RefreshAsync(false);
    }
    public async Task OpenShellAsync(ShellOpenRequest request, Func<bool> stillCurrent)
    {
        var target = ShellPathResolver.Resolve(_owner.Settings, request.Path, request.Kind);
        if (target is null || !IsConfigured)
        {
            CancelQuery();
            _message = target is null ? S[TextId.History_NotProtected] : S[TextId.History_OpenBackupAdvice];
            Changed(nameof(Message)); return;
        }
        await RefreshAsync();
        if (!stillCurrent() || !CanBrowse) return;
        var root = Roots.FirstOrDefault(r => r.Id == target.SourceRootId);
        if (root is null) { _message = S[TextId.History_NoItemHistory]; Changed(nameof(Message)); return; }
        _root = root; _atUtc = null;
        var slash = target.RelativePath.LastIndexOf('/');
        _folder = target.Kind == ShellItemKind.Folder ? target.RelativePath : slash < 0 ? "" : target.RelativePath[..slash];
        await NavigateAsync(true);
        if (!stillCurrent() || !CanBrowse || target.Kind == ShellItemKind.Folder) return;
        ChildRow? focus = null;
        await QueryAsync(async (browser, generation, token) =>
        {
            var child = await browser.GetLatestFileAsync(target.SourceRootId, target.RelativePath, token);
            if (!Current(generation) || !stillCurrent()) return;
            if (child is null) return;
            focus = Children.FirstOrDefault(r => !r.Child.IsFolder && r.Child.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase));
            if (focus is null) { focus = new(child, S); Children.Insert(0, focus); }
        });
        if (!stillCurrent()) return;
        if (focus is not null) await SelectChildrenAsync([focus]);
        else { _message = S[TextId.History_NoFileVersion]; Changed(nameof(Message)); }
    }
    public Task RefreshAsync(bool refresh = true)
    {
        if (!IsConfigured) { InvalidateHistory(); return Task.CompletedTask; }
        OwnerChanged();
        _repositoryPath = _owner.Settings.RepositoryPath;
        return QueryAsync(async (browser, generation, token) =>
        {
            var revision = _owner.HistoryRevision;
            if (refresh) await browser.RefreshAsync(token);
            if (!Current(generation)) return;
            if (_repositoryId != browser.State.RepositoryId) { _root = null; _folder = ""; _atUtc = null; }
            _repositoryId = browser.State.RepositoryId;
            _capture = browser.State.CaptureId; _historyRevision = revision; _refreshRequired = false;
            var historical = await browser.GetHistoricalRootsAsync(token);
            if (!Current(generation)) return;
            BindingNavigation = true;
            try
            {
                var rootId = _root?.Id; Roots.Clear();
                var configured = _owner.Settings.Sources;
                var sourceIds = browser.State.Sources.Select(s => s.SourceRootId).Concat(configured.Where(s => s.IsProtected && s.RuntimePath is not null).Select(s => s.SourceRootId)).Distinct();
                foreach (var id in sourceIds)
                {
                    var source = browser.State.Sources.FirstOrDefault(s => s.SourceRootId == id);
                    var mapping = configured.FirstOrDefault(s => s.SourceRootId == id);
                    var active = mapping is { IsProtected: true, RuntimePath: not null };
                    if (!active && !historical.Contains(id)) continue;
                    var path = mapping?.RuntimePath ?? mapping?.OriginalPath ?? source!.OriginalPath;
                    var presentation = _owner.FolderPolicy.Describe(path);
                    Roots.Add(new(id, PresentationText.FolderName(S, presentation), presentation.Kind, path, active, historical.Contains(id)) { Strings = S, Presentation = presentation });
                }
                foreach (var group in Roots.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToArray())
                    foreach (var root in group.ToArray())
                    {
                        var path = root.Path.TrimEnd('\\', '/'); var slash = path.LastIndexOfAny(['\\', '/']); var parent = slash > 0 ? path[..slash] : path;
                        Roots[Roots.IndexOf(root)] = root with { Disambiguate = true, ParentPath = parent };
                    }
                _root = Roots.FirstOrDefault(r => r.Id == rootId);
                if (_root is null) _folder = "";
                ResetRows(resetTimes: true); UpdateBreadcrumbs();
            }
            finally { BindingNavigation = false; }
            await LoadChildrenAsync(browser, generation, token); await LoadTimesAsync(browser, generation, token);
        });
    }
    private async Task<bool> EnsureFreshAsync()
    {
        if (NeedsRefresh) await RefreshAsync();
        return CanBrowse && !NeedsRefresh;
    }
    private async Task ContinueNavigationAsync(bool times)
    {
        if (NeedsRefresh) { await EnsureFreshAsync(); return; }
        await QueryAsync(times ? LoadTimesAsync : LoadChildrenAsync);
    }
    public async Task ChangeRootAsync(RootChoice? root)
    {
        if (BindingNavigation || root is null || !CanNavigate || !Roots.Contains(root)) return;
        var id = root.Id;
        if (!await EnsureFreshAsync()) return;
        _root = Roots.FirstOrDefault(r => r.Id == id);
        if (_root is null) { _message = S[TextId.History_FolderGone]; Changed(""); return; }
        _folder = ""; await NavigateAsync(true);
    }
    public async Task GoToVirtualRootAsync()
    {
        if (!CanNavigate || !await EnsureFreshAsync()) return;
        _root = null; _folder = ""; await NavigateAsync(true);
    }
    public Task GoToParentAsync() => IsAtVirtualRoot || !CanNavigate ? Task.CompletedTask : _folder.Length == 0 ? GoToVirtualRootAsync() : ChangeFolderAsync(_folder.Contains('/') ? _folder[.._folder.LastIndexOf('/')] : "");
    public Task OpenItemAsync(TimelineItem item) => item is RootChoice root ? ChangeRootAsync(root) : item is ChildRow child ? OpenFolderAsync(child) : Task.CompletedTask;
    public Task SetViewModeAsync(TimelineViewMode mode) => _owner.RunAsync(token => _owner.SavePreferencesAsync(_owner.Preferences with { TimelineViewMode = mode }, token));
    public async Task ChangeFolderAsync(string folder)
    {
        if (!CanNavigate || !await EnsureFreshAsync()) return;
        _folder = folder; await NavigateAsync(true);
    }
    public Task OpenFolderAsync(ChildRow row) => row.Child.IsFolder ? ChangeFolderAsync(_folder.Length == 0 ? row.Name : _folder + "/" + row.Name) : Task.CompletedTask;
    public async Task ChangeTimeAsync(DateTimeOffset? time)
    {
        if (BindingNavigation || !CanNavigate || !await EnsureFreshAsync()) return;
        _atUtc = time?.ToUniversalTime(); _earlierExhausted = _laterExhausted = false; await NavigateAsync(false);
    }
    public Task UseLocalTimeAsync(DateTime date, TimeSpan time)
    {
        var local = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) { _message = S[TextId.History_InvalidLocalTime]; Changed(nameof(Message)); return Task.CompletedTask; }
        // For a repeated autumn time, Windows' standard-time offset is used, matching DateTime conversion.
        return ChangeTimeAsync(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime());
    }
    private bool CanLoadTimes => _timesNext is not null && Times.Count - 1 < MaximumTimes;
    private DateTimeOffset? EarlierBoundary => _atUtc ?? Times.FirstOrDefault(t => t.Utc is not null)?.Utc;
    private TimeChoice? TimeNeighbor(bool earlier) => earlier
        ? Times.FirstOrDefault(t => t.Utc is { } utc && utc < EarlierBoundary)
        : Times.LastOrDefault(t => t.Utc is { } utc && utc > _atUtc);
    private bool CanStepTime(bool earlier) => CanNavigate && !Busy && !_refreshRequired &&
        (_owner.HistoryRevision > _historyRevision && (earlier || _atUtc is not null) ||
        (earlier ? !_earlierExhausted && (TimeNeighbor(true) is not null || _timesNext is not null) : !_laterExhausted && _atUtc is not null && TimeNeighbor(false) is not null));
    private async Task StepTimeAsync(bool earlier)
    {
        if (!CanStepTime(earlier) || !await EnsureFreshAsync()) return;
        await QueryAsync(async (browser, generation, token) =>
        {
            if (_root is null) return;
            var neighbor = await browser.FindTimelineNeighborAsync(new(_root.Id, _folder), earlier ? EarlierBoundary : _atUtc, earlier, token);
            if (!Current(generation)) return;
            if (neighbor is null) { if (earlier) _earlierExhausted = true; else _laterExhausted = true; return; }
            _atUtc = neighbor; _earlierExhausted = _laterExhausted = false;
            ResetRows(resetTimes: false); UpdateBreadcrumbs();
            await LoadChildrenAsync(browser, generation, token);
        });
    }
    private Task NavigateAsync(bool resetTimes)
    {
        ResetRows(resetTimes); UpdateBreadcrumbs();
        return QueryAsync(async (browser, generation, token) =>
        {
            await LoadChildrenAsync(browser, generation, token);
            if (resetTimes) await LoadTimesAsync(browser, generation, token);
        });
    }
    public Task SelectChildrenAsync(IEnumerable<ChildRow> rows)
    {
        if (BindingNavigation || !CanNavigate) return Task.CompletedTask;
        // Native SelectedItems is live: loading/status notifications may synchronize it.
        // Capture the pointer/keyboard intent before publishing any notification.
        var selected = rows.Take(MaximumChildren + 1).ToArray();
        _generation++; _owner.CancelHistory(); Busy = false;
        _selection = selected; _owner.ReleasePreview();
        Versions.Clear(); _versionsNext = null; _version = null;
        _historyPath = !NeedsRefresh && _selection.Length == 1 ? _selection[0].Child.Version : null;
        Changed(""); RefreshCommands();
        if (_historyPath is null) { UpdateDiagnostics(); return Task.CompletedTask; }
        return QueryAsync(LoadVersionsAsync);
    }
    public Task SelectItemsAsync(IEnumerable<TimelineItem> items)
    {
        if (!IsAtVirtualRoot) return SelectChildrenAsync(items.OfType<ChildRow>());
        if (BindingNavigation || !CanNavigate) return Task.CompletedTask;
        _selectedMounts = items.OfType<RootChoice>().Where(Roots.Contains).Take(1024).ToArray();
        _selection = []; _owner.ReleasePreview(); Versions.Clear(); _version = null; _historyPath = null;
        Changed(""); RefreshCommands(); return Task.CompletedTask;
    }
    /// <summary>Context gestures select an unselected displayed row, preserving an existing multi-selection.
    /// Identity checks after async selection reject a replaced view or recycled container.</summary>
    public async Task<bool> SelectContextItemAsync(TimelineItem item)
    {
        if (!CanNavigate || NeedsRefresh || !IsDisplayed(item)) return false;
        if (!SelectedItems.Any(selected => ReferenceEquals(selected, item))) await SelectItemsAsync([item]);
        return CanNavigate && !NeedsRefresh && IsDisplayed(item) && SelectedItems.Any(selected => ReferenceEquals(selected, item));
    }
    public bool SelectContextVersion(VersionRow row)
    {
        if (!CanNavigate || NeedsRefresh || !HasFileSelection || !Versions.Any(version => ReferenceEquals(version, row))) return false;
        SelectedVersion = row; return true;
    }
    private bool IsDisplayed(TimelineItem item) => IsAtVirtualRoot
        ? Roots.Any(root => ReferenceEquals(root, item)) : Children.Any(child => ReferenceEquals(child, item));
    public void Deactivate() { CancelQuery(); _owner.ReleasePreview(); }
    public void CancelQuery()
    {
        _generation++; _owner.CancelHistory();
        if (Busy) _message = S[TextId.History_LoadingCancelledRows];
        Busy = false;
        UpdateDiagnostics();
    }
    private void ResetRows(bool resetTimes)
    {
        BindingNavigation = true;
        try
        {
            _owner.ReleasePreview(); _childrenNext = _versionsNext = null; _selection = []; _selectedMounts = []; _historyPath = null; _version = null;
            Children.Clear(); Versions.Clear();
            if (resetTimes) { _timesNext = null; Times.Clear(); Times.Add(new(null, S)); _earlierExhausted = _laterExhausted = false; }
            _time = Times.FirstOrDefault(t => t.Utc == _atUtc);
        }
        finally { BindingNavigation = false; }
        Changed("");
    }
    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear(); Breadcrumbs.Add(new(S[TextId.History_BreadcrumbRoot], "", VirtualRoot: true));
        if (_root is null) return;
        Breadcrumbs.Add(new(_root.Name, "")); var path = "";
        foreach (var component in _folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        { path = path.Length == 0 ? component : path + "/" + component; Breadcrumbs.Add(new(component, path)); }
    }
    private async Task LoadChildrenAsync(HistoryBrowserSession browser, long generation, CancellationToken token)
    {
        if (_root is null || !Current(generation)) return;
        var page = await browser.GetChildrenAsync(new(_root.Id, _folder, _atUtc), _childrenNext, PageSize, token);
        if (!Current(generation)) return;
        foreach (var child in page.Items.Take(MaximumChildren - Children.Count))
        {
            if (Children.Any(r => r.Child.IsFolder == child.IsFolder && r.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var origin = Path.Combine(_root.Path, _folder.Replace('/', Path.DirectorySeparatorChar), child.Name);
            Children.Add(new(child, S, child.IsFolder ? _owner.FolderPolicy.Describe(origin).Kind : PersonalFolderKind.Folder));
        }
        _childrenNext = page.Continuation; Changed("");
    }
    private async Task LoadTimesAsync(HistoryBrowserSession browser, long generation, CancellationToken token)
    {
        if (_root is null || !Current(generation)) return;
        var page = await browser.GetTimelineAsync(new(_root.Id, _folder), _timesNext, PageSize, token);
        if (!Current(generation)) return;
        BindingNavigation = true;
        try
        {
            foreach (var time in page.Items.Take(MaximumTimes - (Times.Count - 1))) Times.Add(new(time, S));
            _timesNext = page.Continuation; _time = Times.FirstOrDefault(t => t.Utc == _atUtc); Changed("");
        }
        finally { BindingNavigation = false; }
    }
    private async Task LoadVersionsAsync(HistoryBrowserSession browser, long generation, CancellationToken token)
    {
        if (_historyPath is null || !Current(generation)) return;
        var page = await browser.GetPathHistoryAsync(_historyPath, _versionsNext, PageSize, token);
        if (!Current(generation)) return;
        foreach (var version in page.Items.Take(MaximumVersions - Versions.Count)) Versions.Add(new(version, S));
        _versionsNext = page.Continuation; Changed("");
    }
    private Task QueryAsync(Func<HistoryBrowserSession, long, CancellationToken, Task> action)
    {
        var generation = ++_generation; _owner.CancelHistory(); _failure = null; _message = S[TextId.History_Loading]; Busy = true;
        return _owner.RunAsync(async token =>
        {
            if (!Current(generation)) return;
            try
            {
                await _owner.WithBrowserAsync(async (browser, queryToken) =>
                {
                    if (!Current(generation)) return;
                    await action(browser, generation, queryToken);
                    if (Current(generation)) _message = "";
                }, token);
            }
            catch (OperationCanceledException) { if (Current(generation)) _message = S[TextId.History_LoadingCancelled]; }
            catch (ApplicationOperationException error)
            {
                if (!Current(generation)) return;
                if (error.Failure is ApplicationFailure.RecoveryRequired or ApplicationFailure.InvalidRepository or ApplicationFailure.Unavailable)
                    InvalidateHistory();
                _failure = error.Failure;
                _refreshRequired = error.Failure == ApplicationFailure.RefreshRequired;
                _message = PresentationText.Failure(S, error.Failure);
            }
            finally
            {
                if (Current(generation)) { Busy = false; UpdateDiagnostics(); }
            }
        });
    }
    private bool Current(long generation) => generation == _generation;
    private Task ActionAsync(Func<CancellationToken, Task> action) => _owner.RunAsync(action);
    private async Task RestoreAsync(bool original)
    {
        var browser = _owner.Browser; var root = _root; if (browser is null || root is null) return;
        var children = _selection.Select(r => r.Child).ToArray();
        await ActionAsync(async token =>
        {
            var selection = browser.CreateSelection(children);
            if (original && !await ConfirmOriginalAsync()) return;
            var destination = original ? null : await _dialogs.FolderAsync();
            if (!original && destination is null) return;
            try { await _owner.RestoreSelectionAsync(selection, destination, root.Id, original, token); }
            catch (ApplicationOperationException error) when (error.Failure == ApplicationFailure.RestoreStructureConflict)
            { await _dialogs.NoticeAsync(S[TextId.History_RestoreConflictTitle], PresentationText.Failure(S, error.Failure)); return; }
            _message = _owner.LastOutcome; Changed(nameof(Message));
        });
    }
    private Task<bool> ConfirmOriginalAsync() => _dialogs.ConfirmAsync(S[TextId.History_RestoreOriginalTitle], S[TextId.History_RestoreOriginalExplanation], S[TextId.Action_Restore]);
    private Task RestoreExactAsync(bool original)
    {
        var version = SelectedExact; if (version is null) return Task.CompletedTask;
        return ActionAsync(async token =>
        {
            if (original && !await ConfirmOriginalAsync()) return;
            var destination = original ? null : await _dialogs.FolderAsync();
            if (!original && destination is null) return;
            await _owner.RestoreExactVersionAsync(version, destination, original, token);
            _message = _owner.LastOutcome; Changed(nameof(Message));
        });
    }
    private async Task DeleteVersionAsync()
    {
        var version = SelectedExact; var service = _owner.VersionRemoval; var path = _repositoryPath;
        if (version is null || service is null || path is null) return;
        MaintenanceSummary? result = null;
        await ActionAsync(async token =>
        {
            if (await service.IsCurrentAsync(path, version, token))
            { _message = S[TextId.History_ProtectedVersion]; Changed(nameof(Message)); return; }
            if (!await _dialogs.ConfirmDeleteAsync(S[TextId.History_DeleteTitle], S.Format(TextId.History_DeleteExplanation, version.RelativePath, version.StoredUtc.ToLocalTime()))) return;
            _owner.ReleasePreview();
            result = await service.RemoveAsync(path, version, token);
            if (result.ChangesMayExist) _owner.TimelineChanged();
        });
        // Refresh outside the serialized foreground command to avoid recursively taking its gate.
        if (result?.ChangesMayExist == true) await RefreshAsync();
        if (result is null) return;
        _message = result.State == MaintenanceState.Completed && !result.NeedsAttention ? S[TextId.History_Deleted] :
            result.State == MaintenanceState.Cancelled ? S.Format(TextId.History_DeleteCancelled, result.Removed) : S[TextId.History_DeleteAttention];
        if (result.NeedsAttention) _refreshRequired = true;
        _owner.LastOutcome = _message; Changed(""); RefreshCommands(); _owner.Notify();
    }
    private void UpdateDiagnostics()
    {
        _owner.BrowserActivity = $"Capture {_capture} · root {_root?.Id} · folder {_folder} · UTC {_atUtc:O} · children {Children.Count} · times {Times.Count - 1} · versions {Versions.Count} · selected {_selection.Length} · generation {_generation} · busy {Busy} · refreshRequired {_refreshRequired} · failure {_failure}";
        _owner.Notify();
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { Refresh, Parent, LargeIcons, ListView, Latest, Older, Newer, LoadChildren, LoadTimes, LoadVersions, Cancel, Clear, OpenSelected, Preview, PreviewDisplayed, RestoreExact, RestoreExactOriginal, DeleteVersion, RestoreSelected, RestoreOriginal }) command?.Refresh();
    }
}
