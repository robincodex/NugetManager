using NugetManager.Domain;
using NugetManager.Operations;
using NugetManager.Packages;
using NugetManager.Presentation;
using NugetManager.Sdk;
using NugetManager.Workspaces;

namespace NugetManager.Tests;

internal sealed class FakeDiscovery(Workspace workspace) : IWorkspaceDiscovery
{
    public Task<IReadOnlyList<string>> FindSolutionsAsync(string directory, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<Workspace> LoadAsync(string directory, string? solutionPath, CancellationToken ct) => Task.FromResult(workspace);
}

internal sealed class FakeSdk : ISdkClient
{
    public PackageInventory Inventory { get; set; } = new([]);
    public Exception? InventoryError { get; set; }
    public Dictionary<string, List<EvaluatedReference>> References { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string?>? Environment => null;

    public Task<string> VerifyCapabilitiesAsync(string directory, CancellationToken ct) => Task.FromResult("10.0.100");

    public Task<ProjectEvaluation> EvaluateAsync(ProjectDescriptor project, CancellationToken ct) =>
        Task.FromResult(new ProjectEvaluation(project, ["net10.0"],
            References.TryGetValue(project.Path, out var refs) ? refs : [], []));

    public Task<PackageInventory> ReadInventoryAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation>? evaluations, CancellationToken ct) =>
        InventoryError is { } error ? Task.FromException<PackageInventory>(error) : Task.FromResult(Inventory);

    public Task RestoreAsync(IReadOnlyList<ProjectDescriptor> projects, bool lockedMode, CancellationToken ct) => Task.CompletedTask;

    public Task RestorePreservingLockFilesAsync(IReadOnlyList<ProjectEvaluation> evaluations, CancellationToken ct) => Task.CompletedTask;

    public Task<PackageListOutput> ListOutdatedAsync(ProjectDescriptor project, bool includePrerelease, CancellationToken ct) => throw new NotSupportedException();

    public Task<SearchOutput> SearchAsync(string workingDirectory, string query, int skip, int take, bool includePrerelease, bool exactMatch, string? source, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task AddPackageAsync(string projectPath, string packageId, string version, CancellationToken ct) => Task.CompletedTask;

    public Task RemovePackageAsync(string projectPath, string packageId, CancellationToken ct) => Task.CompletedTask;

    public Task<string?> GetGlobalPackagesFolderAsync(string directory, CancellationToken ct) => Task.FromResult<string?>(null);
}

internal sealed class FakeCatalog : IPackageCatalog
{
    public UpdateReport Report { get; set; } = new([], [], true);
    public Exception? UpdateError { get; set; }
    public Func<string, int, Task<SearchPage>>? Search { get; set; }
    public Func<SourceSelection, bool, Task<BrowsePage>>? Browse { get; set; }
    public Func<string?, Task<PackageVersions>>? Versions { get; set; }
    public Func<string, SourceSelection, Task<SearchPage>>? SourceSearch { get; set; }
    public List<bool> PrereleaseRequests { get; } = [];

    public Task<UpdateReport> FindUpdatesAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation> evaluations, WorkspaceScope scope, bool includePrerelease, CancellationToken ct)
    {
        PrereleaseRequests.Add(includePrerelease);
        return UpdateError is { } error ? Task.FromException<UpdateReport>(error) : Task.FromResult(Report);
    }

    public Task<SearchPage> SearchAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct, SourceSelection? selection = null) =>
        SourceSearch?.Invoke(query, selection ?? SourceSelection.All) ?? Search?.Invoke(query, skip) ?? Task.FromResult(new SearchPage([], skip, take, false, []));

    public Task<PackageVersions> GetVersionsAsync(Workspace workspace, string packageId, bool includePrerelease, CancellationToken ct, string? sourceName = null) =>
        Versions?.Invoke(sourceName) ?? Task.FromResult(new PackageVersions(packageId, ["3.0.0", "2.0.0", "1.0.0"], []));

    public Task<BrowsePage> BrowseAsync(Workspace workspace, SourceSelection selection, bool includePrerelease, CancellationToken ct) =>
        Browse?.Invoke(selection, includePrerelease) ?? Task.FromResult(new BrowsePage([]));
}

internal sealed class FakeMetadata : IPackageMetadataReader
{
    public Func<string, string, Task<PackageDetails>>? Read { get; set; }

