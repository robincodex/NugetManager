using NugetManager.Domain;
using NugetManager.Packages;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class SourceControllerTests
{
    private static WorkspaceController Controller(TestWorkspace ws, IPackageCatalog catalog, FakeSdk? sdk = null) =>
        new(new FakeDiscovery(new Workspace(ws.Root, null, [])), sdk ?? new FakeSdk(), catalog, new FakeMetadata(), new FakeOperations());

    private static BrowsePage Page(string name, string id) => new([
        new(new NuGetSource(name, "C:\\feed", true, false), BrowseKind.Ordinary,
            new SearchPage([new SearchHit(id, "1.0.0", name, null)], 0, 30, false, [])),
    ]);

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
        Assert.Null(controller.State.SelectedSourceName);
        await controller.LoadBrowseAsync(false, CancellationToken.None);
        Assert.All(controller.State.Browse!.Sources, g => Assert.Equal(10, g.Page.Items.Count));
        Assert.Equal(2, controller.State.Browse.Sources.Count);
        await controller.SelectSourceAsync("second", CancellationToken.None);
        Assert.Equal(30, Assert.Single(controller.State.Browse!.Sources).Page.Items.Count);
        await controller.SearchAsync("", 0, CancellationToken.None);
        Assert.NotNull(controller.State.Browse);
    }

    [Fact]
    public async Task BrowseCacheIncludesSourceAndPrereleaseAndRefresh()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var revision = 1;
        var catalog = new FakeCatalog { Browse = (source, pre) => Task.FromResult(Page(source ?? "local", $"P{revision}.{pre}")) };
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
        var catalog = new FakeCatalog { Browse = (source, _) => source is null ? slow.Task : Task.FromResult(Page("local", "New")) };
        var controller = Controller(ws, catalog);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        var old = controller.LoadBrowseAsync(false, CancellationToken.None);
        await controller.SelectSourceAsync("local", CancellationToken.None);
        slow.SetResult(Page("old", "Old"));
        await old;
        Assert.Equal("New", controller.State.Browse!.Sources[0].Page.Items[0].PackageId);
        Assert.Equal("local", controller.State.SelectedSourceName);
    }

    [Fact]
    public async Task SourceSelectionRerunsQueryWithoutChangingInstalled()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var sdk = new FakeSdk { Inventory = new PackageInventory([new PackageOccurrence("P", "net10.0", "Installed", "1.0.0", "1.0.0", ReferenceKind.Direct, "P")]) };
        var catalog = new FakeCatalog { SourceSearch = (query, source) => Task.FromResult(new SearchPage(
            [new SearchHit(query, "1.0.0", source ?? "all", null)], 0, 20, false, [])) };
        var controller = Controller(ws, catalog, sdk);
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        var installed = controller.State.Installed.ToArray();
        await controller.SearchAsync("Example", 0, CancellationToken.None);
        await controller.SelectSourceAsync("local", CancellationToken.None);
        Assert.Equal("local", Assert.Single(controller.State.Search!.Items).SourceName);
        Assert.Equal(installed, controller.State.Installed);
    }

    [Fact]
    public async Task RemovedSourceResetsSelectionOnRefresh()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.WriteNuGetConfig($"""<add key="second" value="{ws.FeedDirectory}" />""");
        var controller = Controller(ws, new FakeCatalog());
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectSourceAsync("second", CancellationToken.None);
        ws.WriteNuGetConfig();
        await controller.RefreshAsync(CancellationToken.None);
        Assert.Null(controller.State.SelectedSourceName);
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
