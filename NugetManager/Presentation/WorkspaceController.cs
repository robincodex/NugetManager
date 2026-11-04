using NugetManager.Domain;
using NugetManager.Operations;
using NugetManager.Packages;
using NugetManager.Sdk;
using NugetManager.Workspaces;

namespace NugetManager.Presentation;

internal enum WorkspaceStatus
{
    Idle,
    Loading,
    Ready,
    Writing,
    Cancelled,
    Failed,
    RecoveryRequired,
}

internal enum ProjectAction
{
    None,
    Install,
    Uninstall,
}

/// <summary>Right-pane row: one project's relation to the selected package, kept per framework.</summary>
internal sealed record ProjectPackageState(
    ProjectDescriptor Project,
    IReadOnlyList<PackageOccurrence> Occurrences,
    ProjectAction Action,
    string? BlockReason)
{
    public bool IsDirect => Occurrences.Any(o => o.Kind == ReferenceKind.Direct);
}

internal sealed record WorkspaceViewState
{
    public Workspace? Workspace { get; init; }
    public IReadOnlyList<string> SolutionChoices { get; init; } = [];
    public WorkspaceScope Scope { get; init; } = WorkspaceScope.All;
    public IReadOnlyList<ProjectEvaluation> Evaluations { get; init; } = [];
    public IReadOnlyList<UpdateCandidate> Upgrades { get; init; } = [];
    public bool UpdatesComplete { get; init; }
    public IReadOnlyList<PackageOccurrence> Installed { get; init; } = [];
    public IReadOnlyList<PackageOccurrence> Implicit { get; init; } = [];
    public IReadOnlyList<PackageOccurrence> SdkImplicit { get; init; } = [];
    public string? SelectedPackageId { get; init; }
    public string? SelectedPackageSourceName { get; init; }
    public string? SelectedVersion { get; init; }
    public PackageVersions? Versions { get; init; }
    public PackageDetails? Details { get; init; }
    public bool DetailsLoading { get; init; }
    public string? DetailsError { get; init; }
    public IReadOnlyList<ProjectPackageState> ProjectStates { get; init; } = [];
    public string SearchQuery { get; init; } = "";
    public SearchPage? Search { get; init; }
    public IReadOnlyList<NuGetSource> Sources { get; init; } = [];
    public string? SelectedSourceName { get; init; }
    public BrowsePage? Browse { get; init; }
    public bool SearchLoading { get; init; }
    public string? SearchError { get; init; }
    public IReadOnlyList<ProjectDescriptor> VisibleProjects { get; init; } = [];
    public IReadOnlySet<string> SelectedPackageIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool IncludePrerelease { get; init; }
    public bool IsBusy { get; init; }
    public WorkspaceStatus Status { get; init; } = WorkspaceStatus.Idle;
    public string? StatusMessage { get; init; }
    public string? Error { get; init; }
    public string? Progress { get; init; }
    public IReadOnlyList<SourceDiagnostic> Diagnostics { get; init; } = [];
    public OperationResult? LastOperation { get; init; }
}