    public Task<PackageDetails> ReadAsync(Workspace workspace, string packageId, string version, string? sourceName, CancellationToken ct) =>
        Read?.Invoke(packageId, version) ?? Task.FromResult(new PackageDetails(packageId, version, null, null, null, null, [], [], []));
}

internal sealed class FakeOperations : IPackageOperations
{
    public List<IReadOnlyList<PackageChange>> Planned { get; } = [];
    public Func<OperationPlan, CancellationToken, Task<OperationResult>>? Execute { get; set; }

    public Task<OperationPlan> PlanAsync(Workspace workspace, IReadOnlyList<PackageChange> changes, CancellationToken ct)
    {
        Planned.Add(changes);
        return Task.FromResult(new OperationPlan(changes, [], [], [], [])
        {
            Commands = changes.Select(c => new PlannedCommand(c.Action, c.ProjectPath, c.PackageId, c.Version, "")).ToList(),
        });
    }

    public Task<OperationResult> ExecuteAsync(OperationPlan plan, IProgress<string> progress, CancellationToken ct) =>
        Execute?.Invoke(plan, ct) ?? Task.FromResult(new OperationResult(OperationStatus.Succeeded, null, []));
}

public class WorkspaceControllerTests
{
    [Theory]
    [InlineData("refresh")]
    [InlineData("prerelease")]
    [InlineData("write")]
    public async Task InvalidatedSearchStopsLoadingEvenWhenReplacementOperationDoesNotComplete(string action)
    {
        var h = Create();
        await h.Controller.OpenAsync(Root, null, CancellationToken.None);
        var pending = new TaskCompletionSource<SearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Catalog.Search = (_, _) => pending.Task;
        var search = h.Controller.SearchAsync("pending", 0, CancellationToken.None);
        Assert.True(h.Controller.State.SearchLoading);
        try
        {
            if (action == "refresh")
            {
                h.Sdk.InventoryError = new OperationCanceledException();
                await h.Controller.RefreshAsync(CancellationToken.None);
            }
            else if (action == "prerelease")
            {
                h.Catalog.UpdateError = new OperationCanceledException();
                await h.Controller.SetIncludePrereleaseAsync(true, CancellationToken.None);
            }
            else
            {
                h.Operations.Execute = (_, _) => Task.FromResult(new OperationResult(OperationStatus.RolledBack, "cancelled", []));
                await h.Controller.ExecuteConfirmedAsync(new OperationPlan([], [], [], [], []), CancellationToken.None);
            }
            Assert.False(h.Controller.State.SearchLoading);
        }
        finally
        {
            pending.SetResult(new SearchPage([], 0, 20, false, []));
            await search;
        }
        Assert.False(h.Controller.State.SearchLoading);
        Assert.Null(h.Controller.State.Search);
    }

    private const string Root = @"C:\ws";
    private static readonly string ApiPath = Path.Combine(Root, "Api", "Api.csproj");
    private static readonly string CorePath = Path.Combine(Root, "Core", "Core.csproj");
    private static readonly Workspace TwoProjects = new(Root, null, [new(ApiPath, "Api"), new(CorePath, "Core")]);

    private sealed record Harness(WorkspaceController Controller, FakeSdk Sdk, FakeCatalog Catalog, FakeMetadata Metadata, FakeOperations Operations);

    private static EvaluatedReference Ref(string project, string id) =>
        new(id, "net10.0", project, null, "1.0.0", null, true, null) { ProjectPath = project };

    private static PackageOccurrence Occ(string project, string id, string version, ReferenceKind kind) =>
        new(project, "net10.0", id, kind == ReferenceKind.Direct ? version : null, version, kind, kind == ReferenceKind.Direct ? project : null);

