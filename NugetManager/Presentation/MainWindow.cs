using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Globalization;
using NugetManager.Domain;
using NugetManager.Operations;

namespace NugetManager.Presentation;

/// <summary>
/// Three-column package manager window. All controller notifications are marshalled through the MewUI dispatcher
/// and coalesced so that only the latest state is rendered.
/// </summary>
internal sealed class MainWindow : Window
{
    private static Color SelectedRowColor => AppTheme.Current.Selected;
    private static Color HeaderRowColor => AppTheme.Current.Header;
    private static Color PaneColor => AppTheme.Current.Pane;
    private static Color BorderColor => AppTheme.Current.Border;
    private static Color AccentColor => AppTheme.Current.Accent;

    private readonly WorkspaceController _controller;
    private readonly string? _initialDirectory;
    private readonly string? _recoveryDirectory;
    private AppPreferences _preferences;
    private readonly AppPreferenceStore? _preferenceStore;
    private readonly CultureInfo _systemCulture = CultureInfo.CurrentUICulture;
    private readonly ComboBox _themePicker = new();
    private readonly ComboBox _languagePicker = new();
    private readonly SegmentedControl _tabs = new();
    private readonly Popup _sourcePopup = new();
    private readonly PackageLinkMenu _links;
    private readonly List<(Border Border, Func<Color> Color)> _surfaces = [];
    private readonly TextBlock _scopeTitle = new();
    private readonly TextBlock _sourceTitle = new();
    private bool _savingPreferences;
    private ContextMenu? _openMenu;
    private static readonly Command OpenNuGetCommand = new("package.openNuGet", "Open NuGet");
    private static readonly Command ProjectPackageCommand = new("package.projectAction", "Package action");
    private static readonly Command OpenGitHubCommand = new("package.openGitHub", "Open GitHub");
    private readonly object _pendingGate = new();
    private WorkspaceViewState? _pending;
    private bool _renderScheduled;
    private WorkspaceViewState _state;

    private readonly TextBlock _workspaceText = new();
    private readonly StackPanel _solutionPanel = new StackPanel().Vertical().Spacing(4);
    private readonly ListBox _projectList = new();
    private readonly StackPanel _middle = new StackPanel().Vertical();
    private readonly StackPanel _details = new StackPanel().Vertical().Spacing(8);
    private readonly TextBox _searchBox = new();
    private readonly Button _openButton = new();
    private readonly Button _refreshButton = new();
    private readonly TabControl _sections = new();
    private ConfigurationPage? _configurationPage;
    private readonly Button _cancelButton = new();
    private readonly CheckBox _prerelease = new();
    private readonly CheckBox _quickMode = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _progressText = new();
    private readonly TextBlock _errorText = new();
    private readonly Button _sourcePicker = new();
    private readonly ProgressRing _statusRing = new();
    private readonly ProgressRing _searchRing = new();
    private readonly ProgressRing _detailsRing = new();
    private readonly ProgressRing _versionsRing = new();
    private readonly ProgressRing _installedRing = new();

    private bool _browseMode;
    private bool _suppressEvents;
    private object? _projectsKey;
    private object? _middleKey;
    private object? _detailsKey;
    private CancellationTokenSource? _busyCts;
    private bool _applying;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;

    private MainWindow(WorkspaceController controller, string? initialDirectory, string? recoveryDirectory, AppPreferences? preferences, AppPreferenceStore? preferenceStore)
    {
        _controller = controller;
        _initialDirectory = initialDirectory;
        _recoveryDirectory = recoveryDirectory;
        _preferences = preferences ?? new();
        _preferenceStore = preferenceStore;
        _links = controller.CreateLinkMenu(new ExternalLinkOpener());
        Commands.Register<Uri>(OpenNuGetCommand, uri => Fire(() => _links.OpenAsync(uri)));
        Commands.Register<Uri>(OpenGitHubCommand, uri => Fire(() => _links.OpenAsync(uri)));
        Commands.Register<PackageChange>(ProjectPackageCommand,
            change => Fire(() => ApplyAsync(ct => _controller.PreviewChangesAsync([change], ct))));
        _state = controller.State;
        this.Title(L(TextKey.AppTitle)).Icon(IconSource.FromResource<MainWindow>("NugetManager.logo.ico"))
            .Resizable(1280, 800, minWidth: 980, minHeight: 560);
        _controller.StateChanged += OnStateChanged;
        this.OnLoaded(() =>
        {
            Application.Current.SetThemeMode(ThemeVariantFor(_preferences.Theme));
            Content = BuildLayout();
            Application.Current.ThemeChanged += HandleThemeChanged;
            Render(_controller.State);
            Fire(async () =>
            {
                await CheckRecoveryAsync();
                if (!string.IsNullOrWhiteSpace(_initialDirectory))
                {
                    await OnUiAsync(async () =>
                    {
                        await OpenDirectoryAsync(_initialDirectory!, null);
                        return true;
                    });
                }
            });
        });
        this.OnClosed(() =>
        {
            _controller.StateChanged -= OnStateChanged;
            Application.Current.ThemeChanged -= HandleThemeChanged;
            _sourcePopup.Close();
            _links.Dispose();
            _busyCts?.Cancel();
            _searchCts?.Cancel();
            _detailsCts?.Cancel();
            _statusRing.IsActive = false;
            _searchRing.IsActive = false;
            _detailsRing.IsActive = false;
            _versionsRing.IsActive = false;
            _installedRing.IsActive = false;
        });
    }

    public UiLanguage Language => _preferences.Language;

    public static MainWindow Create(WorkspaceController controller, string? initialDirectory = null, string? recoveryDirectory = null,
        AppPreferences? preferences = null, AppPreferenceStore? preferenceStore = null) =>
        new MainWindow(controller, initialDirectory, recoveryDirectory, preferences, preferenceStore);

    private string L(TextKey key, params string[] arguments) =>
        AppLocalization.Format(new UiMessage(key, arguments), _preferences.Language, _systemCulture);
    private string F(UiMessage? message) => message is null ? "" : AppLocalization.Format(message, _preferences.Language, _systemCulture);
    private string LF(TextKey key, FormattableString format) => F(UiMessage.FromFormat(key, format));
    private OperationDialog Dialogs => new(_preferences.Language, _systemCulture);

