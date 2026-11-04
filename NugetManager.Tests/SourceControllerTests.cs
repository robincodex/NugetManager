using NugetManager.Domain;
using NugetManager.Packages;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class SourceControllerTests
{
    [Fact]
    public async Task InstalledSelectionFiltersOccurrencesAndUpgradePreviewByActualVersionOrigin()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.WriteNuGetConfig($"""<add key="second" value="{ws.Path_("second-feed")}" />""");
        var firstProject = new ProjectDescriptor(ws.Path_("First.csproj"), "First");
        var secondProject = new ProjectDescriptor(ws.Path_("Second.csproj"), "Second");
        var unknownProject = new ProjectDescriptor(ws.Path_("Unknown.csproj"), "Unknown");
        PackageOccurrence[] occurrences = [
            new(firstProject.Path, "net10.0", "Same", "1.0", "1.0.0", ReferenceKind.Direct, firstProject.Path) { SourceLocation = ws.FeedDirectory },
            new(secondProject.Path, "net10.0", "Same", "2.0", "2.0.0", ReferenceKind.Direct, secondProject.Path) { SourceLocation = ws.Path_("second-feed") },
            new(unknownProject.Path, "net10.0", "Unknown", "1.0", "1.0.0", ReferenceKind.Direct, unknownProject.Path),
            new(firstProject.Path, "net10.0", "Transitive", null, "1.0.0", ReferenceKind.Transitive, null) { SourceLocation = ws.FeedDirectory },
            new(secondProject.Path, "net10.0", "Implicit", null, "1.0.0", ReferenceKind.SdkImplicit, null) { SourceLocation = ws.Path_("second-feed") },
        ];
        var catalog = new FakeCatalog
        {
            Browse = (_, _) => throw new InvalidOperationException("Installed selection must not browse."),
            Report = new([new("Same", occurrences.Where(o => o.PackageId == "Same").Select(o =>
                new PackageUpgrade(new(o.PackageId, o.Framework, o.ProjectPath, null, o.RequestedVersion, null, true, null),
                    o.ResolvedVersion, "3.0.0")).ToArray())], [], true),
        };
        var operations = new FakeOperations();
        var controller = new WorkspaceController(new FakeDiscovery(new(ws.Root, null, [firstProject, secondProject, unknownProject])),
            new FakeSdk { Inventory = new(occurrences) }, catalog, new FakeMetadata(), operations);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        Assert.Equal(3, controller.State.Installed.Count);
        controller.SetUpgradeSelected("Same", true);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["local"]), CancellationToken.None, loadBrowse: false);
        Assert.Equal(firstProject.Path, Assert.Single(controller.State.Installed).ProjectPath);
        Assert.Single(controller.State.Implicit);
        Assert.Empty(controller.State.SdkImplicit);
        Assert.Equal(firstProject.Path, Assert.Single(Assert.Single(controller.State.Upgrades).Upgrades).Reference.ProjectPath);
        var plan = await controller.PreviewSelectedUpgradesAsync(CancellationToken.None);
        Assert.Equal(firstProject.Path, Assert.Single(plan.Changes).ProjectPath);
        Assert.Contains(controller.State.Diagnostics, d => d.Code == "UnknownInstalledSource");
        await controller.SelectSourcesAsync(SourceSelection.Subset(["second"]), CancellationToken.None, loadBrowse: false);
        Assert.Equal(secondProject.Path, Assert.Single(controller.State.Installed).ProjectPath);
        Assert.Single(controller.State.SdkImplicit);
        await controller.SelectSourcesAsync(SourceSelection.Subset([]), CancellationToken.None, loadBrowse: false);
        Assert.Empty(controller.State.Installed);
        Assert.Empty(controller.State.Upgrades);
        Assert.Empty(controller.State.SelectedPackageIds);
        await controller.SelectSourcesAsync(SourceSelection.All, CancellationToken.None, loadBrowse: false);
        Assert.Equal(3, controller.State.Installed.Count);
    }

    [Fact]
    public async Task MultiSourcePagingUsesPerSourceOffsetRatherThanCombinedRowCount()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var catalog = new FakeCatalog
        {
            Search = (_, skip) => Task.FromResult(new SearchPage(
                Enumerable.Range(skip, 20).SelectMany(i => new[] { new SearchHit($"P{i}", "1.0", "local", null),
                    new SearchHit($"P{i}", "1.0", "second", null) }).ToArray(), skip, 20, true, [])),
        };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SearchAsync("packages", 0, CancellationToken.None);
        Assert.Equal(20, controller.State.Search!.NextSkip);
        await controller.SearchAsync("packages", controller.State.Search.NextSkip, CancellationToken.None);
        Assert.Equal(80, controller.State.Search!.Items.Count);
        Assert.Equal(40, controller.State.Search.NextSkip);
        await controller.SearchAsync("packages", controller.State.Search.NextSkip, CancellationToken.None);
        Assert.Equal(120, controller.State.Search!.Items.Count);
        Assert.Equal(60, controller.State.Search.NextSkip);
    }

    [Fact]
    public async Task CancelledVersionRequestStopsItsOwnRing()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var catalog = new FakeCatalog { Versions = _ => Task.FromException<PackageVersions>(new OperationCanceledException()) };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectPackageAsync("Example", "1.0", "local", CancellationToken.None);
        Assert.False(PresentationModel.BuildActivity(controller.State, false).Versions);
    }

    private static WorkspaceController Controller(TestWorkspace ws, IPackageCatalog catalog, FakeSdk? sdk = null) =>
        new(new FakeDiscovery(new Workspace(ws.Root, null, [])), sdk ?? new FakeSdk(), catalog, new FakeMetadata(), new FakeOperations());

    private static BrowsePage Page(string name, string id) => new([
        new(new NuGetSource(name, "C:\\feed", true, false), BrowseKind.Ordinary,
            new SearchPage([new SearchHit(id, "1.0.0", name, null)], 0, 30, false, [])),
    ]);

    [Fact]
    public async Task RefreshingBrowseKeepsExistingGroupsVisibleUntilReplacementArrives()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var replacement = new TaskCompletionSource<BrowsePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var original = Page("local", "Existing");
        var catalog = new FakeCatalog { Browse = (_, _) => ++calls == 1 ? Task.FromResult(original) : replacement.Task };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.LoadBrowseAsync(false, CancellationToken.None);
        var refresh = controller.LoadBrowseAsync(true, CancellationToken.None);
        try
        {
            Assert.True(controller.State.SearchLoading);
            Assert.Same(original, controller.State.Browse);
        }
        finally { replacement.SetResult(Page("local", "Replacement")); await refresh; }
        Assert.Equal("Replacement", controller.State.Browse!.Sources[0].Page.Items[0].PackageId);
    }

    [Fact]
    public async Task ChangingSourceUsesPendingTextRatherThanLastDebouncedQuery()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var controller = Controller(ws, new FakeCatalog());
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SearchAsync("previous", 0, CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["local"]), CancellationToken.None, "");
        Assert.Equal("", controller.State.SearchQuery);
        Assert.NotNull(controller.State.Browse);
        Assert.Null(controller.State.Search);
    }

    [Fact]
    public async Task FirstBrowseLoadsAllSourcesAndSingleSourceReloadsThirty()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var second = ws.Path_("second");
        Directory.CreateDirectory(second);
        for (var i = 0; i < 35; i++)
        {
            var file = ws.AddPackage($"Example.P{i:00}", "1.0.0");
            File.Copy(file, Path.Combine(second, Path.GetFileName(file)));
        }
        ws.WriteNuGetConfig($"""<add key="second" value="{second}" />""");
        var controller = Controller(ws, new PackageCatalog(ws.CreateSdk()));
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        Assert.True(controller.State.SelectedSources.IsAll);
        await controller.LoadBrowseAsync(false, CancellationToken.None);
        Assert.All(controller.State.Browse!.Sources, g => Assert.Equal(10, g.Page.Items.Count));
        Assert.Equal(2, controller.State.Browse.Sources.Count);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["second"]), CancellationToken.None);
        Assert.Equal(30, Assert.Single(controller.State.Browse!.Sources).Page.Items.Count);
        await controller.SearchAsync("", 0, CancellationToken.None);
        Assert.NotNull(controller.State.Browse);
    }

    [Fact]
    public async Task BrowseCacheIncludesSourceAndPrereleaseAndRefresh()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var revision = 1;
        var catalog = new FakeCatalog { Browse = (_, pre) => Task.FromResult(Page("local", $"P{revision}.{pre}")) };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.LoadBrowseAsync(false, CancellationToken.None);
        revision = 2;
        await controller.LoadBrowseAsync(false, CancellationToken.None);
        Assert.Equal("P1.False", controller.State.Browse!.Sources[0].Page.Items[0].PackageId);
        await controller.LoadBrowseAsync(true, CancellationToken.None);
        Assert.Equal("P2.False", controller.State.Browse!.Sources[0].Page.Items[0].PackageId);
        await controller.SetIncludePrereleaseAsync(true, CancellationToken.None);
        Assert.Equal("P2.True", controller.State.Browse!.Sources[0].Page.Items[0].PackageId);
    }

    [Fact]
    public async Task LateOldSourceResponseIsIgnored()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var slow = new TaskCompletionSource<BrowsePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog { Browse = (source, _) => source.IsAll ? slow.Task : Task.FromResult(Page("local", "New")) };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        var old = controller.LoadBrowseAsync(false, CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["local"]), CancellationToken.None);
        slow.SetResult(Page("old", "Old"));
        await old;
        Assert.Equal("New", controller.State.Browse!.Sources[0].Page.Items[0].PackageId);
        Assert.Equal(["local"], controller.State.SelectedSources.Resolve(controller.State.Sources));
    }

    [Fact]
    public async Task SourceSelectionRerunsQueryWithoutChangingInstalled()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var sdk = new FakeSdk { Inventory = new PackageInventory([new PackageOccurrence("P", "net10.0", "Installed", "1.0.0", "1.0.0", ReferenceKind.Direct, "P") { SourceLocation = ws.FeedDirectory }]) };
        var catalog = new FakeCatalog { SourceSearch = (query, source) => Task.FromResult(new SearchPage(
            [new SearchHit(query, "1.0.0", source.Resolve(NuGetConfigReader.Load(ws.Root).Sources).Single(), null)], 0, 20, false, [])) };
        var controller = Controller(ws, catalog, sdk);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        var installed = controller.State.Installed.ToArray();
        await controller.SearchAsync("Example", 0, CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["local"]), CancellationToken.None);
        Assert.Equal("local", Assert.Single(controller.State.Search!.Items).SourceName);
        Assert.Equal(installed, controller.State.Installed);
    }

    [Fact]
    public async Task RemovedSourceLeavesExplicitEmptySelectionOnRefresh()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.WriteNuGetConfig($"""<add key="second" value="{ws.FeedDirectory}" />""");
        var controller = Controller(ws, new FakeCatalog());
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["second"]), CancellationToken.None);
        ws.WriteNuGetConfig();
        await controller.RefreshAsync(CancellationToken.None);
        Assert.False(controller.State.SelectedSources.IsAll);
        Assert.Empty(controller.State.SelectedSources.Resolve(controller.State.Sources));
        Assert.DoesNotContain(controller.State.Sources, s => s.Name == "second");
    }

    [Fact]
    public async Task SamePackageFromDifferentSourcesLoadsCorrectVersions()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var catalog = new FakeCatalog { Versions = source => Task.FromResult(new PackageVersions("Same",
            [source == "local" ? "1.0.0" : "2.0.0"], []) { SourceName = source }) };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectPackageAsync("Same", "1.0.0", "local", CancellationToken.None);
        Assert.Equal(["1.0.0"], controller.State.Versions!.Versions);
        await controller.SelectPackageAsync("Same", "2.0.0", "second", CancellationToken.None);
        Assert.Equal(["2.0.0"], controller.State.Versions!.Versions);
        Assert.Equal("second", controller.State.SelectedPackageSourceName);
    }
}