    private static Harness Create()
    {
        var sdk = new FakeSdk
        {
            Inventory = new PackageInventory(
            [
                Occ(ApiPath, "Example.Json", "1.0.0", ReferenceKind.Direct),
                Occ(ApiPath, "Example.Core", "1.0.0", ReferenceKind.Transitive),
                Occ(CorePath, "Example.Json", "1.0.0", ReferenceKind.Direct),
                Occ(CorePath, "Example.Http", "1.0.0", ReferenceKind.Direct),
                Occ(CorePath, "Microsoft.NET.ILLink.Tasks", "10.0.0", ReferenceKind.SdkImplicit),
            ]),
        };
        sdk.References[ApiPath] = [Ref(ApiPath, "Example.Json")];
        sdk.References[CorePath] = [Ref(CorePath, "Example.Json"), Ref(CorePath, "Example.Http")];
        var catalog = new FakeCatalog
        {
            Report = new UpdateReport(
            [
                new UpdateCandidate("Example.Http", [new PackageUpgrade(Ref(CorePath, "Example.Http"), "1.0.0", "2.0.0")]),
                new UpdateCandidate("Example.Json",
                [
                    new PackageUpgrade(Ref(ApiPath, "Example.Json"), "1.0.0", "3.0.0"),
                    new PackageUpgrade(Ref(CorePath, "Example.Json"), "1.0.0", "3.0.0"),
                ]),
            ], [], true),
        };
        var metadata = new FakeMetadata();
        var operations = new FakeOperations();
        return new Harness(new WorkspaceController(new FakeDiscovery(TwoProjects), sdk, catalog, metadata, operations), sdk, catalog, metadata, operations);
    }

    private static async Task<Harness> OpenAsync()
    {
        var h = Create();
        await h.Controller.OpenAsync(Root, null, CancellationToken.None);
        return h;
    }

    [Fact]
    public async Task SingleProjectFiltersRightPane()
    {
        var h = await OpenAsync();
        await h.Controller.SelectScopeAsync(new WorkspaceScope(CorePath), CancellationToken.None);
        await h.Controller.SelectPackageAsync("Example.Json", "1.0.0", null, CancellationToken.None);
        var state = h.Controller.State;

        Assert.Equal(CorePath, Assert.Single(state.VisibleProjects).Path);
        Assert.Equal(CorePath, Assert.Single(state.ProjectStates).Project.Path);
        Assert.All(state.Installed, p => Assert.Equal(CorePath, p.ProjectPath));
        Assert.Empty(state.Implicit);
        Assert.All(state.Upgrades.SelectMany(u => u.Upgrades), u => Assert.Equal(CorePath, u.Reference.ProjectPath));
    }

    [Fact]
    public async Task WholeWorkspaceRestoresAllProjects()
    {
        var h = await OpenAsync();
        await h.Controller.SelectScopeAsync(new WorkspaceScope(CorePath), CancellationToken.None);
        await h.Controller.SelectScopeAsync(WorkspaceScope.All, CancellationToken.None);

        Assert.Equal(2, h.Controller.State.VisibleProjects.Count);
        Assert.Equal(2, h.Controller.State.Upgrades.Single(u => u.PackageId == "Example.Json").Upgrades.Count);
    }

    [Fact]
    public async Task EmptyUpdatesHideHeader()
    {
        var h = Create();
        h.Catalog.Report = new UpdateReport([], [], true);
        await h.Controller.OpenAsync(Root, null, CancellationToken.None);

        Assert.Empty(h.Controller.State.Upgrades);
        Assert.True(h.Controller.State.UpdatesComplete);
        Assert.Equal(WorkspaceStatus.Ready, h.Controller.State.Status);
    }

    [Fact]
    public async Task DirectAndTransitiveUseSeparateGroups()
    {
        var state = (await OpenAsync()).Controller.State;

        Assert.All(state.Installed, p => Assert.Equal(ReferenceKind.Direct, p.Kind));
        Assert.All(state.Implicit, p => Assert.Equal(ReferenceKind.Transitive, p.Kind));
        Assert.Equal("Microsoft.NET.ILLink.Tasks", Assert.Single(state.SdkImplicit).PackageId);
        Assert.Contains(state.Implicit, p => p.PackageId == "Example.Core");
    }

    [Fact]
    public async Task ScopeChangeClearsSelection()
    {
        var h = await OpenAsync();
        h.Controller.SelectAllUpgrades(true);
        Assert.Equal(2, h.Controller.State.SelectedPackageIds.Count);

        await h.Controller.SelectScopeAsync(new WorkspaceScope(ApiPath), CancellationToken.None);

        Assert.Empty(h.Controller.State.SelectedPackageIds);
    }

    [Fact]
    public async Task SelectAllThenExcludeOne()
    {
        var h = await OpenAsync();
        h.Controller.SelectAllUpgrades(true);
        h.Controller.SetUpgradeSelected("Example.Http", false);

        await h.Controller.PreviewSelectedUpgradesAsync(CancellationToken.None);

        var changes = Assert.Single(h.Operations.Planned);
        Assert.All(changes, c => Assert.Equal("Example.Json", c.PackageId));
        Assert.Equal([ApiPath, CorePath], changes.Select(c => c.ProjectPath).Order().ToArray());
        Assert.All(changes, c => Assert.Equal("3.0.0", c.Version));
    }

