using NugetManager.Domain;
using NugetManager.Packages;

namespace NugetManager.Tests;

public class PackageCatalogTests
{
    private static Workspace WorkspaceOf(TestWorkspace ws, params string[] projects) =>
        new(ws.Root, null, projects.Select(p => new ProjectDescriptor(p, Path.GetFileNameWithoutExtension(p))).ToList());

    private static async Task<(Workspace Workspace, IReadOnlyList<ProjectEvaluation> Evaluations)> LoadAsync(TestWorkspace ws, params string[] projects)
    {
        var sdk = ws.CreateSdk();
        var workspace = WorkspaceOf(ws, projects);
        var evaluations = new List<ProjectEvaluation>();
        foreach (var p in workspace.Projects)
        {
            evaluations.Add(await sdk.EvaluateAsync(p, CancellationToken.None));
        }
        return (workspace, evaluations);
    }

    private static async Task<TestWorkspace> WithVersionsAsync()
    {
        var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        ws.AddPackage("Example.Json", "2.0.0");
        ws.AddPackage("Example.Json", "3.0.0");
        ws.AddPackage("Example.Json", "4.0.0-preview.1");
        return ws;
    }

    [Fact]
    public async Task DefaultsToStableAllowsMajor()
    {
        await using var ws = await WithVersionsAsync();
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var (workspace, evaluations) = await LoadAsync(ws, project);

        var report = await new PackageCatalog(ws.CreateSdk()).FindUpdatesAsync(workspace, evaluations, WorkspaceScope.All, false, CancellationToken.None);

        Assert.True(report.IsComplete);
        Assert.Equal("3.0.0", Assert.Single(report.Candidates).Upgrades[0].ToVersion);
        Assert.Equal("1.0.0", report.Candidates[0].Upgrades[0].FromVersion);
        Assert.DoesNotContain(report.Candidates.SelectMany(p => p.Upgrades), p => p.ToVersion == "4.0.0-preview.1");
    }

    [Fact]
    public async Task EnabledPrereleaseIncludesPreview()
    {
        await using var ws = await WithVersionsAsync();
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var (workspace, evaluations) = await LoadAsync(ws, project);

        var report = await new PackageCatalog(ws.CreateSdk()).FindUpdatesAsync(workspace, evaluations, WorkspaceScope.All, true, CancellationToken.None);

        Assert.Equal("4.0.0-preview.1", Assert.Single(report.Candidates).ToVersion);
    }

    [Fact]
    public async Task DoesNotDowngradeProjectAlreadyNewer()
    {
        await using var ws = await WithVersionsAsync();
        var old = ws.WriteProject("Old/Old.csproj", "net10.0", ("Example.Json", "1.0.0"));
        var current = ws.WriteProject("Current/Current.csproj", "net10.0", ("Example.Json", "3.0.0"));
        await ws.RestoreAsync(old);
        await ws.RestoreAsync(current);
        var (workspace, evaluations) = await LoadAsync(ws, old, current);

        var report = await new PackageCatalog(ws.CreateSdk()).FindUpdatesAsync(workspace, evaluations, WorkspaceScope.All, false, CancellationToken.None);

        var upgrade = Assert.Single(Assert.Single(report.Candidates).Upgrades);
        Assert.Equal(old, upgrade.Reference.ProjectPath);

        var scoped = await new PackageCatalog(ws.CreateSdk()).FindUpdatesAsync(workspace, evaluations, new WorkspaceScope(current), false, CancellationToken.None);
        Assert.Empty(scoped.Candidates);
    }

    [Fact]
    public async Task TransitivePackagesAreNotUpgradeTargets()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Core", "1.0.0");
        ws.AddPackage("Example.Core", "2.0.0");
        ws.AddPackage("Example.Json", "1.0.0", dependencies: [("net10.0", "Example.Core", "1.0.0"), ("net8.0", "Example.Core", "1.0.0")]);
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var (workspace, evaluations) = await LoadAsync(ws, project);

        var report = await new PackageCatalog(ws.CreateSdk()).FindUpdatesAsync(workspace, evaluations, WorkspaceScope.All, false, CancellationToken.None);

        Assert.DoesNotContain(report.Candidates, c => c.PackageId == "Example.Core");
    }

    [Fact]
    public async Task SourceFailureMakesReportIncomplete()
    {
        await using var ws = await WithVersionsAsync();
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        // NuGet rejects plain HTTP sources, which surfaces as a structured per-project error.
        ws.WriteNuGetConfig("""<add key="broken" value="http://127.0.0.1:9/v3/index.json" />""");
        var (workspace, evaluations) = await LoadAsync(ws, project);

        var report = await new PackageCatalog(ws.CreateSdk()).FindUpdatesAsync(workspace, evaluations, WorkspaceScope.All, false, CancellationToken.None);

        Assert.False(report.IsComplete);
        Assert.NotEmpty(report.Diagnostics);
    }

    [Fact]
    public async Task SearchPaginationPreservesSource()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
        {
            ws.AddPackage("Example." + name, "1.0.0");
        }
        var workspace = WorkspaceOf(ws);
        var catalog = new PackageCatalog(ws.CreateSdk());

        var first = await catalog.SearchAsync(workspace, "Example", 0, 2, false, CancellationToken.None);
        var last = await catalog.SearchAsync(workspace, "Example", 4, 2, false, CancellationToken.None);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.All(first.Items, i => Assert.Equal("local", i.SourceName));
        Assert.Single(last.Items);
        Assert.False(last.HasMore);
        Assert.Equal(4, last.Skip);
        Assert.Empty(first.Items.Select(i => i.PackageId).Intersect(last.Items.Select(i => i.PackageId)));
    }

    [Fact]
    public async Task VersionListIsSemanticallyOrdered()
    {
        await using var ws = await WithVersionsAsync();
        ws.AddPackage("Example.Json", "10.0.0");
        var catalog = new PackageCatalog(ws.CreateSdk());

        var stable = await catalog.GetVersionsAsync(WorkspaceOf(ws), "Example.Json", false, CancellationToken.None);
        var all = await catalog.GetVersionsAsync(WorkspaceOf(ws), "Example.Json", true, CancellationToken.None);

        Assert.Equal(["10.0.0", "3.0.0", "2.0.0", "1.0.0"], stable.Versions);
        Assert.Contains("4.0.0-preview.1", all.Versions);
    }
}
