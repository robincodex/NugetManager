using NugetManager.Domain;
using NugetManager.Operations;
using NugetManager.Packages;
using NugetManager.Presentation;
using NugetManager.Workspaces;

namespace NugetManager.Tests;

/// <summary>End-to-end workflows driven through the real controller, SDK and file transaction against an isolated feed.</summary>
public sealed class PackageWorkflowTests
{
    private static readonly HttpClient Http = new();

    private static string RecoveryDirectory(TestWorkspace ws) => Path.Combine(ws.Root, ".recovery");

    private static WorkspaceController Controller(TestWorkspace ws)
    {
        var sdk = ws.CreateSdk();
        return new WorkspaceController(new WorkspaceDiscovery(), sdk, new PackageCatalog(sdk),
            new PackageMetadataReader(Http, ws.PackagesDirectory, ws.Path_("NuGet.Config")),
            new PackageOperations(sdk, RecoveryDirectory(ws)));
    }

    private static async Task<TestWorkspace> FeedAsync()
    {
        var ws = await TestWorkspace.CreateAsync();
        foreach (var v in new[] { "1.0.0", "3.0.0", "4.0.0-preview.1" })
        {
            ws.AddPackage("Example.Json", v);
            ws.AddPackage("Example.Core", v);
        }
        return ws;
    }

    private static IEnumerable<PackageOccurrence> Direct(WorkspaceViewState state, string id) =>
        state.Installed.Where(p => p.PackageId == id);

    [Fact]
    public async Task WorkspaceToStableBatchToRefresh()
    {
        await using var ws = await FeedAsync();
        var api = ws.WriteProject("Api/Api.csproj", "net10.0", ("Example.Json", "1.0.0"), ("Example.Core", "1.0.0"));
        var core = ws.WriteProject("Core/Core.csproj", "net8.0;net10.0", ("Example.Json", "1.0.0"));
        ws.Write("Demo.slnx", """
            <Solution>
              <Project Path="Api/Api.csproj" />
              <Project Path="Core/Core.csproj" />
            </Solution>
            """);
        await ws.RestoreAsync(ws.Root);
        var controller = Controller(ws);

        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        var opened = controller.State;
        Assert.Equal(WorkspaceStatus.Ready, opened.Status);
        Assert.True(opened.UpdatesComplete);
        Assert.Equal(2, opened.Workspace!.Projects.Count);
        Assert.Equal(["Example.Core", "Example.Json"], opened.Upgrades.Select(u => u.PackageId).Order());
        Assert.All(opened.Upgrades, u => Assert.Equal("3.0.0", u.ToVersion));

        const string selectedId = "Example.Json";
        const string excludedId = "Example.Core";
        controller.SelectAllUpgrades(true);
        controller.SetUpgradeSelected(excludedId, false);
        Assert.Equal([selectedId], controller.State.SelectedPackageIds);
        var plan = await controller.PreviewSelectedUpgradesAsync(CancellationToken.None);
        Assert.True(plan.CanExecute);
        Assert.Equal(2, plan.Changes.Count);
        Assert.All(plan.Changes, c => Assert.Equal(selectedId, c.PackageId));

        await controller.ExecuteConfirmedAsync(plan, CancellationToken.None);

        var refreshed = controller.State;
        Assert.Equal(WorkspaceStatus.Ready, refreshed.Status);
        Assert.Equal(OperationStatus.Succeeded, refreshed.LastOperation!.Status);
        Assert.Equal(3, Direct(refreshed, selectedId).Count());
        Assert.All(Direct(refreshed, selectedId), p => Assert.Equal("3.0.0", p.ResolvedVersion));
        Assert.Equal("1.0.0", Direct(refreshed, excludedId).Single().ResolvedVersion);
        Assert.Equal([excludedId], refreshed.Upgrades.Select(u => u.PackageId));
        Assert.Empty(refreshed.SelectedPackageIds);
        Assert.Empty(await FileTransaction.FindIncompleteAsync(RecoveryDirectory(ws), CancellationToken.None));
        Assert.Contains("Version=\"1.0.0\"", (await File.ReadAllTextAsync(api)).Split("Example.Core")[1]);
        Assert.Contains("Include=\"Example.Json\" Version=\"3.0.0\"", await File.ReadAllTextAsync(core));
    }