    [Fact]
    public async Task SelectedUpgradesRespectScope()
    {
        var h = await OpenAsync();
        await h.Controller.SelectScopeAsync(new WorkspaceScope(ApiPath), CancellationToken.None);
        h.Controller.SelectAllUpgrades(true);

        await h.Controller.PreviewSelectedUpgradesAsync(CancellationToken.None);

        Assert.Equal(ApiPath, Assert.Single(Assert.Single(h.Operations.Planned)).ProjectPath);
    }

    [Fact]
    public async Task OldSearchCannotOverwriteNewScope()
    {
        var h = await OpenAsync();
        var slow = new TaskCompletionSource<SearchPage>();
        h.Catalog.Search = (q, skip) => q == "old" ? slow.Task
            : Task.FromResult(new SearchPage([new SearchHit("Example.New", "1.0.0", "local", null)], skip, 20, false, []));

        var oldSearch = h.Controller.SearchAsync("old", 0, CancellationToken.None);
        await h.Controller.SearchAsync("new", 0, CancellationToken.None);
        await h.Controller.SelectScopeAsync(new WorkspaceScope(ApiPath), CancellationToken.None);
        slow.SetResult(new SearchPage([new SearchHit("Example.Old", "1.0.0", "local", null)], 0, 20, false, []));
        await oldSearch;

        Assert.Equal("Example.New", Assert.Single(h.Controller.State.Search!.Items).PackageId);
        Assert.Equal("new", h.Controller.State.SearchQuery);
    }

    [Fact]
    public async Task OldDetailsCannotOverwriteNewVersion()
    {
        var h = await OpenAsync();
        var slow = new TaskCompletionSource<PackageDetails>();
        h.Metadata.Read = (id, version) => version == "1.0.0" ? slow.Task
            : Task.FromResult(new PackageDetails(id, version, null, "new", null, null, [], [], []));

        var oldDetails = h.Controller.SelectPackageAsync("Example.Json", "1.0.0", null, CancellationToken.None);
        await h.Controller.SelectPackageAsync("Example.Json", "3.0.0", null, CancellationToken.None);
        slow.SetResult(new PackageDetails("Example.Json", "1.0.0", null, "old", null, null, [], [], []));
        await oldDetails;

        Assert.Equal("3.0.0", h.Controller.State.Details!.Version);
        Assert.Equal("new", h.Controller.State.Details.Description);
        Assert.Equal("3.0.0", h.Controller.State.SelectedVersion);
    }

