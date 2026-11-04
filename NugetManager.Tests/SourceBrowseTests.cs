using System.Net;
using System.Text;
using NugetManager.Domain;
using NugetManager.Packages;

namespace NugetManager.Tests;

public class SourceBrowseTests
{
    private static Workspace WorkspaceOf(TestWorkspace ws) => new(ws.Root, null, []);

    private static async Task<TestWorkspace> TwoFeedsAsync()
    {
        var ws = await TestWorkspace.CreateAsync();
        var second = ws.Path_("second");
        Directory.CreateDirectory(second);
        for (var i = 0; i < 35; i++)
        {
            var file = ws.AddPackage($"Example.P{i:00}", "1.0.0");
            File.Copy(file, Path.Combine(second, Path.GetFileName(file)));
        }
        ws.WriteNuGetConfig($"""<add key="second" value="{second}" />""");
        return ws;
    }

    [Fact]
    public async Task SearchAcrossSourcesPreservesDuplicates()
    {
        await using var ws = await TwoFeedsAsync();
        var page = await new PackageCatalog(ws.CreateSdk()).SearchAsync(WorkspaceOf(ws), "Example.P00", 0, 30, false, CancellationToken.None);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(["local", "second"], page.Items.Select(i => i.SourceName).Order());
    }

    [Theory]
    [InlineData(null, 2, 10)]
    [InlineData("second", 1, 30)]
    public async Task InitialBrowseUsesPerSourceLimits(string? source, int groups, int count)
    {
        await using var ws = await TwoFeedsAsync();
        var page = await new PackageCatalog(ws.CreateSdk()).BrowseAsync(WorkspaceOf(ws), source, false, CancellationToken.None);
        Assert.Equal(groups, page.Sources.Count);
        Assert.All(page.Sources, g =>
        {
            Assert.Equal(count, g.Page.Items.Count);
            Assert.Equal(BrowseKind.Ordinary, g.Kind);
            Assert.All(g.Page.Items, i => Assert.Equal(g.Source.Name, i.SourceName));
        });
    }

    [Fact]
    public async Task ExplicitSourceRestrictsSearchAndVersions()
    {
        await using var ws = await TwoFeedsAsync();
        var newer = ws.AddPackage("Example.P00", "2.0.0");
        File.Move(newer, Path.Combine(ws.Path_("second"), Path.GetFileName(newer)));
        var catalog = new PackageCatalog(ws.CreateSdk());
        var page = await catalog.SearchAsync(WorkspaceOf(ws), "Example.P00", 0, 30, false, CancellationToken.None, "second");
        Assert.Equal("second", Assert.Single(page.Items).SourceName);
        Assert.Equal("2.0.0", page.Items[0].Version);
        var versions = await catalog.GetVersionsAsync(WorkspaceOf(ws), "Example.P00", false, CancellationToken.None, "local");
        Assert.Equal(["1.0.0"], versions.Versions);
    }

    [Fact]
    public async Task FailedSourceDoesNotHideSuccessfulSource()
    {
        await using var ws = await TwoFeedsAsync();
        ws.WriteNuGetConfig("""<add key="broken" value="http://127.0.0.1:9/v3/index.json" />""");
        var page = await new PackageCatalog(ws.CreateSdk()).BrowseAsync(WorkspaceOf(ws), null, false, CancellationToken.None);
        Assert.Equal(10, page.Sources[0].Page.Items.Count);
        Assert.NotEmpty(page.Sources.Single(g => g.Source.Name == "broken").Page.Diagnostics);
    }

    [Fact]
    public async Task PopularityUsesVerifiedGalleryOrderingAndSourceIdentity()
    {
        var handler = new RecordingHandler(r =>
        {
            Assert.Contains("/search/query?", r.RequestUri!.AbsoluteUri);
            Assert.Contains("sortBy=totalDownloads-desc", r.RequestUri.Query);
            Assert.Contains("take=30", r.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"totalHits":2,"data":[{"PackageRegistration":{"Id":"Popular","DownloadCount":100},"Version":"1.0.0","Description":"popular"},{"PackageRegistration":{"Id":"Other","DownloadCount":50},"Version":"2.0.0"}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var result = await new PopularPackageReader(new HttpClient(handler)).ReadAsync(
            new NuGetSource("public", "https://api.nuget.org/v3/index.json", true, false), 30, false, CancellationToken.None);
        Assert.True(result.Supported);
        Assert.Equal(["Popular", "Other"], result.Items.Select(i => i.PackageId));
        Assert.All(result.Items, i => Assert.Equal("public", i.SourceName));
    }

    [Theory]
    [InlineData("https://private.test/index.json", false)]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    public async Task UnsupportedOrCredentialedSourcesNeverUseAnonymousPopularity(string url, bool credentials)
    {
        var result = await new PopularPackageReader(new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be used"))))
            .ReadAsync(new NuGetSource("private", url, true, credentials), 10, false, CancellationToken.None);
        Assert.False(result.Supported);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task PopularityFailureIsReportedNotOrdinarySuccess()
    {
        var result = await new PopularPackageReader(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
            .ReadAsync(new NuGetSource("public", "https://api.nuget.org/v3/index.json", true, false), 10, false, CancellationToken.None);
        Assert.True(result.Supported);
        Assert.Empty(result.Items);
        Assert.NotEmpty(result.Diagnostics);
    }
}