/// <summary>
/// Coordinates workspace loading, scope filtering, selection, search, details and writes.
/// Every asynchronous read carries a generation number so that a stale response never overwrites newer state.
/// </summary>
internal sealed class WorkspaceController(
    IWorkspaceDiscovery discovery,
    ISdkClient sdk,
    IPackageCatalog catalog,
    IPackageMetadataReader metadata,
    IPackageOperations operations)
{
    private const int SearchPageSize = 20;
    private const int EvaluationParallelism = 4;

    private readonly object _gate = new();
    private WorkspaceViewState _state = new();
    private PackageInventory _inventory = new([]);
    private UpdateReport _report = new([], [], false);
    private IReadOnlyList<SourceDiagnostic> _loadDiagnostics = [];
    private long _loadGeneration;
    private long _scopeGeneration;
    private long _searchGeneration;
    private long _detailsGeneration;
    private long _versionsGeneration;
    private bool _writing;
    private bool _browseActivated;
    private readonly Dictionary<(string Directory, string? Source, bool Prerelease), BrowsePage> _browseCache = [];

    public event Action<WorkspaceViewState>? StateChanged;

    public WorkspaceViewState State
    {
        get { lock (_gate) return _state; }
    }

    public async Task OpenAsync(string directory, string? solutionPath, CancellationToken ct)
    {
        EnsureNotWriting();
        var generation = Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _scopeGeneration);
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _detailsGeneration);
        Interlocked.Increment(ref _versionsGeneration);
        lock (_gate)
        {
            _browseCache.Clear();
            _browseActivated = false;
        }
        Publish(_ => new WorkspaceViewState
        {
            IncludePrerelease = State.IncludePrerelease,
            Status = WorkspaceStatus.Loading,
            StatusMessage = "正在加载工作区…",
            IsBusy = true,
        });
        try
        {
            await sdk.VerifyCapabilitiesAsync(directory, ct).ConfigureAwait(false);
            var workspace = await discovery.LoadAsync(directory, solutionPath, ct).ConfigureAwait(false);
            await LoadAsync(workspace, generation, ct).ConfigureAwait(false);
        }
        catch (SolutionSelectionRequiredException ex)
        {
            PublishIf(generation, s => s with
            {
                Status = WorkspaceStatus.Idle,
                IsBusy = false,
                SolutionChoices = ex.Solutions,
                StatusMessage = "目录中有多个解决方案，请选择一个。",
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishIf(generation, s => s with { Status = WorkspaceStatus.Failed, IsBusy = false, Error = ex.Message, StatusMessage = "加载失败" });
        }
        catch (OperationCanceledException)
        {
            PublishIf(generation, s => s with { Status = WorkspaceStatus.Cancelled, IsBusy = false, StatusMessage = "已取消加载" });
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureNotWriting();
        var workspace = State.Workspace ?? throw new InvalidOperationException("尚未打开工作区。");
        var generation = Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _searchGeneration);
        lock (_gate) _browseCache.Clear();
        Publish(s => s with { Status = WorkspaceStatus.Loading, IsBusy = true, StatusMessage = "正在刷新…", Error = null });
        try
        {
            await LoadAsync(workspace, generation, ct).ConfigureAwait(false);
            if (generation == Interlocked.Read(ref _loadGeneration) && (_browseActivated || !string.IsNullOrWhiteSpace(State.SearchQuery)))
            {
                await SearchAsync(State.SearchQuery, 0, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            PublishIf(generation, s => s with { Status = WorkspaceStatus.Cancelled, IsBusy = false, StatusMessage = "已取消刷新" });
        }
        catch (Exception ex)
        {
            PublishIf(generation, s => s with { Status = WorkspaceStatus.Failed, IsBusy = false, Error = ex.Message, StatusMessage = "刷新失败" });
        }
    }

    public async Task SetIncludePrereleaseAsync(bool include, CancellationToken ct)
    {
        EnsureNotWriting();
        Interlocked.Increment(ref _searchGeneration);
        Publish(s => s with { IncludePrerelease = include });
        if (State.Workspace is null)
        {
            return;
        }
        var generation = Interlocked.Increment(ref _loadGeneration);
        Publish(s => s with { IsBusy = true, StatusMessage = "正在检测更新…" });
        try
        {
            await CheckUpdatesAsync(State.Workspace!, State.Evaluations, include, generation, ct).ConfigureAwait(false);
            PublishIf(generation, s => Compose(s with { IsBusy = false, Status = WorkspaceStatus.Ready, StatusMessage = ReadyMessage() }));
            if (generation == Interlocked.Read(ref _loadGeneration) && (_browseActivated || !string.IsNullOrWhiteSpace(State.SearchQuery)))
            {
                await SearchAsync(State.SearchQuery, 0, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            PublishIf(generation, s => s with { Status = WorkspaceStatus.Cancelled, IsBusy = false, StatusMessage = "已取消更新检测" });
        }
    }

    public Task SelectScopeAsync(WorkspaceScope scope, CancellationToken ct)
    {
        EnsureNotWriting();
        Interlocked.Increment(ref _scopeGeneration);
        Interlocked.Increment(ref _searchGeneration);
        Publish(s => Compose(s with
        {
            Scope = scope,
            SelectedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SearchLoading = false,
        }));
        return Task.CompletedTask;
    }

    public void SelectAllUpgrades(bool selected)
    {
        if (_writing)
        {
            return;
        }
        Publish(s => s with
        {
            SelectedPackageIds = selected
                ? s.Upgrades.Select(u => u.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        });
    }

    public void SetUpgradeSelected(string packageId, bool selected)
    {
        if (_writing)
        {
            return;
        }
        Publish(s =>
        {
            if (selected && !s.Upgrades.Any(u => string.Equals(u.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                return s;
            }
            var ids = s.SelectedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected)
            {
                ids.Add(packageId);
            }
            else
            {
                ids.Remove(packageId);
            }
            return s with { SelectedPackageIds = ids };
        });
    }

    public async Task SelectPackageAsync(string packageId, string version, string? sourceName, CancellationToken ct)
    {
        var workspace = State.Workspace ?? throw new InvalidOperationException("尚未打开工作区。");
        var generation = Interlocked.Increment(ref _detailsGeneration);
        var packageChanged = !string.Equals(State.SelectedPackageId, packageId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(State.SelectedPackageSourceName, sourceName, StringComparison.OrdinalIgnoreCase);
        Publish(s => Compose(s with
        {
            SelectedPackageId = packageId,
            SelectedPackageSourceName = sourceName,
            SelectedVersion = version,
            Details = null,
            DetailsLoading = true,
            DetailsError = null,
            Versions = packageChanged ? null : s.Versions,
        }));

        if (packageChanged)
        {
            _ = LoadVersionsAsync(workspace, packageId, sourceName, ct);
        }

        try
        {
            var details = await metadata.ReadAsync(workspace, packageId, version, sourceName, ct).ConfigureAwait(false);
            PublishIf(() => generation == Interlocked.Read(ref _detailsGeneration),
                s => s with { Details = details, DetailsLoading = false });
        }
        catch (OperationCanceledException)
        {
            PublishIf(() => generation == Interlocked.Read(ref _detailsGeneration), s => s with { DetailsLoading = false });
        }
        catch (Exception ex)
        {
            PublishIf(() => generation == Interlocked.Read(ref _detailsGeneration),
                s => s with { DetailsLoading = false, DetailsError = ex.Message });
        }
    }

    public async Task SearchAsync(string query, int skip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Publish(s => s with { SearchQuery = "", Search = null });
            await LoadBrowseAsync(false, ct).ConfigureAwait(false);
            return;
        }
        var workspace = State.Workspace ?? throw new InvalidOperationException("尚未打开工作区。");
        var generation = Interlocked.Increment(ref _searchGeneration);
        var state = State;
        Publish(s => s with { SearchQuery = query, Browse = null, SearchLoading = true, SearchError = null, Search = skip == 0 ? null : s.Search });
        try
        {
            var page = await catalog.SearchAsync(workspace, query, skip, SearchPageSize, state.IncludePrerelease, ct, state.SelectedSourceName).ConfigureAwait(false);
            PublishIf(() => generation == Interlocked.Read(ref _searchGeneration), s => s with
            {
                SearchLoading = false,
                Search = skip > 0 && s.Search is { } previous
                    ? page with { Items = [.. previous.Items, .. page.Items], Skip = 0 }
                    : page,
            });
        }
        catch (OperationCanceledException)
        {
            PublishIf(() => generation == Interlocked.Read(ref _searchGeneration), s => s with { SearchLoading = false });
        }
        catch (Exception ex)
        {
            PublishIf(() => generation == Interlocked.Read(ref _searchGeneration),
                s => s with { SearchLoading = false, SearchError = ex.Message });
        }
    }

    public async Task SelectSourceAsync(string? sourceName, CancellationToken ct, string? query = null)
    {
        EnsureNotWriting();
        if (sourceName is not null && !State.Sources.Any(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"包源不存在或已禁用：{sourceName}");
        }
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _detailsGeneration);
        Interlocked.Increment(ref _versionsGeneration);
        Publish(s => Compose(s with { SelectedSourceName = sourceName, SearchQuery = query ?? s.SearchQuery, Search = null, Browse = null, SearchError = null,
            SelectedPackageId = null, SelectedPackageSourceName = null, Versions = null, Details = null, DetailsLoading = false }));
        await SearchAsync(State.SearchQuery, 0, ct).ConfigureAwait(false);
    }

    public async Task LoadBrowseAsync(bool refresh, CancellationToken ct)
    {
        var state = State;
        var workspace = state.Workspace ?? throw new InvalidOperationException("尚未打开工作区。");
        var generation = Interlocked.Increment(ref _searchGeneration);
        var key = (workspace.Directory, state.SelectedSourceName, state.IncludePrerelease);
        BrowsePage? cached;
        lock (_gate)
        {
            _browseActivated = true;
            if (refresh) _browseCache.Remove(key);
            _browseCache.TryGetValue(key, out cached);
        }
        Publish(s => s with { SearchQuery = "", Search = null, Browse = cached ?? (refresh ? s.Browse : null),
            SearchLoading = cached is null, SearchError = null });
        if (cached is not null) return;
        try
        {
            var page = await catalog.BrowseAsync(workspace, state.SelectedSourceName, state.IncludePrerelease, ct).ConfigureAwait(false);
            PublishIf(() => generation == Interlocked.Read(ref _searchGeneration), s =>
            {
                // Failed source groups must be retryable rather than cached as a permanent result.
                if (page.Sources.All(g => g.Page.Diagnostics.Count == 0)) _browseCache[key] = page;
                return s with { Browse = page, SearchLoading = false };
            });
        }
        catch (OperationCanceledException)
        {
            PublishIf(() => generation == Interlocked.Read(ref _searchGeneration), s => s with { SearchLoading = false });
        }
        catch (Exception ex)
        {
            PublishIf(() => generation == Interlocked.Read(ref _searchGeneration), s => s with { SearchLoading = false, SearchError = ex.Message });
        }
    }

    public Task<OperationPlan> PreviewChangesAsync(IReadOnlyList<PackageChange> changes, CancellationToken ct)
    {
        EnsureNotWriting();
        var workspace = State.Workspace ?? throw new InvalidOperationException("尚未打开工作区。");
        return operations.PlanAsync(workspace, changes, ct);
    }

    public Task<OperationPlan> PreviewSelectedUpgradesAsync(CancellationToken ct)
    {
        var state = State;
        var changes = state.Upgrades
            .Where(c => state.SelectedPackageIds.Contains(c.PackageId))
            .SelectMany(c => c.Upgrades.Select(u => (c.PackageId, u.Reference.ProjectPath, u.ToVersion)))
            .GroupBy(x => (x.PackageId, x.ProjectPath), TupleComparer.Instance)
            .Select(g => new PackageChange(PackageAction.Upgrade, g.Key.ProjectPath, g.Key.PackageId,
                g.Select(x => x.ToVersion).OrderByDescending(v => v, NuGetVersionComparer.Instance).First()))
            .ToList();
        if (changes.Count == 0)
        {
            throw new InvalidOperationException("没有勾选可升级的包。");
        }
        return PreviewChangesAsync(changes, ct);
    }

    public async Task ExecuteConfirmedAsync(OperationPlan plan, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_writing)
            {
                throw new InvalidOperationException("已有写入操作正在进行。");
            }
            _writing = true;
        }
        var generation = Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _searchGeneration);
        Publish(s => s with
        {
            Status = WorkspaceStatus.Writing,
            IsBusy = true,
            Error = null,
            StatusMessage = "正在写入…",
            Progress = null,
        });
        try
        {
            OperationResult result;
            try
            {
                var progress = new Progress<string>(p => Publish(s => s with { Progress = p }));
                result = await operations.ExecuteAsync(plan, progress, ct).ConfigureAwait(false);
            }
            catch (PlanStaleException ex)
            {
                Publish(s => s with { Status = WorkspaceStatus.Failed, IsBusy = false, Error = ex.Message, StatusMessage = "预览后文件已变化，未写入任何内容，请重新预览。" });
                return;
            }
            catch (Exception ex)
            {
                Publish(s => s with { Status = WorkspaceStatus.Failed, IsBusy = false, Error = ex.Message, StatusMessage = "操作未执行" });
                return;
            }

            switch (result.Status)
            {
                case OperationStatus.RecoveryRequired:
                    Publish(s => s with
                    {
                        LastOperation = result,
                        Status = WorkspaceStatus.RecoveryRequired,
                        IsBusy = false,
                        Error = result.Error,
                        StatusMessage = "操作失败且未能完全回滚，需要恢复。",
                    });
                    return;
                case OperationStatus.RolledBack:
                    Publish(s => s with
                    {
                        LastOperation = result,
                        Status = WorkspaceStatus.Failed,
                        IsBusy = false,
                        Error = result.Error,
                        StatusMessage = "操作失败，所有文件已回滚。",
                    });
                    return;
            }

            Publish(s => s with { LastOperation = result, SelectedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) });
            try
            {
                var workspace = await discovery.LoadAsync(State.Workspace!.Directory, State.Workspace.SolutionPath, CancellationToken.None).ConfigureAwait(false);
                await LoadAsync(workspace, generation, CancellationToken.None).ConfigureAwait(false);
                PublishIf(generation, s => s with
                {
                    StatusMessage = s.UpdatesComplete ? "操作成功" : "操作成功，更新检测未完成",
                });
            }
            catch (Exception ex)
            {
                PublishIf(generation, s => s with
                {
                    Status = WorkspaceStatus.Ready,
                    IsBusy = false,
                    UpdatesComplete = false,
                    StatusMessage = "操作成功，刷新依赖列表未完成",
                    Error = ex.Message,
                });
            }
        }
        finally
        {
            lock (_gate)
            {
                _writing = false;
            }
            Publish(s => s with { IsBusy = s.Status is WorkspaceStatus.Loading });
        }
    }

    private async Task LoadAsync(Workspace workspace, long generation, CancellationToken ct)
    {
        var evaluations = await EvaluateAllAsync(workspace, ct).ConfigureAwait(false);
        var sources = NuGetConfigReader.Load(workspace.Directory).Sources.Where(s => s.IsEnabled).ToList();
        var inventory = await sdk.ReadInventoryAsync(workspace, evaluations, ct).ConfigureAwait(false);
        if (generation != Interlocked.Read(ref _loadGeneration))
        {
            return;
        }
        lock (_gate)
        {
            _inventory = inventory;
            _loadDiagnostics = inventory.Problems.Select(p => new SourceDiagnostic(p.ProjectPath, "ProjectProblem", p.Message)).ToList();
        }
        PublishIf(generation, s =>
        {
            var scope = s.Scope.ProjectPath is { } path && !workspace.Projects.Any(p => PathUtil.AreEqual(p.Path, path))
                ? WorkspaceScope.All
                : s.Scope;
            var selectedSource = sources.Any(source => string.Equals(source.Name, s.SelectedSourceName, StringComparison.OrdinalIgnoreCase))
                ? s.SelectedSourceName : null;
            return s with { Workspace = workspace, Evaluations = evaluations, Scope = scope, SolutionChoices = [],
                Sources = sources, SelectedSourceName = selectedSource };
        });
        await CheckUpdatesAsync(workspace, evaluations, State.IncludePrerelease, generation, ct).ConfigureAwait(false);
        PublishIf(generation, s => Compose(s with
        {
            Status = WorkspaceStatus.Ready,
            IsBusy = false,
            Error = null,
            StatusMessage = ReadyMessage(),
        }));
    }

    private async Task CheckUpdatesAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation> evaluations, bool includePrerelease, long generation, CancellationToken ct)
    {
        UpdateReport report;
        try
        {
            report = await catalog.FindUpdatesAsync(workspace, evaluations, WorkspaceScope.All, includePrerelease, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report = new UpdateReport([], [new SourceDiagnostic("", "UpdateCheckFailed", ex.Message)], false);
        }
        if (generation != Interlocked.Read(ref _loadGeneration))
        {
            return;
        }
        lock (_gate)
        {
            _report = report;
        }
    }

    private async Task<IReadOnlyList<ProjectEvaluation>> EvaluateAllAsync(Workspace workspace, CancellationToken ct)
    {
        var results = new ProjectEvaluation[workspace.Projects.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = EvaluationParallelism, CancellationToken = ct },
            async (i, token) => results[i] = await sdk.EvaluateAsync(workspace.Projects[i], token).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    private async Task LoadVersionsAsync(Workspace workspace, string packageId, string? sourceName, CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _versionsGeneration);
        try
        {
            var versions = await catalog.GetVersionsAsync(workspace, packageId, State.IncludePrerelease, ct, sourceName).ConfigureAwait(false);
            PublishIf(() => generation == Interlocked.Read(ref _versionsGeneration)
                    && string.Equals(State.SelectedPackageId, packageId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(State.SelectedPackageSourceName, sourceName, StringComparison.OrdinalIgnoreCase),
                s => s with { Versions = versions });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishIf(() => generation == Interlocked.Read(ref _versionsGeneration),
                s => s with { Versions = new PackageVersions(packageId, [], [new SourceDiagnostic("", "VersionsFailed", ex.Message)]) });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string ReadyMessage()
    {
        lock (_gate)
        {
            return _report.IsComplete ? "就绪" : "更新检测未完成";
        }
    }

    /// <summary>Derives every scope-dependent list from the raw inventory and update report.</summary>
    private WorkspaceViewState Compose(WorkspaceViewState s)
    {
        PackageInventory inventory;
        UpdateReport report;
        IReadOnlyList<SourceDiagnostic> loadDiagnostics;
        lock (_gate)
        {
            inventory = _inventory;
            report = _report;
            loadDiagnostics = _loadDiagnostics;
        }
        var scope = s.Scope;
        var visible = s.Workspace?.Projects.Where(p => scope.Includes(p.Path)).ToList() ?? [];
        var scoped = inventory.Occurrences.Where(o => scope.Includes(o.ProjectPath)).ToList();
        var upgrades = report.Candidates
            .Select(c => c with { Upgrades = c.Upgrades.Where(u => scope.Includes(u.Reference.ProjectPath)).ToList() })
            .Where(c => c.Upgrades.Count > 0)
            .OrderBy(c => c.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var upgradeIds = upgrades.Select(u => u.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return s with
        {
            VisibleProjects = visible,
            Upgrades = upgrades,
            UpdatesComplete = report.IsComplete,
            Diagnostics = [.. loadDiagnostics, .. report.Diagnostics],
            Installed = Sorted(scoped.Where(o => o.Kind == ReferenceKind.Direct)),
            Implicit = Sorted(scoped.Where(o => o.Kind == ReferenceKind.Transitive)),
            SdkImplicit = Sorted(scoped.Where(o => o.Kind == ReferenceKind.SdkImplicit)),
            SelectedPackageIds = s.SelectedPackageIds.Where(upgradeIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ProjectStates = s.SelectedPackageId is { } id ? ProjectStatesFor(id, visible, inventory, s.Evaluations) : [],
        };
    }

    private static List<PackageOccurrence> Sorted(IEnumerable<PackageOccurrence> occurrences) =>
        occurrences.OrderBy(o => o.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Framework, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<ProjectPackageState> ProjectStatesFor(string packageId, IReadOnlyList<ProjectDescriptor> projects,
        PackageInventory inventory, IReadOnlyList<ProjectEvaluation> evaluations)
    {
        var rows = new List<ProjectPackageState>(projects.Count);
        foreach (var project in projects)
        {
            var occurrences = inventory.Occurrences
                .Where(o => PathUtil.AreEqual(o.ProjectPath, project.Path) && string.Equals(o.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.Framework, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var evaluation = evaluations.FirstOrDefault(e => PathUtil.AreEqual(e.Project.Path, project.Path));
            var reference = evaluation?.References.FirstOrDefault(r => string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

            if (occurrences.Any(o => o.Kind == ReferenceKind.Direct) || reference is not null)
            {
                var blocked = reference is { IsWritable: false } ? reference.WriteBlockReason ?? "该引用无法安全修改。" : null;
                rows.Add(new ProjectPackageState(project, occurrences, ProjectAction.Uninstall, blocked));
            }
            else if (occurrences.Count > 0 && occurrences.All(o => o.Kind == ReferenceKind.SdkImplicit))
            {
                rows.Add(new ProjectPackageState(project, occurrences, ProjectAction.None, "SDK 自动引用，不可修改。"));
            }
            else
            {
                rows.Add(new ProjectPackageState(project, occurrences, ProjectAction.Install, null));
            }
        }
        return rows;
    }

    private void EnsureNotWriting()
    {
        if (_writing)
        {
            throw new InvalidOperationException("写入操作进行中，暂不能更改范围或刷新。");
        }
    }

    private void Publish(Func<WorkspaceViewState, WorkspaceViewState> update) => PublishIf(() => true, update);

    private void PublishIf(long loadGeneration, Func<WorkspaceViewState, WorkspaceViewState> update) =>
        PublishIf(() => loadGeneration == Interlocked.Read(ref _loadGeneration), update);

    private void PublishIf(Func<bool> isCurrent, Func<WorkspaceViewState, WorkspaceViewState> update)
    {
        WorkspaceViewState next;
        lock (_gate)
        {
            if (!isCurrent())
            {
                return;
            }
            next = update(_state);
            if (ReferenceEquals(next, _state))
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    private sealed class TupleComparer : IEqualityComparer<(string PackageId, string ProjectPath)>
    {
        public static TupleComparer Instance { get; } = new();

        public bool Equals((string PackageId, string ProjectPath) x, (string PackageId, string ProjectPath) y) =>
            string.Equals(x.PackageId, y.PackageId, StringComparison.OrdinalIgnoreCase) && PathUtil.AreEqual(x.ProjectPath, y.ProjectPath);

        public int GetHashCode((string PackageId, string ProjectPath) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PackageId), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProjectPath));
    }
}
