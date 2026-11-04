using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using NugetManager.Domain;
using NugetManager.Operations;

namespace NugetManager.Presentation;

/// <summary>
/// Three-column package manager window. All controller notifications are marshalled through the MewUI dispatcher
/// and coalesced so that only the latest state is rendered.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Color SelectedRowColor = Color.FromRgb(0xE3, 0xEE, 0xFB);
    private static readonly Color HeaderRowColor = Color.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly Color PaneColor = Color.FromRgb(0xFA, 0xFA, 0xFA);
    private static readonly Color BorderColor = Color.FromRgb(0xDD, 0xDD, 0xDD);
    private static readonly Color AccentColor = Color.FromRgb(0x1F, 0x6F, 0xD1);

    private readonly WorkspaceController _controller;
    private readonly string? _initialDirectory;
    private readonly string? _recoveryDirectory;
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
    private readonly Button _installedTab = new();
    private readonly Button _browseTab = new();
    private readonly Button _openButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _cancelButton = new();
    private readonly CheckBox _prerelease = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _progressText = new();
    private readonly TextBlock _errorText = new();
    private readonly ComboBox _sourcePicker = new();
    private readonly ProgressRing _statusRing = new();
    private readonly ProgressRing _searchRing = new();
    private readonly ProgressRing _detailsRing = new();
    private readonly ProgressRing _versionsRing = new();
    private object? _sourcesKey;

    private bool _browseMode;
    private bool _suppressEvents;
    private object? _projectsKey;
    private object? _middleKey;
    private object? _detailsKey;
    private CancellationTokenSource? _busyCts;
    private bool _applying;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;

    private MainWindow(WorkspaceController controller, string? initialDirectory, string? recoveryDirectory)
    {
        _controller = controller;
        _initialDirectory = initialDirectory;
        _recoveryDirectory = recoveryDirectory;
        _state = controller.State;
        this.Title("NuGet 包管理器").Resizable(1280, 800, minWidth: 980, minHeight: 560);
        Content = BuildLayout();
        _controller.StateChanged += OnStateChanged;
        this.OnLoaded(() =>
        {
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
            _busyCts?.Cancel();
            _searchCts?.Cancel();
            _detailsCts?.Cancel();
            _statusRing.IsActive = false;
            _searchRing.IsActive = false;
            _detailsRing.IsActive = false;
            _versionsRing.IsActive = false;
        });
    }

    public static Window Create(WorkspaceController controller, string? initialDirectory = null, string? recoveryDirectory = null) =>
        new MainWindow(controller, initialDirectory, recoveryDirectory);

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
                await MessageBox.NotifyAsync("无法检查未完成的操作", PromptIconKind.Warning, $"{_recoveryDirectory}{Environment.NewLine}{ex.Message}", this);
                return true;
            });
            return;
        }

        foreach (var item in pending)
        {
            var choice = await OnUiAsync(() => OperationDialog.ShowRecoveryAsync(this, item));
            try
            {
                if (choice == RecoveryChoice.Recover)
                {
                    var results = await RecoveryCoordinator.RecoverAsync(item, CancellationToken.None);
                    await OnUiAsync(async () =>
                    {
                        await OperationDialog.ShowRecoveryResultAsync(this, item, results);
                        return true;
                    });
                }
                else if (choice == RecoveryChoice.Discard)
                {
                    var confirmed = await OnUiAsync(() => MessageBox.ConfirmAsync("保留当前文件并删除这条备份记录？", PromptIconKind.Warning,
                        "删除后将不能再用该备份撤回这次操作。备份目录：" + item.BackupDirectory, this));
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
                    await MessageBox.NotifyAsync("恢复未完成，文件保持现状", PromptIconKind.Error,
                        $"{ex.Message}{Environment.NewLine}备份目录：{item.BackupDirectory}", this);
                    return true;
                });
            }
        }
    }

    private Element BuildLayout()
    {
        _openButton.Content("打开目录…").OnClick(() => Fire(OpenFolderAsync));
        _refreshButton.Content("刷新").OnClick(() => Fire(() => RunBusyAsync(ct => _controller.RefreshAsync(ct))));
        _cancelButton.Content("取消").IsVisible(false).OnClick(() => _busyCts?.Cancel());
        _prerelease.Content("包含预发布版本").OnCheckedChanged(value =>
        {
            if (!_suppressEvents)
            {
                Fire(() => RunBusyAsync(ct => _controller.SetIncludePrereleaseAsync(value == true, ct)));
            }
        });

        var toolbar = new Border()
            .Background(HeaderRowColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .Padding(10, 6)
            .DockTop()
            .Child(new Grid()
                .Columns("Auto,*")
                .Children(
                    new StackPanel().Horizontal().Spacing(8).Children(_openButton, _refreshButton, _prerelease),
                    _workspaceText.Column(1).Margin(16, 0, 0, 0).CenterVertical().TextTrimming(TextTrimming.CharacterEllipsis)));

        _errorText.Foreground(OperationDialog.ErrorColor).TextTrimming(TextTrimming.CharacterEllipsis);
        _progressText.Foreground(OperationDialog.MutedColor).TextTrimming(TextTrimming.CharacterEllipsis);
        var statusBar = new Border()
            .Background(HeaderRowColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 1, 0, 0))
            .Padding(10, 4)
            .DockBottom()
            .Child(new Grid()
                .Columns("Auto,*,Auto")
                .Children(
                    _statusText.CenterVertical(),
                    new StackPanel().Vertical().Column(1).Margin(16, 0).Children(_progressText, _errorText),
                    new StackPanel().Horizontal().Spacing(8).Column(2).Children(
                        _statusRing.Width(20).Height(20).CenterVertical().IsActive(false).IsVisible(false),
                        _cancelButton)));

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
                new TextBlock().Text("范围").Bold().Margin(2, 0, 0, 6).DockTop(),
                _solutionPanel.DockTop(),
                _projectList));

        _installedTab.Content("已安装").OnClick(() => SetBrowseMode(false));
        _browseTab.Content("浏览").OnClick(() => SetBrowseMode(true));
        _searchBox.Placeholder("搜索所选源中的 NuGet 包").IsVisible(false).OnTextChanged(_ => ScheduleSearch());
        _sourcePicker.Width(210).OnSelectionChanged(_ =>
        {
            if (_suppressEvents || !_browseMode || _state.Workspace is null) return;
            var index = _sourcePicker.SelectedIndex;
            var source = index > 0 && index <= _state.Sources.Count ? _state.Sources[index - 1].Name : null;
            if (string.Equals(source, _state.SelectedSourceName, StringComparison.OrdinalIgnoreCase)) return;
            _searchCts?.Cancel();
            _detailsCts?.Cancel();
            var query = _searchBox.Text?.Trim() ?? "";
            Fire(() => _controller.SelectSourceAsync(source, CancellationToken.None, query));
        });
        var middle = new DockPanel().Children(
            new StackPanel().Vertical().Spacing(6).Margin(8, 8, 8, 4).DockTop().Children(
                new Grid().Columns("*,Auto").Children(
                    new StackPanel().Horizontal().Spacing(4).Children(_installedTab, _browseTab),
                    new StackPanel().Horizontal().Spacing(6).Column(1).Children(
                        Muted("包源").CenterVertical(), _sourcePicker)),
                _searchBox),
            new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(_middle.Margin(8, 0, 8, 8)));

        var right = new Border()
            .Background(PaneColor)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(1, 0, 0, 0))
            .Child(new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(_details.Margin(12)));

        return new DockPanel().Children(
            toolbar,
            statusBar,
            new Grid().Columns("240,*,400").Children(left, middle.Column(1), right.Column(2)));
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
        _workspaceText.Text = state.Workspace is { } w ? w.SolutionPath ?? w.Directory : "未打开工作区";
        _statusText.Text = state.StatusMessage ?? StatusLabel(state.Status);
        _progressText.Text = state.Progress ?? "";
        _progressText.IsVisible = !string.IsNullOrEmpty(state.Progress) && state.IsBusy;
        _errorText.Text = state.Error ?? "";
        _errorText.IsVisible = !string.IsNullOrEmpty(state.Error);
        _errorText.ToolTip(state.Error ?? "");
        _cancelButton.IsVisible = state.IsBusy && _busyCts is not null;
        _openButton.IsEnabled = !state.IsBusy;
        _refreshButton.IsEnabled = !state.IsBusy && state.Workspace is not null;
        _prerelease.IsEnabled = !state.IsBusy;
        _prerelease.IsChecked = state.IncludePrerelease;
        _projectList.IsEnabled = !state.IsBusy || state.Status != WorkspaceStatus.Writing;
        _installedTab.IsEnabled = _browseMode;
        _browseTab.IsEnabled = !_browseMode && state.Workspace is not null;
        _searchBox.IsVisible = _browseMode;
        var activity = PresentationModel.BuildActivity(state, _browseMode);
        _statusRing.IsActive = activity.Workspace;
        _statusRing.IsVisible = activity.Workspace;
        _sourcePicker.IsEnabled = activity.SourceSelection;
        if (!Equals(_sourcesKey, state.Sources))
        {
            _sourcesKey = state.Sources;
            _sourcePicker.Items(new[] { "全部源" }.Concat(state.Sources.Select(s => s.Name)).ToArray());
        }
        var sourceIndex = state.SelectedSourceName is null ? 0
            : state.Sources.ToList().FindIndex(s => string.Equals(s.Name, state.SelectedSourceName, StringComparison.OrdinalIgnoreCase)) + 1;
        if (_sourcePicker.SelectedIndex != sourceIndex) _sourcePicker.SelectedIndex = sourceIndex;
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
                ? new[] { $"整个工作区（{workspace.Projects.Count} 个项目）" }.Concat(workspace.Projects.Select(p => p.Name)).ToArray()
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
        var key = _browseMode
            ? (object)(true, state.Workspace, state.Search, state.Browse, state.SearchLoading, state.SearchError,
                state.SelectedPackageId, state.SelectedPackageSourceName, state.SearchQuery, state.Installed)
            : (false, state.Workspace, state.Upgrades, state.Installed, state.Implicit, state.SdkImplicit, state.SelectedPackageIds,
                state.SelectedPackageId, state.IsBusy, state.UpdatesComplete, state.Diagnostics, state.Status);
        if (Equals(key, _middleKey))
        {
            return;
        }
        _middleKey = key;
        _searchRing.IsActive = false;
        _middle.Clear();

        if (state.Workspace is null)
        {
            _middle.Add(Muted(state.Status == WorkspaceStatus.Loading
                ? "正在加载工作区…"
                : "点击“打开目录…”选择包含 .sln / .slnx 或项目文件的目录。").Margin(4, 12, 4, 0));
            return;
        }
        if (_browseMode)
        {
            RenderSearch(state);
            return;
        }

        var model = PresentationModel.BuildPackageList(state);
        if (model.Notice is { } notice)
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
                .Margin(4, 0, 4, 4).ToolTip(diagnostic.Message));
        }
        if (model.Groups.Count == 0)
        {
            _middle.Add(Muted("当前范围没有包引用。").Margin(4, 8, 4, 0));
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
            new TextBlock().Text($"{group.Title}  {group.Rows.Count}").Bold().CenterVertical());
        foreach (var action in group.Actions)
        {
            switch (action.Kind)
            {
                case GroupActionKind.SelectAll:
                    grid.Add(new CheckBox()
                        .Content(action.Label)
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
                        .Content(action.Label)
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
            .Background(selected ? SelectedRowColor : Color.White)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .Padding(8, 6)
            .Child(grid)
            .OnMouseDown(_ => SelectPackage(row.PackageId, row.DetailVersion, null));
    }

    private void RenderSearch(WorkspaceViewState state)
    {
        if (state.SearchError is { } error)
        {
            _middle.Add(Wrapped("搜索失败：" + FirstLine(error)).Foreground(OperationDialog.ErrorColor).ToolTip(error).Margin(4, 8, 4, 0));
            _middle.Add(new Button().Content("重试").OnClick(() => Fire(() => _controller.SearchAsync(state.SearchQuery, 0, CancellationToken.None))));
        }
        if (state.Browse is { } browse)
        {
            if (browse.Sources.Count == 0) _middle.Add(Muted("工作区没有启用的包源。").Margin(4, 8, 4, 0));
            foreach (var group in browse.Sources)
            {
                _middle.Add(new Border().Background(HeaderRowColor).Padding(8, 8).Margin(0, 8, 0, 0)
                    .Child(new TextBlock().Text(PresentationModel.BrowseGroupTitle(group)).SemiBold()));
                RenderSearchItems(group.Page, state);
                if (group.Page.Diagnostics.Count > 0)
                {
                    _middle.Add(new Button().Content(state.SelectedSourceName is null ? "重新加载全部包源" : "重试包源")
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
                _middle.Add(new Button().Content("加载更多").Margin(0, 8, 0, 0)
                    .OnClick(() => Fire(() => _controller.SearchAsync(state.SearchQuery, page.Items.Count, CancellationToken.None))));
            }
        }
        if (state.SearchLoading)
        {
            _middle.Add(LoadingLine(_searchRing, string.IsNullOrWhiteSpace(state.SearchQuery) ? "正在加载包源…" : "正在搜索…")
                .Margin(4, 8, 4, 0));
        }
    }

    private void RenderSearchItems(SearchPage page, WorkspaceViewState state)
    {
        foreach (var diagnostic in page.Diagnostics)
            _middle.Add(WarningLine($"{diagnostic.SourceName}：{FirstLine(diagnostic.Message)}").Margin(4, 0, 4, 4).ToolTip(diagnostic.Message));
        if (page.Items.Count == 0 && !state.SearchLoading)
            _middle.Add(Muted(page.Diagnostics.Count > 0 ? "没有结果（包源查询失败）。" : "没有找到匹配的包。").Margin(4, 8, 4, 0));
        foreach (var hit in page.Items)
        {
            var selected = string.Equals(state.SelectedPackageId, hit.PackageId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.SelectedPackageSourceName, hit.SourceName, StringComparison.OrdinalIgnoreCase);
            var installed = state.Installed.Any(o => string.Equals(o.PackageId, hit.PackageId, StringComparison.OrdinalIgnoreCase));
            _middle.Add(new Border().Background(selected ? SelectedRowColor : Color.White).BorderBrush(BorderColor)
                .BorderThickness(new Thickness(0, 0, 0, 1)).Padding(8, 6)
                .OnMouseDown(_ => SelectPackage(hit.PackageId, hit.Version, hit.SourceName))
                .Child(new Grid().Columns("*,Auto").Children(
                    new StackPanel().Vertical().Children(
                        new TextBlock().Text(installed ? $"{hit.PackageId}（已安装）" : hit.PackageId).SemiBold(),
                        Muted(FirstLine(hit.Description ?? "未提供简介")).FontSize(ThemeFontSize.Small).TextTrimming(TextTrimming.CharacterEllipsis)),
                    new StackPanel().Vertical().Column(1).Margin(8, 0, 0, 0).Children(
                        new TextBlock().Text(hit.Version), Muted(hit.SourceName).FontSize(ThemeFontSize.Small)))));
        }
    }

    private static FrameworkElement LoadingLine(ProgressRing ring, string message) =>
        new StackPanel().Horizontal().Spacing(8).Children(ring.Width(18).Height(18).IsActive(true).CenterVertical(), Muted(message).CenterVertical());

    private static FrameworkElement WarningLine(string message) =>
        new Grid().Columns("Auto,*").Children(
            new PromptIcon { Kind = PromptIconKind.Warning }.Width(16).Height(16).Margin(0, 2, 6, 0).Top(),
            Wrapped(message).Column(1).Foreground(OperationDialog.WarningColor).FontSize(ThemeFontSize.Small));

    private void RenderDetails(WorkspaceViewState state)
    {
        var key = (state.SelectedPackageId, state.SelectedPackageSourceName, state.SelectedVersion, state.Versions, state.Details, state.DetailsLoading, state.DetailsError,
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
            _details.Add(Muted("选择一个包查看详情。"));
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
        var versionRow = new StackPanel().Horizontal().Spacing(8).Children(new TextBlock().Text("版本").CenterVertical(), combo);
        if (PresentationModel.BuildActivity(state, _browseMode).Versions)
            versionRow.Add(LoadingLine(_versionsRing, "正在读取版本…"));
        _details.Add(versionRow);
        if (state.Versions?.Diagnostics is { Count: > 0 } versionDiagnostics)
        {
            foreach (var diagnostic in versionDiagnostics)
            {
                _details.Add(WarningLine(FirstLine(diagnostic.Message)));
            }
        }

        _details.Add(SectionTitle("项目"));
        foreach (var row in PresentationModel.BuildProjectRows(state))
        {
            _details.Add(ProjectRow(row, packageId, state));
        }

        _details.Add(SectionTitle("包信息"));
        if (PresentationModel.BuildActivity(state, _browseMode).Details)
        {
            _details.Add(LoadingLine(_detailsRing, "正在读取包信息…"));
        }
        else if (state.DetailsError is { } detailsError)
        {
            _details.Add(Wrapped("读取包信息失败：" + detailsError).Foreground(OperationDialog.ErrorColor));
        }
        else if (state.Details is { } details)
        {
            AddField("作者", details.Authors);
            AddField("简介", details.Description);
            AddField("许可证", details.License);
            AddField("项目地址", details.ProjectUrl?.ToString());
            AddField("支持框架", details.Frameworks.Count > 0 ? string.Join("、", details.Frameworks) : null);
            if (details.Origin is { } origin)
            {
                _details.Add(Muted("来源：" + origin).FontSize(ThemeFontSize.Small));
            }
            _details.Add(new TextBlock().Text("依赖").SemiBold());
            if (details.Dependencies.Count == 0)
            {
                _details.Add(Muted("未提供（或无依赖）").Margin(8, 0, 0, 0));
            }
            foreach (var group in details.Dependencies)
            {
                _details.Add(new TextBlock().Text(group.Framework).Margin(8, 0, 0, 0));
                if (group.Items.Count == 0)
                {
                    _details.Add(Muted("无依赖").Margin(20, 0, 0, 0).FontSize(ThemeFontSize.Small));
                }
                foreach (var dependency in group.Items)
                {
                    _details.Add(Muted($"{dependency.PackageId} {dependency.VersionRange}").Margin(20, 0, 0, 0).FontSize(ThemeFontSize.Small));
                }
            }
            foreach (var diagnostic in details.Diagnostics)
            {
                _details.Add(WarningLine($"{diagnostic.SourceName}：{diagnostic.Message}"));
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
        var grid = new Grid().Columns("*,Auto").Children(info);
        if (row.ActionLabel is { } label)
        {
            grid.Add(new Button()
                .Content(label)
                .IsEnabled(row.ActionEnabled)
                .Column(1)
                .CenterVertical()
                .Margin(8, 0, 0, 0)
                .OnClick(() =>
                {
                    var change = row.Action == ProjectAction.Install
                        ? new PackageChange(PackageAction.Install, row.ProjectPath, packageId, state.SelectedVersion)
                        : new PackageChange(PackageAction.Uninstall, row.ProjectPath, packageId, null);
                    Fire(() => ApplyAsync(ct => _controller.PreviewChangesAsync([change], ct)));
                }));
        }
        return new Border()
            .Background(Color.White)
            .BorderBrush(BorderColor)
            .BorderThickness(new Thickness(1))
            .CornerRadius(new CornerRadius(4))
            .Padding(8, 6)
            .Child(grid);
    }

    private void AddField(string label, string? value)
    {
        _details.Add(new Grid().Columns("72,*").Children(
            Muted(label),
            Wrapped(string.IsNullOrWhiteSpace(value) ? "未提供" : value).Column(1)));
    }

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
            Title = "选择包含解决方案或项目的目录",
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
            return MessageBox.NotifyAsync("目录不存在", PromptIconKind.Error, directory, this);
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
        if (_applying)
        {
            return;
        }
        _applying = true;
        try
        {
            await ApplyCoreAsync(preview);
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task ApplyCoreAsync(Func<CancellationToken, Task<OperationPlan>> preview)
    {
        var plan = await preview(CancellationToken.None);
        var confirmed = await OnUiAsync(() => OperationDialog.ConfirmAsync(this, plan, _controller.State.Scope));
        if (!confirmed)
        {
            return;
        }
        await RunBusyAsync(ct => _controller.ExecuteConfirmedAsync(plan, ct));
        var state = _controller.State;
        if (state.LastOperation is { Status: not OperationStatus.Succeeded } result && state.Status != WorkspaceStatus.Ready)
        {
            await OnUiAsync(async () =>
            {
                await OperationDialog.ShowFailureAsync(this, result);
                return true;
            });
        }
        else if (state.Status == WorkspaceStatus.Failed && state.Error is { } error)
        {
            await OnUiAsync(async () =>
            {
                await MessageBox.NotifyAsync(state.StatusMessage ?? "操作失败", PromptIconKind.Error, error, this);
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
                    await MessageBox.NotifyAsync("操作未完成", PromptIconKind.Error, ex.Message, this);
                    return true;
                });
            }
            catch (Exception)
            {
                // The window may already be closed; nothing left to report to.
            }
        }
    }

    private static IDispatcher Dispatcher => Application.Current.Dispatcher ?? throw new InvalidOperationException("UI 调度器不可用。");

    private static string StatusLabel(WorkspaceStatus status) => status switch
    {
        WorkspaceStatus.Idle => "就绪",
        WorkspaceStatus.Loading => "正在加载…",
        WorkspaceStatus.Ready => "就绪",
        WorkspaceStatus.Writing => "正在写入…",
        WorkspaceStatus.Cancelled => "已取消",
        WorkspaceStatus.Failed => "失败",
        _ => "需要恢复",
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static TextBlock Muted(string text) => new TextBlock().Text(text).Foreground(OperationDialog.MutedColor).TextWrapping(TextWrapping.Wrap);

    private static TextBlock Wrapped(string text) => new TextBlock().Text(text).TextWrapping(TextWrapping.Wrap);

    private static TextBlock SectionTitle(string text) => new TextBlock().Text(text).Bold().Margin(0, 8, 0, 0);
}