    [Fact]
    public async Task SearchInstallUninstallRefresh()
    {
        await using var ws = await FeedAsync();
        var app = ws.WriteProject("App/App.csproj", "net8.0;net10.0", ("Example.Json", "1.0.0"));
        var lib = ws.WriteProject("Lib/Lib.csproj", "net10.0");
        await ws.RestoreAsync(app);
        await ws.RestoreAsync(lib);
        var originalApp = await File.ReadAllBytesAsync(app);
        var controller = Controller(ws);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectScopeAsync(new WorkspaceScope(app), CancellationToken.None);

        await controller.SearchAsync("Example", 0, CancellationToken.None);
        var hit = Assert.Single(controller.State.Search!.Items, r => r.PackageId == "Example.Core");
        Assert.Equal("3.0.0", hit.Version);
        await controller.SelectPackageAsync(hit.PackageId, hit.Version, hit.SourceName, CancellationToken.None);
        var appState = Assert.Single(controller.State.ProjectStates);
        Assert.Equal(ProjectAction.Install, appState.Action);

        var install = await controller.PreviewChangesAsync([new(PackageAction.Install, app, hit.PackageId, hit.Version)], CancellationToken.None);
        Assert.True(install.CanExecute);
        await controller.ExecuteConfirmedAsync(install, CancellationToken.None);

        Assert.Equal(WorkspaceStatus.Ready, controller.State.Status);
        Assert.Equal(2, Direct(controller.State, "Example.Core").Count());
        Assert.All(Direct(controller.State, "Example.Core"), p => Assert.Equal("3.0.0", p.ResolvedVersion));
        Assert.Equal(ProjectAction.Uninstall, Assert.Single(controller.State.ProjectStates).Action);

        var uninstall = await controller.PreviewChangesAsync([new(PackageAction.Uninstall, app, hit.PackageId, null)], CancellationToken.None);
        await controller.ExecuteConfirmedAsync(uninstall, CancellationToken.None);

        Assert.Equal(WorkspaceStatus.Ready, controller.State.Status);
        Assert.Empty(Direct(controller.State, "Example.Core"));
        Assert.Equal(ProjectAction.Install, Assert.Single(controller.State.ProjectStates).Action);
        Assert.Equal(originalApp, await File.ReadAllBytesAsync(app));
    }

    [Fact]
    public async Task SharedCentralBatchWithFailureKeepsOriginalFiles()
    {
        await using var ws = await FeedAsync();
        ws.AddPackage("Example.Net10Only", "1.0.0", frameworks: ["net8.0", "net10.0"]);
        ws.AddPackage("Example.Net10Only", "2.0.0", frameworks: ["net10.0"]);
        var centralPropsPath = ws.WriteCentralProps(("Example.Json", "1.0.0"), ("Example.Net10Only", "1.0.0"));
        var api = ws.WriteProject("Api/Api.csproj", "net10.0", ("Example.Json", null));
        var core = ws.WriteProject("Core/Core.csproj", "net8.0;net10.0", ("Example.Json", null), ("Example.Net10Only", null));
        await ws.RestoreAsync(api);
        await ws.RestoreAsync(core);
        var originalPropsBytes = await File.ReadAllBytesAsync(centralPropsPath);
        var originalApi = await File.ReadAllBytesAsync(api);
        var originalCore = await File.ReadAllBytesAsync(core);
        var controller = Controller(ws);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        Assert.Contains(controller.State.Upgrades, u => u.PackageId == "Example.Json");

        var plan = await controller.PreviewChangesAsync(
        [
            new(PackageAction.Upgrade, api, "Example.Json", "3.0.0"),
            new(PackageAction.Upgrade, core, "Example.Json", "3.0.0"),
            new(PackageAction.Upgrade, core, "Example.Net10Only", "2.0.0"),
        ], CancellationToken.None);
        Assert.True(plan.CanExecute);
        Assert.Contains(centralPropsPath, plan.Files, StringComparer.OrdinalIgnoreCase);
        await controller.ExecuteConfirmedAsync(plan, CancellationToken.None);

        Assert.Equal(WorkspaceStatus.Failed, controller.State.Status);
        Assert.Equal(OperationStatus.RolledBack, controller.State.LastOperation!.Status);
        Assert.Contains("NU1202", controller.State.LastOperation.Error);
        Assert.All(controller.State.LastOperation.Recovery, r => Assert.Equal(RecoveryStatus.Restored, r.Status));
        Assert.Equal(originalPropsBytes, await File.ReadAllBytesAsync(centralPropsPath));
        Assert.Equal(originalApi, await File.ReadAllBytesAsync(api));
        Assert.Equal(originalCore, await File.ReadAllBytesAsync(core));
        Assert.Empty(await FileTransaction.FindIncompleteAsync(RecoveryDirectory(ws), CancellationToken.None));

        await controller.RefreshAsync(CancellationToken.None);
        Assert.All(Direct(controller.State, "Example.Json"), p => Assert.Equal("1.0.0", p.ResolvedVersion));
    }