    private static ThemeVariant ThemeVariantFor(UiTheme theme) => theme switch
    {
        UiTheme.Light => ThemeVariant.Light, UiTheme.Dark => ThemeVariant.Dark, _ => ThemeVariant.System,
    };

    private async Task ChangePreferencesAsync(AppPreferences preferences)
    {
        _savingPreferences = true;
        Render(_state);
        try
        {
            if (_preferenceStore is not null) await _preferenceStore.SaveAsync(preferences, CancellationToken.None);
            await OnUiAsync(() =>
            {
                _preferences = preferences;
                Application.Current.SetThemeMode(ThemeVariantFor(preferences.Theme));
                InvalidatePresentation();
                return Task.FromResult(true);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await OnUiAsync(async () =>
            {
                await MessageBox.NotifyAsync(L(TextKey.PreferencesWriteFailed, ex.Message), PromptIconKind.Error, null, this);
                return true;
            });
        }
        finally
        {
            _savingPreferences = false;
            OnStateChanged(_state);
        }
    }

    private void HandleThemeChanged(Theme previous, Theme current) => InvalidatePresentation();

    private void InvalidatePresentation()
    {
        _projectsKey = _middleKey = _detailsKey = null;
        _sourcePopup.Close();
        _links.Close();
        Render(_state);
    }

    private void ShowSourcePicker()
    {
        if (!_sourcePicker.IsEnabled) return;
        if (_sourcePopup.IsOpen) { _sourcePopup.Close(); return; }
        var choices = new StackPanel().Vertical().Spacing(6).MinWidth(230);
        var selected = _state.SelectedSources.Resolve(_state.Sources);
        var all = new CheckBox().Content(L(TextKey.AllSources)).IsChecked(
            selected.Count == 0 ? false : selected.Count == _state.Sources.Count ? true : null);
        choices.Add(all);
        var checks = new List<(string Name, CheckBox Check)>();
        var syncing = false;
        foreach (var source in _state.Sources)
        {
            var check = new CheckBox().Content(source.Name).IsChecked(selected.Contains(source.Name, StringComparer.OrdinalIgnoreCase));
            checks.Add((source.Name, check));
            choices.Add(check);
            check.OnCheckedChanged(value =>
            {
                if (syncing) return;
                var names = checks.Where(c => c.Check.IsChecked == true).Select(c => c.Name).ToArray();
                syncing = true;
                all.IsChecked = names.Length == 0 ? false : names.Length == checks.Count ? true : null;
                syncing = false;
                ChangeSources(SourceSelection.Subset(names));
            });
        }
        all.OnCheckedChanged(value =>
        {
            if (syncing) return;
            syncing = true;
            foreach (var (_, check) in checks) check.IsChecked = value == true;
            syncing = false;
            ChangeSources(value == true ? SourceSelection.All : SourceSelection.Subset([]));
        });
        _sourcePopup.Content = new Border().Background(PaneColor).BorderBrush(BorderColor).BorderThickness(new Thickness(1))
            .Padding(12).Child(new ScrollViewer().MaxHeight(360).VerticalScroll(ScrollMode.Auto).Content(choices));
        _sourcePopup.ShowAt(_sourcePicker, _sourcePicker.Bounds, PopupAnchorSide.Below);
    }

    private void ChangeSources(SourceSelection selection)
    {
        _searchCts?.Cancel();
        _detailsCts?.Cancel();
        _links.Close();
        var query = _searchBox.Text?.Trim() ?? "";
        var browseMode = _browseMode;
        Fire(() => _controller.SelectSourcesAsync(selection, CancellationToken.None, query, loadBrowse: browseMode));
    }

    private void PackagePointer(MouseEventArgs e, string id, string version, string? source)
    {
        if (e.Button == MouseButton.Left) { SelectPackage(id, version, source); return; }
        if (e.Button != MouseButton.Right || _state.Workspace is not { } workspace) return;
        e.Handled = true;
        var identity = new PackageLinkIdentity(workspace.Directory, id, version, source);
        var menu = _openMenu = new ContextMenu();
        var nuget = new MenuItem(L(TextKey.OpenNuGet), OpenNuGetCommand) { IsEnabled = false };
        var github = new MenuItem(L(TextKey.OpenGitHub), OpenGitHubCommand) { IsEnabled = false };
        menu.SetItems(nuget, github);
        menu.Show(this, e.GetPosition(this));
        Fire(async () =>
        {
            await _links.LoadAsync(workspace, identity, CancellationToken.None);
            await OnUiAsync(async () =>
            {
                if (!ReferenceEquals(_openMenu, menu) || _links.State.Identity != identity || menu.Parent is null)
                {
                    if (ReferenceEquals(_openMenu, menu)) _links.Close();
                    return true;
                }
                var links = _links.State.Links;
                nuget.CommandData = links.NuGetPage;
                github.CommandData = links.GitHubRepository;
                nuget.IsEnabled = links.NuGetPage is not null;
                github.IsEnabled = links.GitHubRepository is not null;
                if (_links.State.Error is { } error)
                    await MessageBox.NotifyAsync(L(TextKey.ErrorDetail, F(error)), PromptIconKind.Warning, null, this);
                return true;
            });
        });
    }

    /// <summary>Offers recovery for operations that did not finish last time. Files change only on an explicit choice.</summary>
    private async Task CheckRecoveryAsync()
    {
        if (_recoveryDirectory is null)
        {
            return;
        }
        IReadOnlyList<PendingRecovery> pending;
        try
        {
            pending = await RecoveryCoordinator.FindAsync(_recoveryDirectory, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await OnUiAsync(async () =>
            {
                await MessageBox.NotifyAsync(L(TextKey.M075), PromptIconKind.Warning, $"{_recoveryDirectory}{Environment.NewLine}{UiMessage.FromException(ex)}", this);
                return true;
            });
            return;
        }

        foreach (var item in pending)
        {
            var choice = await OnUiAsync(() => Dialogs.ShowRecoveryAsync(this, item));
            try
            {
                if (choice == RecoveryChoice.Recover)
                {
                    var results = await RecoveryCoordinator.RecoverAsync(item, CancellationToken.None);
                    await OnUiAsync(async () =>
                    {
                        await Dialogs.ShowRecoveryResultAsync(this, item, results);
                        return true;
                    });
                }
                else if (choice == RecoveryChoice.Discard)
                {
                    var confirmed = await OnUiAsync(() => MessageBox.ConfirmAsync(L(TextKey.M076), PromptIconKind.Warning,
                        L(TextKey.M077) + item.BackupDirectory, this));
                    if (confirmed)
                    {
                        await RecoveryCoordinator.DiscardAsync(item, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                await OnUiAsync(async () =>
                {
                    await MessageBox.NotifyAsync(L(TextKey.M078), PromptIconKind.Error,
                        LF(TextKey.M079, $"{UiMessage.FromException(ex)}{Environment.NewLine}备份目录：{item.BackupDirectory}"), this);
                    return true;
                });
            }
        }
    }

    private Element BuildLayout()
    {
        _surfaces.Clear();
        _openButton.Content(L(TextKey.OpenDirectory)).OnClick(() => Fire(OpenFolderAsync));
        _refreshButton.Content(L(TextKey.Refresh)).OnClick(() =>
        {
            if (_sections.SelectedIndex == 1) _configurationPage?.Update(_state.Workspace?.Directory, reload: true);
            else Fire(() => RunBusyAsync(ct => _controller.RefreshAsync(ct)));
        });
        _cancelButton.Content(L(TextKey.Cancel)).IsVisible(false).OnClick(() => _busyCts?.Cancel());
        _prerelease.Content(L(TextKey.Prerelease)).OnCheckedChanged(value =>
        {
            if (!_suppressEvents)
            {
                Fire(() => RunBusyAsync(ct => _controller.SetIncludePrereleaseAsync(value == true, ct)));
            }
        });
        _quickMode.Content(L(TextKey.QuickMode)).OnCheckedChanged(value =>
        {
            if (!_suppressEvents && !_savingPreferences && !_applying && !_state.IsBusy)
                Fire(() => ChangePreferencesAsync(_preferences with { QuickMode = value == true }));
        });

        var toolbar = new Border()
            .Background(HeaderRowColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .Padding(10, 6)
            .DockTop()
            .Child(new Grid()
                .Columns("Auto,*,Auto")
                .Children(
                    new StackPanel().Horizontal().Spacing(8).Children(_openButton, _refreshButton, _prerelease, _quickMode),
                    new StackPanel().Horizontal().Spacing(8).Column(2).Children(
                        _themePicker.Width(140), _languagePicker.Width(150))));
        _themePicker.OnSelectionChanged(_ =>
        {
            if (!_suppressEvents && !_savingPreferences && _themePicker.SelectedIndex is >= 0 and <= 2)
                Fire(() => ChangePreferencesAsync(_preferences with { Theme = (UiTheme)_themePicker.SelectedIndex }));
        });
        _languagePicker.OnSelectionChanged(_ =>
        {
            if (!_suppressEvents && !_savingPreferences && _languagePicker.SelectedIndex is >= 0 and <= 4)
                Fire(() => ChangePreferencesAsync(_preferences with { Language = (UiLanguage)_languagePicker.SelectedIndex }));
        });
        _surfaces.Add((toolbar, () => HeaderRowColor));

        _errorText.Foreground(OperationDialog.ErrorColor).TextTrimming(TextTrimming.CharacterEllipsis);
        _progressText.Foreground(OperationDialog.MutedColor).TextTrimming(TextTrimming.CharacterEllipsis);
        var statusBar = new Border()
            .Background(HeaderRowColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 1, 0, 0))
            .Padding(10, 4)
            .DockBottom()
            .Child(new Grid()
                .Columns("Auto,*,*,Auto")
                .Children(
                    _statusText.CenterVertical(),
                    new StackPanel().Vertical().Column(1).CenterVertical().Margin(16, 0).Children(_progressText, _errorText),
                    _workspaceText.Column(2).Margin(8, 0).CenterVertical().TextTrimming(TextTrimming.CharacterEllipsis),
                    new StackPanel().Horizontal().Spacing(8).Column(3).Children(
                        _statusRing.Width(20).Height(20).CenterVertical().IsActive(false).IsVisible(false),
                        _cancelButton)));
        _surfaces.Add((statusBar, () => HeaderRowColor));

        _projectList.OnSelectionChanged(_ =>
        {
            if (_suppressEvents || _state.Workspace is not { } workspace)
            {
                return;
            }
            var index = _projectList.SelectedIndex;
            var scope = index <= 0 || index > workspace.Projects.Count ? WorkspaceScope.All : new WorkspaceScope(workspace.Projects[index - 1].Path);
            if (scope != _state.Scope)
            {
                Fire(() => _controller.SelectScopeAsync(scope, CancellationToken.None));
            }
        });
        var left = new Border()
            .Background(PaneColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 0, 1, 0))
            .Child(new DockPanel().Margin(8).Children(
                _scopeTitle.Text(L(TextKey.M084)).Bold().Margin(2, 0, 0, 6).DockTop(),
                _solutionPanel.DockTop(),
                _projectList));

        _tabs.OnSelectionChanged(_ =>
        {
            if (!_suppressEvents) SetBrowseMode(_tabs.SelectedIndex == 1);
        });
        _searchBox.Placeholder(L(TextKey.M085)).IsVisible(false).OnTextChanged(_ => ScheduleSearch());
        _sourcePicker.MaxWidth(210).OnClick(ShowSourcePicker);
        var middle = new DockPanel().Children(
            new StackPanel().Vertical().Spacing(6).Margin(8, 8, 8, 4).DockTop().Children(
                new Grid().Columns("*,Auto").Children(
                    _tabs.HorizontalAlignment(HorizontalAlignment.Left),
                    new StackPanel().Horizontal().Spacing(6).Column(1).Children(
                        _sourceTitle.CenterVertical(), _sourcePicker)),
                _searchBox),
            new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(_middle.Margin(8, 0, 8, 8)));

        var right = new Border()
            .Background(PaneColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(1, 0, 0, 0))
            .Child(new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(_details.Margin(12)));
        _surfaces.Add((left, () => PaneColor));
        _surfaces.Add((right, () => PaneColor));

        _configurationPage = new ConfigurationPage(this, F,
            () => !_controller.State.IsBusy && !_applying,
            () => RunBusyAsync(ct => _controller.RefreshAsync(ct)),
            () => Render(_controller.State));
        _sections.TabItems(
            new TabItem().Header(L(TextKey.PackagesTab)).Content(
                new Grid().Columns("240,*,400").Children(left, middle.Column(1), right.Column(2))),
            new TabItem().Header(L(TextKey.ConfigurationTab)).Content(_configurationPage.Content));
        _sections.OnSelectionChanged(_ =>
        {
            _sourcePopup.Close();
            if (_sections.SelectedIndex == 1) _configurationPage.Update(_state.Workspace?.Directory, reload: true);
        });
        return new DockPanel().Children(
            toolbar,
            statusBar,
            _sections);
    }

    private void OnStateChanged(WorkspaceViewState state)
    {
        lock (_pendingGate)
        {
            _pending = state;
            if (_renderScheduled)
            {
                return;
            }
            _renderScheduled = true;
        }
        Dispatcher.BeginInvoke(() =>
        {
            WorkspaceViewState? next;
            lock (_pendingGate)
            {
                next = _pending;
                _pending = null;
                _renderScheduled = false;
            }
            if (next is not null)
            {
                Render(next);
            }
        });
    }

    private void Render(WorkspaceViewState state)
    {
        if (_configurationPage?.IsSaving == true || _configurationPage?.IsEditing == true) state = state with { IsBusy = true };
        _state = state;
        _suppressEvents = true;
        try
        {
            RenderChrome(state);
            RenderProjects(state);
            RenderMiddle(state);
            RenderDetails(state);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void RenderChrome(WorkspaceViewState state)
    {
        if (_sections.Tabs.Count == 2)
        {
            _sections.Tabs[0].Header(L(TextKey.PackagesTab));
            _sections.Tabs[1].Header(L(TextKey.ConfigurationTab));
        }
        if (_sections.SelectedIndex == 1) _configurationPage?.Update(state.Workspace?.Directory);
        var workspaceName = state.Workspace is { } workspace
            ? Path.GetFileName(workspace.SolutionPath ?? Path.TrimEndingDirectorySeparator(workspace.Directory)) : null;
        Title = string.IsNullOrEmpty(workspaceName) ? L(TextKey.AppTitle) : $"{L(TextKey.AppTitle)} — {workspaceName}";
        _openButton.Content(L(TextKey.OpenDirectory));
        _refreshButton.Content(L(TextKey.Refresh));
        _cancelButton.Content(L(TextKey.Cancel));
        _prerelease.Content(L(TextKey.Prerelease));
        _quickMode.Content(L(TextKey.QuickMode));
        _quickMode.ToolTip(L(TextKey.QuickModeHint));
        _quickMode.IsChecked = _preferences.QuickMode;
        _quickMode.IsEnabled = !_savingPreferences && !_applying && !state.IsBusy;
        _themePicker.Items([L(TextKey.System), L(TextKey.Light), L(TextKey.Dark)]);
        _themePicker.SelectedIndex = (int)_preferences.Theme;
        _languagePicker.Items([L(TextKey.System), "简体中文", "English", "한국어", "Русский"]);
        _languagePicker.SelectedIndex = (int)_preferences.Language;
        _themePicker.IsEnabled = _languagePicker.IsEnabled = !_savingPreferences;
        _tabs.Items([L(TextKey.Installed), L(TextKey.Browse)]);
        _tabs.SelectedIndex = _browseMode ? 1 : 0;
        _scopeTitle.Foreground = _sourceTitle.Foreground = AppTheme.Current.Text;
        _scopeTitle.Text = L(TextKey.M084);
        _sourceTitle.Text = L(TextKey.M087);
        foreach (var (border, color) in _surfaces) { border.Background = color(); border.BorderBrush = BorderColor; }
        _errorText.Foreground = OperationDialog.ErrorColor;
        _progressText.Foreground = OperationDialog.MutedColor;
        _workspaceText.Text = state.Workspace is { } w ? w.SolutionPath ?? w.Directory : L(TextKey.M088);
        _workspaceText.ToolTip(_workspaceText.Text);
        _workspaceText.Foreground = OperationDialog.MutedColor;
        _statusText.Text = state.StatusMessage is { } status ? F(status) : StatusLabel(state.Status);
        _progressText.Text = F(state.Progress);
        _progressText.IsVisible = state.Progress is not null && state.IsBusy;
        _errorText.Text = F(state.Error);
        _errorText.IsVisible = state.Error is not null;
        _errorText.ToolTip(F(state.Error));
        _cancelButton.IsVisible = state.IsBusy && _busyCts is not null;
        _openButton.IsEnabled = !state.IsBusy;
        _refreshButton.IsEnabled = !state.IsBusy && state.Workspace is not null;
        _prerelease.IsEnabled = !state.IsBusy;
        _prerelease.IsChecked = state.IncludePrerelease;
        _projectList.IsEnabled = !state.IsBusy || state.Status != WorkspaceStatus.Writing;
        _tabs.IsEnabled = state.Workspace is not null;
        _searchBox.IsVisible = _browseMode;
        var activity = PresentationModel.BuildActivity(state, _browseMode);
        _statusRing.IsActive = activity.Workspace;
        _statusRing.IsVisible = activity.Workspace;
        _sourcePicker.IsEnabled = activity.SourceSelection;
        var resolved = state.SelectedSources.Resolve(state.Sources);
        _sourcePicker.Content(state.SelectedSources.IsAll ? L(TextKey.AllSources)
            : resolved.Count == 0 ? L(TextKey.NoSources) : resolved.Count == 1 ? resolved[0]
            : L(TextKey.SelectedSources, resolved.Count.ToString(CultureInfo.InvariantCulture)));
        if (!activity.SourceSelection) _sourcePopup.Close();
    }

    private void RenderProjects(WorkspaceViewState state)
    {
        var key = (state.Workspace, state.SolutionChoices);
        if (!Equals(key, _projectsKey))
        {
            _projectsKey = key;
            _solutionPanel.Clear();
            foreach (var solution in state.SolutionChoices)
            {
                var directory = Path.GetDirectoryName(solution)!;
                _solutionPanel.Add(new Button()
                    .Content(Path.GetFileName(solution))
                    .ToolTip(solution)
                    .OnClick(() => Fire(() => OpenDirectoryAsync(directory, solution))));
            }
            _solutionPanel.IsVisible = state.SolutionChoices.Count > 0;
            var items = state.Workspace is { } workspace
                ? new[] { LF(TextKey.M089, $"整个工作区（{workspace.Projects.Count} 个项目）") }.Concat(workspace.Projects.Select(p => p.Name)).ToArray()
                : [];
            _projectList.Items(items);
        }
        var index = 0;
        if (state.Workspace is { } ws && state.Scope.ProjectPath is { } path)
        {
            var found = ws.Projects.ToList().FindIndex(p => PathUtil.AreEqual(p.Path, path));
            index = found < 0 ? 0 : found + 1;
        }
        if (state.Workspace is not null && _projectList.SelectedIndex != index)
        {
            _projectList.SelectedIndex = index;
        }
    }

    private void RenderMiddle(WorkspaceViewState state)
    {
        var updatingDependencies = _applying || state.IsBusy && state.Status == WorkspaceStatus.Writing;
        var key = _browseMode
            ? (object)(true, state.Workspace, state.Search, state.Browse, state.SearchLoading, state.SearchError,
                state.SelectedPackageId, state.SelectedPackageSourceName, state.SearchQuery, state.Installed, updatingDependencies)
            : (false, state.Workspace, state.Upgrades, state.Installed, state.Implicit, state.SdkImplicit, state.SelectedPackageIds,
                state.SelectedPackageId, state.IsBusy, state.UpdatesComplete, state.Diagnostics, state.Status, updatingDependencies);
        if (Equals(key, _middleKey))
        {
            return;
        }
        _middleKey = key;
        _searchRing.IsActive = false;
        _installedRing.IsActive = false;
        _middle.Clear();

        if (state.Workspace is null)
        {
            _middle.Add((PresentationModel.BuildActivity(state, _browseMode).InstalledWorkspace
                ? LoadingLine(_installedRing, L(TextKey.LoadingWorkspace))
                : Muted(L(TextKey.M091))).Margin(4, 12, 4, 0));
            return;
        }
        if (updatingDependencies)
            _middle.Add(LoadingLine(_installedRing, L(TextKey.UpdatingDependencies)).Margin(4, 4, 4, 6));
        if (_browseMode)
        {
            RenderSearch(state);
            return;
        }

        var model = PresentationModel.BuildPackageList(state);
        if (!updatingDependencies && PresentationModel.BuildActivity(state, false).InstalledCheck)
            _middle.Add(LoadingLine(_installedRing, L(TextKey.CheckingUpdates)).Margin(4, 4, 4, 6));
        else if (!updatingDependencies && model.Notice is { } notice)
        {
            var text = Muted(notice).Margin(4, 4, 4, 6);
            if (!state.UpdatesComplete && state.Status != WorkspaceStatus.Loading)
            {
                text.Foreground(OperationDialog.WarningColor);
            }
            _middle.Add(text);
        }
        foreach (var diagnostic in state.Diagnostics.Take(5))
        {
            _middle.Add(WarningLine($"{(string.IsNullOrEmpty(diagnostic.SourceName) ? "" : diagnostic.SourceName + "：")}{FirstLine(diagnostic.Message)}")
                .Margin(4, 0, 4, 4).ToolTip(F(diagnostic.Message)));
        }
        if (model.Groups.Count == 0)
        {
            _middle.Add(Muted(L(!state.SelectedSources.IsAll && state.SelectedSources.Resolve(state.Sources).Count == 0
                ? TextKey.SelectInstalledSources : TextKey.M093)).Margin(4, 8, 4, 0));
        }
        foreach (var group in model.Groups)
        {
            _middle.Add(GroupHeader(group));
            foreach (var row in group.Rows)
            {
                _middle.Add(PackageRow(row, state));
            }
        }
    }

    private Element GroupHeader(PackageGroupModel group)
    {
        var grid = new Grid().Columns("*,Auto,Auto").Children(
            new TextBlock().Text($"{F(group.Title)}  {group.Rows.Count}").Bold().CenterVertical());
        foreach (var action in group.Actions)
        {
            switch (action.Kind)
            {
                case GroupActionKind.SelectAll:
                    grid.Add(new CheckBox()
                        .Content(F(action.Label))
                        .IsChecked(action.IsChecked)
                        .IsEnabled(action.IsEnabled)
                        .Column(1)
                        .Margin(0, 0, 8, 0)
                        .CenterVertical()
                        .OnCheckedChanged(value =>
                        {
                            if (!_suppressEvents)
                            {
                                _controller.SelectAllUpgrades(value == true);
                            }
                        }));
                    break;
                case GroupActionKind.UpgradeSelected:
                    grid.Add(new Button()
                        .Content(F(action.Label))
                        .IsEnabled(action.IsEnabled)
                        .Column(2)
                        .OnClick(() => Fire(() => ApplyAsync(ct => _controller.PreviewSelectedUpgradesAsync(ct)))));
                    break;
            }
        }
        return new Border()
            .Background(HeaderRowColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .Padding(8, 5)
            .Margin(0, 8, 0, 0)
            .Child(grid);
    }

    private Element PackageRow(PackageRowModel row, WorkspaceViewState state)
    {
        var grid = new Grid().Columns("Auto,*,Auto");
        if (row.CanSelect)
        {
            grid.Add(new CheckBox()
                .IsChecked(row.IsSelected)
                .IsEnabled(!state.IsBusy)
                .CenterVertical()
                .Margin(0, 0, 8, 0)
                .OnCheckedChanged(value =>
                {
                    if (!_suppressEvents)
                    {
                        _controller.SetUpgradeSelected(row.PackageId, value == true);
                    }
                }));
        }
        grid.Add(new StackPanel().Vertical().Column(1).Children(
            new TextBlock().Text(row.PackageId).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis),
            Muted(row.ProjectsText).FontSize(ThemeFontSize.Small)));
        if (row.Group == PackageGroupKind.Upgrades)
        {
            var upgrade = state.Upgrades.First(u => string.Equals(u.PackageId, row.PackageId, StringComparison.OrdinalIgnoreCase));
            grid.Add(new StackPanel().Horizontal().Spacing(6).Column(2).CenterVertical().Margin(8, 0, 0, 0).Children(
                new TextBlock().Text(string.Join(", ", upgrade.FromVersions)).Foreground(AccentColor),
                new GlyphElement().Kind(GlyphKind.ChevronRight).GlyphSize(4).Foreground(AccentColor).CenterVertical(),
                new TextBlock().Text(upgrade.ToVersion).Foreground(AccentColor)));
        }
        else grid.Add(new TextBlock().Text(row.VersionText).Column(2).CenterVertical().Margin(8, 0, 0, 0));

        var selected = string.Equals(state.SelectedPackageId, row.PackageId, StringComparison.OrdinalIgnoreCase);
        return new Border()
            .Background(selected ? SelectedRowColor : AppTheme.Current.Row)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .Padding(8, 6)
            .Child(grid)
            .OnMouseDown(e => PackagePointer(e, row.PackageId, row.DetailVersion, null));
    }

    private void RenderSearch(WorkspaceViewState state)
    {
        if (state.SearchError is { } error)
        {
            _middle.Add(Wrapped(L(TextKey.M094) + FirstLine(error)).Foreground(OperationDialog.ErrorColor).ToolTip(F(error)).Margin(4, 8, 4, 0));
            _middle.Add(new Button().Content(L(TextKey.M095)).OnClick(() => Fire(() => _controller.SearchAsync(state.SearchQuery, 0, CancellationToken.None))));
        }
        if (state.Browse is { } browse)
        {
            if (browse.Sources.Count == 0) _middle.Add(Muted(_state.Sources.Count == 0 ? L(TextKey.M096) : L(TextKey.SelectSources)).Margin(4, 8, 4, 0));
            foreach (var group in browse.Sources)
            {
                _middle.Add(new Border().Background(HeaderRowColor).Padding(8, 8).Margin(0, 8, 0, 0)
                    .Child(new TextBlock().Text(F(PresentationModel.BrowseGroupTitle(group))).SemiBold()));
                RenderSearchItems(group.Page, state);
                if (group.Page.Diagnostics.Count > 0)
                {
                    _middle.Add(new Button().Content(state.SelectedSources.IsAll ? L(TextKey.M097) : L(TextKey.M098))
                        .Margin(0, 6, 0, 0).IsEnabled(!state.SearchLoading && !state.IsBusy)
                        .OnClick(() => Fire(() => _controller.LoadBrowseAsync(true, CancellationToken.None))));
                }
            }
        }
        if (state.Search is { } page)
        {
            RenderSearchItems(page, state);
            if (page.HasMore && !state.SearchLoading)
            {
                _middle.Add(new Button().Content(L(TextKey.M099)).Margin(0, 8, 0, 0)
                    .OnClick(() => Fire(() => _controller.SearchAsync(state.SearchQuery, page.NextSkip, CancellationToken.None))));
            }
        }
        if (state.SearchLoading)
        {
            _middle.Add(LoadingLine(_searchRing, string.IsNullOrWhiteSpace(state.SearchQuery) ? L(TextKey.M100) : L(TextKey.M101))
                .Margin(4, 8, 4, 0));
        }
    }

    private void RenderSearchItems(SearchPage page, WorkspaceViewState state)
    {
        foreach (var diagnostic in page.Diagnostics)
            _middle.Add(WarningLine($"{diagnostic.SourceName}: {FirstLine(diagnostic.Message)}").Margin(4, 0, 4, 4).ToolTip(F(diagnostic.Message)));
        if (page.Items.Count == 0 && !state.SearchLoading)
            _middle.Add(Muted(page.Diagnostics.Count > 0 ? L(TextKey.M102) : L(TextKey.M103)).Margin(4, 8, 4, 0));
        foreach (var hit in page.Items)
        {
            var selected = string.Equals(state.SelectedPackageId, hit.PackageId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.SelectedPackageSourceName, hit.SourceName, StringComparison.OrdinalIgnoreCase);
            var installed = state.Installed.Any(o => string.Equals(o.PackageId, hit.PackageId, StringComparison.OrdinalIgnoreCase));
            _middle.Add(new Border().Background(selected ? SelectedRowColor : AppTheme.Current.Row).BorderBrush(BorderColor)
                .BorderThickness(new Thickness(0, 0, 0, 1)).Padding(8, 6)
                .OnMouseDown(e => PackagePointer(e, hit.PackageId, hit.Version, hit.SourceName))
                .Child(new Grid().Columns("*,Auto").Children(
                    new StackPanel().Vertical().Children(
                        new TextBlock().Text(installed ? LF(TextKey.M104, $"{hit.PackageId}（已安装）") : hit.PackageId).SemiBold(),
                        Muted(FirstLine(hit.Description ?? L(TextKey.M105))).FontSize(ThemeFontSize.Small).TextTrimming(TextTrimming.CharacterEllipsis)),
                    new StackPanel().Vertical().Column(1).Margin(8, 0, 0, 0).Children(
                        new TextBlock().Text(hit.Version), Muted(hit.SourceName).FontSize(ThemeFontSize.Small)))));
        }
    }

    private static FrameworkElement LoadingLine(ProgressRing ring, string message) =>
        new StackPanel().Horizontal().Spacing(8).Children(ring.Width(18).Height(18).IsActive(true).CenterVertical(),
            new TextBlock().Text(message).TextWrapping(TextWrapping.Wrap).CenterVertical());

    private static FrameworkElement WarningLine(string message) =>
        new Grid().Columns("Auto,*").Children(
            new PromptIcon { Kind = PromptIconKind.Warning }.Width(16).Height(16).Margin(0, 2, 6, 0).Top(),
            Wrapped(message).Column(1).Foreground(OperationDialog.WarningColor).FontSize(ThemeFontSize.Small));

    private void RenderDetails(WorkspaceViewState state)
    {
        var key = (state.SelectedPackageId, state.SelectedPackageSourceName, state.SelectedVersion, state.Versions, state.VersionsLoading, state.Details, state.DetailsLoading, state.DetailsError,
            state.ProjectStates, state.IsBusy);
        if (Equals(key, _detailsKey))
        {
            return;
        }
        _detailsKey = key;
        _versionsRing.IsActive = false;
        _detailsRing.IsActive = false;
        _details.Clear();

        if (state.SelectedPackageId is not { } packageId)
        {
            _details.Add(Muted(L(TextKey.M106)));
            return;
        }
        _details.Add(new TextBlock().Text(packageId).FontSize(18).Bold().TextWrapping(TextWrapping.Wrap));

        var versions = state.Versions?.Versions is { Count: > 0 } list ? list.ToList() : [];
        if (state.SelectedVersion is { } current && !versions.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            versions.Insert(0, current);
        }
        var combo = new ComboBox().Items(versions.ToArray()).Width(180);
        combo.SelectedIndex = versions.FindIndex(v => string.Equals(v, state.SelectedVersion, StringComparison.OrdinalIgnoreCase));
        combo.OnSelectionChanged(_ =>
        {
            if (!_suppressEvents && combo.SelectedIndex >= 0 && combo.SelectedIndex < versions.Count)
            {
                SelectPackage(packageId, versions[combo.SelectedIndex], state.SelectedPackageSourceName ?? state.Versions?.SourceName);
            }
        });
        var versionRow = new StackPanel().Horizontal().Spacing(8).Children(new TextBlock().Text(L(TextKey.M107)).CenterVertical(), combo);
        if (PresentationModel.BuildActivity(state, _browseMode).Versions)
            versionRow.Add(LoadingLine(_versionsRing, L(TextKey.M108)));
        _details.Add(versionRow);
        if (state.Versions?.Diagnostics is { Count: > 0 } versionDiagnostics)
        {
            foreach (var diagnostic in versionDiagnostics)
            {
                _details.Add(WarningLine(FirstLine(diagnostic.Message)));
            }
        }

        _details.Add(SectionTitle(L(TextKey.M109)));
        foreach (var row in PresentationModel.BuildProjectRows(state))
        {
            _details.Add(ProjectRow(row, packageId, state));
        }

        _details.Add(SectionTitle(L(TextKey.M110)));
        if (PresentationModel.BuildActivity(state, _browseMode).Details)
        {
            _details.Add(LoadingLine(_detailsRing, L(TextKey.M111)));
        }
        else if (state.DetailsError is { } detailsError)
        {
            _details.Add(Wrapped(L(TextKey.M112) + F(detailsError)).Foreground(OperationDialog.ErrorColor));
        }
        else if (state.Details is { } details)
        {
            AddField(L(TextKey.M113), details.Authors);
            AddField(L(TextKey.M114), details.Description);
            AddField(L(TextKey.M115), details.License);
            AddField(L(TextKey.M116), details.ProjectUrl?.ToString());
            AddField(L(TextKey.M117), details.Frameworks.Count > 0 ? string.Join("、", details.Frameworks) : null);
            if (details.Origin is { } origin)
            {
                _details.Add(Muted(L(TextKey.M118) + F(origin)).FontSize(ThemeFontSize.Small));
            }
            _details.Add(new TextBlock().Text(L(TextKey.M119)).SemiBold());
            if (details.Dependencies.Count == 0)
            {
                _details.Add(Muted(L(TextKey.M120)).Margin(8, 0, 0, 0));
            }
            foreach (var group in details.Dependencies)
            {
                _details.Add(new TextBlock().Text(group.Framework.Length == 0 ? L(TextKey.M070) : group.Framework).Margin(8, 0, 0, 0));
                if (group.Items.Count == 0)
                {
                    _details.Add(Muted(L(TextKey.M121)).Margin(20, 0, 0, 0).FontSize(ThemeFontSize.Small));
                }
                foreach (var dependency in group.Items)
                {
                    _details.Add(Muted($"{dependency.PackageId} {dependency.VersionRange}").Margin(20, 0, 0, 0).FontSize(ThemeFontSize.Small));
                }
            }
            foreach (var diagnostic in details.Diagnostics)
            {
                _details.Add(WarningLine($"{diagnostic.SourceName}: {F(diagnostic.Message)}"));
            }
        }
    }

    private Element ProjectRow(ProjectRowModel row, string packageId, WorkspaceViewState state)
    {
        var info = new StackPanel().Vertical().Children(
            new TextBlock().Text(row.ProjectName).SemiBold(),
            Wrapped(row.VersionText).Foreground(OperationDialog.MutedColor).FontSize(ThemeFontSize.Small));
        if (row.Note is { } note)
        {
            info.Add(Wrapped(note).Foreground(OperationDialog.WarningColor).FontSize(ThemeFontSize.Small));
        }
        var grid = new Grid().Columns("*,Auto,Auto").Children(info);
        if (row.ActionLabel is { } label)
        {
            var install = row.Action == ProjectAction.Install;
            var change = install
                ? new PackageChange(PackageAction.Install, row.ProjectPath, packageId, state.SelectedVersion)
                : new PackageChange(PackageAction.Uninstall, row.ProjectPath, packageId, null);
            grid.Add(ProjectActionButton(label, install ? GlyphKind.Plus : GlyphKind.Minus, row.ActionEnabled, change).Column(2));
        }
        if (row.VersionChange is { } version)
        {
            var glyph = version.Action switch
            {
                PackageAction.Upgrade => GlyphKind.ChevronUp,
                PackageAction.Downgrade => GlyphKind.ChevronDown,
                _ => GlyphKind.ChevronRight,
            };
            grid.Add(ProjectActionButton(version.Label, glyph, version.IsEnabled,
                new(version.Action, row.ProjectPath, packageId, version.Version)).Column(1));
        }
        return new Border()
            .Background(AppTheme.Current.Row)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(1))
            .CornerRadius(new CornerRadius(4))
            .Padding(8, 6)
            .Child(grid);
    }

    private Button ProjectActionButton(UiMessage label, GlyphKind kind, bool enabled, PackageChange change) =>
        new Button { CommandData = change }
                .Command(ProjectPackageCommand, CommandPresentationMode.None)
                .Content(new GlyphElement().Kind(kind).GlyphSize(5).Width(16).Height(16))
                .ToolTip(F(label))
                .Width(32).Height(30).Padding(6)
                .WithTheme((theme, button) =>
                {
                    var palette = AppTheme.From(theme);
                    var color = change.Action switch
                    {
                        PackageAction.Install => palette.Success,
                        PackageAction.Uninstall => palette.Error,
                        _ => palette.Accent,
                    };
                    button.Foreground = color;
                    button.Background = palette.ActionBackground(color);
                    button.BorderBrush = color;
                }, invokeImmediately: true)
                .IsEnabled(enabled)
                .CenterVertical()
                .Margin(8, 0, 0, 0);

    private void AddField(string label, string? value)
    {
        _details.Add(new Grid().Columns("72,*").Children(
            Muted(label),
            Wrapped(string.IsNullOrWhiteSpace(value) ? L(TextKey.M122) : value).Column(1)));
    }

    private void AddField(string label, UiMessage? value) => AddField(label, value is null ? null : F(value));

    private void SetBrowseMode(bool browse)
    {
        _browseMode = browse;
        _middleKey = null;
        Render(_controller.State);
        if (browse)
        {
            _searchBox.Focus();
            if (_state.Workspace is not null)
                Fire(() => _controller.SearchAsync(_searchBox.Text?.Trim() ?? "", 0, CancellationToken.None));
        }
    }

    private void ScheduleSearch()
    {
        if (_suppressEvents || _state.Workspace is null)
        {
            return;
        }
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var query = _searchBox.Text?.Trim() ?? "";
        Fire(async () =>
        {
            await Task.Delay(350, cts.Token).ConfigureAwait(false);
            await _controller.SearchAsync(query, 0, cts.Token).ConfigureAwait(false);
        });
    }

    private void SelectPackage(string packageId, string version, string? source)
    {
        if (_state.Workspace is null)
        {
            return;
        }
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        Fire(() => _controller.SelectPackageAsync(packageId, version, source, cts.Token));
    }

    private async Task OpenFolderAsync()
    {
        var folder = FileDialog.SelectFolder(new FolderDialogOptions
        {
            Owner = this,
            Title = L(TextKey.M123),
            PreferNative = true,
            InitialDirectory = _state.Workspace?.Directory,
        });
        if (folder is not null)
        {
            await OpenDirectoryAsync(folder, null);
        }
    }

    private Task OpenDirectoryAsync(string directory, string? solutionPath)
    {
        if (!Directory.Exists(directory))
        {
            return MessageBox.NotifyAsync(L(TextKey.M124), PromptIconKind.Error, directory, this);
        }
        _detailsKey = null;
        return RunBusyAsync(async ct =>
        {
            await _controller.OpenAsync(directory, solutionPath, ct);
            if (_browseMode && _controller.State.Workspace is not null)
                await _controller.SearchAsync("", 0, ct);
        });
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        _busyCts = cts;
        try
        {
            await work(cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_busyCts, cts))
            {
                _busyCts = null;
            }
            OnStateChanged(_controller.State);
        }
    }

    /// <summary>Preview → confirmation dialog → execution, with failure details shown afterwards.</summary>
    private async Task ApplyAsync(Func<CancellationToken, Task<OperationPlan>> preview)
    {
        // Preparing the plan takes seconds; repeated clicks must not queue a second operation.
        if (_applying || _configurationPage?.IsSaving == true || _configurationPage?.IsEditing == true)
        {
            return;
        }
        _applying = true;
        Render(_state);
        try
        {
            await ApplyCoreAsync(preview);
        }
        finally
        {
            _applying = false;
            Render(_state);
        }
    }

    private async Task ApplyCoreAsync(Func<CancellationToken, Task<OperationPlan>> preview)
    {
        var plan = await preview(CancellationToken.None);
        if (_preferences.QuickMode && !plan.CanExecute)
        {
            throw UiErrors.InvalidOperation(UiMessage.Join(Environment.NewLine,
                [new UiMessage(TextKey.OperationBlocked), .. plan.Blockers]));
        }
        if (PresentationModel.RequiresConfirmation(plan, _preferences))
        {
            var confirmed = await OnUiAsync(() => Dialogs.ConfirmAsync(this, plan, _controller.State.Scope));
            if (!confirmed) return;
        }
        await RunBusyAsync(ct => _controller.ExecuteConfirmedAsync(plan, ct));
        var state = _controller.State;
        if (state.LastOperation is { Status: not OperationStatus.Succeeded } result && state.Status != WorkspaceStatus.Ready)
        {
            await OnUiAsync(async () =>
            {
                await Dialogs.ShowFailureAsync(this, result);
                return true;
            });
        }
        else if (state.Status == WorkspaceStatus.Failed && state.Error is { } error)
        {
            await OnUiAsync(async () =>
            {
                await MessageBox.NotifyAsync(state.StatusMessage is { } message ? F(message) : L(TextKey.M125), PromptIconKind.Error, F(error), this);
                return true;
            });
        }
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.IsOnUIThread)
        {
            return action();
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                tcs.SetResult(await action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>UI event boundary: runs async work and reports unexpected failures instead of crashing.</summary>
    private async void Fire(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await OnUiAsync(async () =>
                {
                    await MessageBox.NotifyAsync(L(TextKey.M126), PromptIconKind.Error, F(UiMessage.FromException(ex)), this);
                    return true;
                });
            }
            catch (Exception)
            {
                // The window may already be closed; nothing left to report to.
            }
        }
    }

    private static IDispatcher Dispatcher => Application.Current.Dispatcher ?? throw UiErrors.InvalidOperation(Messages.M(TextKey.M127));

    private string StatusLabel(WorkspaceStatus status) => status switch
    {
        WorkspaceStatus.Idle => L(TextKey.Ready),
        WorkspaceStatus.Loading => L(TextKey.M129),
        WorkspaceStatus.Ready => L(TextKey.Ready),
        WorkspaceStatus.Writing => L(TextKey.M130),
        WorkspaceStatus.Cancelled => L(TextKey.M131),
        WorkspaceStatus.Failed => L(TextKey.M132),
        _ => L(TextKey.M133),
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
    private string FirstLine(UiMessage text) => FirstLine(F(text));
    private TextBlock Muted(UiMessage text) => Muted(F(text));
    private TextBlock Wrapped(UiMessage text) => Wrapped(F(text));
    private FrameworkElement WarningLine(UiMessage text) => WarningLine(F(text));

    private static TextBlock Muted(string text) => new TextBlock().Text(text).Foreground(OperationDialog.MutedColor).TextWrapping(TextWrapping.Wrap);

    private static TextBlock Wrapped(string text) => new TextBlock().Text(text).TextWrapping(TextWrapping.Wrap);

    private static TextBlock SectionTitle(string text) => new TextBlock().Text(text).Bold().Margin(0, 8, 0, 0);
}