    [Fact]
    public async Task WriteBusyBlocksRangeChanges()
    {
        var h = await OpenAsync();
        var gate = new TaskCompletionSource<OperationResult>();
        h.Operations.Execute = (_, _) => gate.Task;
        var plan = await h.Controller.PreviewChangesAsync([new(PackageAction.Uninstall, ApiPath, "Example.Json", null)], CancellationToken.None);

        var running = h.Controller.ExecuteConfirmedAsync(plan, CancellationToken.None);
        Assert.Equal(WorkspaceStatus.Writing, h.Controller.State.Status);
        Assert.True(h.Controller.State.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Controller.SelectScopeAsync(new WorkspaceScope(ApiPath), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Controller.ExecuteConfirmedAsync(plan, CancellationToken.None));

        gate.SetResult(new OperationResult(OperationStatus.Succeeded, null, []));
        await running;
        Assert.False(h.Controller.State.IsBusy);
        Assert.True(h.Controller.State.Scope.IsAll);
    }

    [Fact]
    public async Task PartialDetectionIsNeverAllLatest()
    {
        var h = Create();
        h.Catalog.Report = new UpdateReport([], [new SourceDiagnostic("broken", "SourceError", "源不可用")], false);
        await h.Controller.OpenAsync(Root, null, CancellationToken.None);
        var state = h.Controller.State;

        Assert.False(state.UpdatesComplete);
        Assert.NotEmpty(state.Diagnostics);
    }

    [Fact]
    public async Task UpdateCheckFailureIsVisible()
    {
        var h = Create();
        h.Catalog.UpdateError = new SdkCommandException("package list", 1, "network down");
        await h.Controller.OpenAsync(Root, null, CancellationToken.None);

        Assert.False(h.Controller.State.UpdatesComplete);
        Assert.Contains(h.Controller.State.Diagnostics, d => d.Message.Contains("network down"));
        Assert.NotEmpty(h.Controller.State.Installed);
    }

    [Fact]
    public async Task UninstallThenTransitiveRemains()
    {
        var h = await OpenAsync();
        h.Operations.Execute = (_, _) =>
        {
            h.Sdk.References[ApiPath] = [];
            h.Sdk.Inventory = new PackageInventory(
            [
                Occ(ApiPath, "Example.Json", "1.0.0", ReferenceKind.Transitive),
                Occ(CorePath, "Example.Json", "1.0.0", ReferenceKind.Direct),
            ]);
            return Task.FromResult(new OperationResult(OperationStatus.Succeeded, null, []));
        };
        await h.Controller.SelectScopeAsync(new WorkspaceScope(ApiPath), CancellationToken.None);
        await h.Controller.SelectPackageAsync("Example.Json", "1.0.0", null, CancellationToken.None);
        Assert.Equal(ProjectAction.Uninstall, Assert.Single(h.Controller.State.ProjectStates).Action);

        var plan = await h.Controller.PreviewChangesAsync([new(PackageAction.Uninstall, ApiPath, "Example.Json", null)], CancellationToken.None);
        await h.Controller.ExecuteConfirmedAsync(plan, CancellationToken.None);
        var state = h.Controller.State;

        Assert.DoesNotContain(state.Installed, p => p.PackageId == "Example.Json");
        Assert.Contains(state.Implicit, p => p.PackageId == "Example.Json");
        Assert.Equal(ProjectAction.Install, Assert.Single(state.ProjectStates).Action);
    }

    [Fact]
    public async Task SuccessfulWriteFailedRefreshKeepsSuccessfulStatus()
    {
        var h = await OpenAsync();
        h.Operations.Execute = (_, _) =>
        {
            h.Catalog.UpdateError = new SdkCommandException("package list", 1, "source offline");
            return Task.FromResult(new OperationResult(OperationStatus.Succeeded, null, []));
        };
        var plan = await h.Controller.PreviewChangesAsync([new(PackageAction.Uninstall, ApiPath, "Example.Json", null)], CancellationToken.None);

        await h.Controller.ExecuteConfirmedAsync(plan, CancellationToken.None);
        var state = h.Controller.State;

        Assert.Equal(OperationStatus.Succeeded, state.LastOperation!.Status);
        Assert.NotEqual(WorkspaceStatus.Failed, state.Status);
        Assert.Contains("操作成功", state.StatusMessage);
        Assert.False(state.UpdatesComplete);
    }

    [Fact]
    public async Task FailedWriteReportsRollbackAndRecovery()
    {
        var h = await OpenAsync();
        h.Operations.Execute = (_, _) => Task.FromResult(new OperationResult(OperationStatus.RecoveryRequired, "boom",
            [new FileRecovery(ApiPath, RecoveryStatus.Conflict, "external edit")]));
        var plan = await h.Controller.PreviewChangesAsync([new(PackageAction.Uninstall, ApiPath, "Example.Json", null)], CancellationToken.None);

        await h.Controller.ExecuteConfirmedAsync(plan, CancellationToken.None);

        Assert.Equal(WorkspaceStatus.RecoveryRequired, h.Controller.State.Status);
        Assert.Contains("boom", h.Controller.State.Error);
    }

    [Fact]
    public async Task PrereleaseDefaultsOffAndToggleRechecks()
    {
        var h = await OpenAsync();
        Assert.False(h.Controller.State.IncludePrerelease);

        await h.Controller.SetIncludePrereleaseAsync(true, CancellationToken.None);

        Assert.True(h.Controller.State.IncludePrerelease);
        Assert.Equal([false, true], h.Catalog.PrereleaseRequests);
    }

    [Fact]
    public async Task NotInstalledProjectOffersInstall()
    {
        var h = await OpenAsync();
        await h.Controller.SelectPackageAsync("Example.Http", "1.0.0", null, CancellationToken.None);
        var rows = h.Controller.State.ProjectStates;

        Assert.Equal(ProjectAction.Install, rows.Single(r => r.Project.Path == ApiPath).Action);
        Assert.Equal(ProjectAction.Uninstall, rows.Single(r => r.Project.Path == CorePath).Action);
    }
}