    [Fact]
    public async Task ReopenAfterInterruptedOperationShowsRecovery()
    {
        await using var ws = await FeedAsync();
        var centralPropsPath = ws.WriteCentralProps(("Example.Json", "1.0.0"));
        var api = ws.WriteProject("Api/Api.csproj", "net10.0", ("Example.Json", null));
        var core = ws.WriteProject("Core/Core.csproj", "net8.0;net10.0", ("Example.Json", null));
        await ws.RestoreAsync(api);
        await ws.RestoreAsync(core);
        var originalProps = await File.ReadAllBytesAsync(centralPropsPath);
        var originalCore = await File.ReadAllBytesAsync(core);

        // Simulates a process that died after writing: the transaction is never committed or rolled back.
        var tx = await FileTransaction.BeginAsync([centralPropsPath, core], RecoveryDirectory(ws), CancellationToken.None,
            description: "升级 Example.Json 到 3.0.0");
        await File.WriteAllTextAsync(centralPropsPath, (await File.ReadAllTextAsync(centralPropsPath)).Replace("1.0.0", "3.0.0"));
        await File.WriteAllTextAsync(core, (await File.ReadAllTextAsync(core)).Replace("<ItemGroup>", "<ItemGroup><!-- ours -->"));
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        await File.AppendAllTextAsync(core, "<!-- edited by someone else afterwards -->");
        var externallyEdited = await File.ReadAllBytesAsync(core);
        await tx.DisposeAsync();

        var pending = Assert.Single(await RecoveryCoordinator.FindAsync(RecoveryDirectory(ws), CancellationToken.None));
        Assert.Null(pending.Error);
        Assert.Equal("升级 Example.Json 到 3.0.0", pending.Description);
        Assert.True(Directory.Exists(pending.BackupDirectory));
        Assert.Equal(PendingFileState.OwnedChange, pending.Files.Single(f => f.Path == centralPropsPath).State);
        Assert.Equal(PendingFileState.Conflict, pending.Files.Single(f => f.Path == core).State);
        Assert.True(pending.CanRecover);

        var results = await RecoveryCoordinator.RecoverAsync(pending, CancellationToken.None);

        Assert.Equal(RecoveryStatus.Restored, results.Single(r => r.Path == centralPropsPath).Status);
        Assert.Equal(RecoveryStatus.Conflict, results.Single(r => r.Path == core).Status);
        Assert.Equal(originalProps, await File.ReadAllBytesAsync(centralPropsPath));
        Assert.Equal(externallyEdited, await File.ReadAllBytesAsync(core));
        Assert.NotEqual(originalCore, externallyEdited);
        var retained = Assert.Single(await RecoveryCoordinator.FindAsync(RecoveryDirectory(ws), CancellationToken.None));
        Assert.Equal(pending.ManifestPath, retained.ManifestPath);
        Assert.Equal(PendingFileState.Unchanged, retained.Files.Single(f => f.Path == centralPropsPath).State);
        Assert.False(retained.CanRecover);

        await RecoveryCoordinator.DiscardAsync(retained, CancellationToken.None);
        Assert.Empty(await RecoveryCoordinator.FindAsync(RecoveryDirectory(ws), CancellationToken.None));
        Assert.Equal(externallyEdited, await File.ReadAllBytesAsync(core));
    }

    [Fact]
    public async Task CorruptRecoveryRecordIsReportedNotThrown()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var record = Path.Combine(RecoveryDirectory(ws), "broken");
        Directory.CreateDirectory(record);
        await File.WriteAllTextAsync(Path.Combine(record, "manifest.json"), "{ not json");

        var pending = Assert.Single(await RecoveryCoordinator.FindAsync(RecoveryDirectory(ws), CancellationToken.None));

        Assert.NotNull(pending.Error);
        Assert.Empty(pending.Files);
        Assert.False(pending.CanRecover);
        Assert.Equal(Path.GetFullPath(record), pending.BackupDirectory);
    }
}
