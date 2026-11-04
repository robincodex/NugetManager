using NugetManager.Domain;
using NugetManager.Packages;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class SourceSelectionTests
{
    [Theory]
    [InlineData("C:\\feed", "file:///C:/feed/")]
    [InlineData("C:\\feed", "c:\\FEED\\")]
    [InlineData("https://example.test/v3/index.json", "https://EXAMPLE.test/v3/index.json/")]
    public void SourceLocationsMatchCacheOrigins(string configured, string cached)
    {
        Assert.True(new NuGetSource("source", configured, true, false).MatchesLocation(cached));
        Assert.False(new NuGetSource("other", "https://other.test/v3/index.json", true, false).MatchesLocation(cached));
    }

    [Fact]
    public void AllAndEmptyHaveDifferentIntentAndSubsetNormalizesNames()
    {
        NuGetSource[] sources = [new("First", "C:\\a", true, false), new("Second", "C:\\b", true, false), new("Disabled", "C:\\c", false, false)];
        Assert.Equal(["First", "Second"], SourceSelection.All.Resolve(sources));
        Assert.Empty(SourceSelection.Subset([]).Resolve(sources));
        Assert.Equal(["First"], SourceSelection.Subset(["FIRST", "first", "Disabled", "missing"]).Resolve(sources));
        Assert.Equal(SourceSelection.Subset(["First", "Second"]), SourceSelection.Subset(["SECOND", "first"]));
        Assert.NotEqual(SourceSelection.All, SourceSelection.Subset(["First", "Second"]));
    }

    [Fact]
    public async Task EmptySelectionDoesNotQueryCatalog()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var catalog = new FakeCatalog
        {
            Browse = (_, _) => throw new InvalidOperationException("Empty must not query"),
            Search = (_, _) => throw new InvalidOperationException("Empty must not search"),
        };
        var controller = new WorkspaceController(new FakeDiscovery(new Workspace(ws.Root, null, [])), new FakeSdk(), catalog, new FakeMetadata(), new FakeOperations());
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset([]), CancellationToken.None);
        Assert.Empty(controller.State.Browse!.Sources);
        await controller.SearchAsync("anything", 0, CancellationToken.None);
        Assert.Empty(controller.State.Search!.Items);
        Assert.False(controller.State.SearchLoading);
    }

    [Fact]
    public async Task SourceSetCacheIgnoresOrderAndRefreshReconcilesIntent()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.WriteNuGetConfig($"""<add key="second" value="{ws.FeedDirectory}" />""");
        var calls = 0;
        var catalog = new FakeCatalog { Browse = (_, _) => { calls++; return Task.FromResult(new BrowsePage([])); } };
        var controller = new WorkspaceController(new FakeDiscovery(new Workspace(ws.Root, null, [])), new FakeSdk(), catalog, new FakeMetadata(), new FakeOperations());
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["second", "local"]), CancellationToken.None);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["LOCAL", "SECOND"]), CancellationToken.None);
        Assert.Equal(1, calls);
        await controller.SelectSourcesAsync(SourceSelection.Subset(["second"]), CancellationToken.None);
        ws.WriteNuGetConfig();
        await controller.RefreshAsync(CancellationToken.None);
        Assert.Empty(controller.State.SelectedSources.Resolve(controller.State.Sources));
        Assert.False(controller.State.SelectedSources.IsAll);
        await controller.SelectSourcesAsync(SourceSelection.All, CancellationToken.None);
        ws.WriteNuGetConfig($"""<add key="new" value="{ws.FeedDirectory}" />""");
        await controller.RefreshAsync(CancellationToken.None);
        Assert.Equal(["local", "new"], controller.State.SelectedSources.Resolve(controller.State.Sources));
    }

    [Fact]
    public async Task CatalogUsesResolvedCountAndEmptyMakesNoSdkCalls()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var noSdk = new PackageCatalog(new FakeSdk());
        var workspace = new Workspace(ws.Root, null, []);
        Assert.Empty((await noSdk.BrowseAsync(workspace, SourceSelection.Subset([]), false, CancellationToken.None)).Sources);
        Assert.Empty((await noSdk.SearchAsync(workspace, "x", 0, 20, false, CancellationToken.None, SourceSelection.Subset([]))).Items);
        for (var i = 0; i < 35; i++) ws.AddPackage($"Example.P{i:00}", "1.0.0");
        var page = await new PackageCatalog(ws.CreateSdk()).BrowseAsync(workspace, SourceSelection.All, false, CancellationToken.None);
        Assert.Equal(30, Assert.Single(page.Sources).Page.Items.Count);
    }
}
