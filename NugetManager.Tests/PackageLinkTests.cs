using NugetManager.Domain;
using NugetManager.Packages;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class PackageLinkTests
{
    private static readonly PackageLinkIdentity Identity = new("C:\\workspace", "Example.Json", "1.2.3", "public");
    private static PackageDetails Details(string? repository = null, string? project = null, string location = "https://api.nuget.org/v3/index.json") =>
        new("Example.Json", "1.2.3", null, null, null, project is null ? null : new Uri(project), [], [], [])
        {
            RepositoryUrl = repository is null ? null : new Uri(repository),
            SourceName = "public",
            SourceLocation = location,
        };

    [Fact]
    public void RepositoryElementBeatsProjectHomepage() =>
        Assert.Equal("https://github.com/owner/repository", PackageLinkResolver.Resolve(Identity,
            Details("https://github.com/owner/repository.git", "https://github.com/other/home")).GitHubRepository?.AbsoluteUri.TrimEnd('/'));

    [Fact]
    public void ProjectUrlGitHubFallback() =>
        Assert.Equal("https://github.com/owner/repo", PackageLinkResolver.Resolve(Identity,
            Details(project: "https://github.com/owner/repo")).GitHubRepository?.AbsoluteUri.TrimEnd('/'));

    [Fact]
    public void NoRepositoryDisablesGitHub() => Assert.Null(PackageLinkResolver.Resolve(Identity, Details()).GitHubRepository);

    [Fact]
    public void PrivateSourceDoesNotInventPublicPage() =>
        Assert.Null(PackageLinkResolver.Resolve(Identity, Details(location: "https://private.test/index.json")).NuGetPage);

    [Fact]
    public void KnownPublicOriginBuildsVersionPage() =>
        Assert.Equal("https://www.nuget.org/packages/Example.Json/1.2.3", PackageLinkResolver.Resolve(Identity, Details()).NuGetPage?.AbsoluteUri);

    [Fact]
    public void ClickedSourceDoesNotReuseOtherDetails()
    {
        var result = PackageLinkResolver.Resolve(Identity with { SourceName = "private" }, Details("https://github.com/other/wrong"));
        Assert.Null(result.NuGetPage);
        Assert.Null(result.GitHubRepository);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("git+https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git/", "https://github.com/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo", null)]
    [InlineData("https://user@github.com/owner/repo", null)]
    [InlineData("file:///C:/repo", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ssh://git@github.com/owner/repo", null)]
    [InlineData("git@github.com:owner/repo.git", null)]
    [InlineData("https://github.com/owner", null)]
    [InlineData("https://github.com/owner/repo/issues", null)]
    [InlineData("https://github.com/owner/repo?tab=readme", null)]
    [InlineData("https://github.com/owner/%2f", null)]
    [InlineData("https://github.com/owner/..", null)]
    public void OnlyRepositoryRootUrlsAreEnabled(string input, string? expected) =>
        Assert.Equal(expected, PackageLinkResolver.GitHubRepository(input)?.AbsoluteUri.TrimEnd('/'));

    [Fact]
    public async Task ClosedMenuIgnoresLateMetadata()
    {
        var pending = new TaskCompletionSource<PackageDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var menu = new PackageLinkMenu(new FakeMetadata { Read = (_, _) => pending.Task }, new ThrowingOpener());
        var loading = menu.LoadAsync(new Workspace(Identity.WorkspaceDirectory, null, []), Identity, CancellationToken.None);
        menu.Close();
        pending.SetResult(Details("https://github.com/owner/repo"));
        await loading;
        Assert.Null(menu.State.Identity);
        Assert.Null(menu.State.Links.GitHubRepository);
        Assert.False(menu.State.Loading);
    }

    [Fact]
    public async Task OpenFailureIsReported()
    {
        using var menu = new PackageLinkMenu(new FakeMetadata(), new ThrowingOpener());
        await Assert.ThrowsAsync<IOException>(() => menu.OpenAsync(new Uri("https://github.com/owner/repo")));
        await Assert.ThrowsAsync<ArgumentException>(() => menu.OpenAsync(new Uri("file:///C:/repo")));
    }

    [Fact]
    public async Task RepositoryMetadataIsReadFromLocalAndCache()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0", repositoryUrl: "git+https://github.com/owner/repo.git");
        var reader = new PackageMetadataReader(new HttpClient(new RecordingHandler()), ws.PackagesDirectory, ws.Path_("no-user-config.xml"));
        var details = await reader.ReadAsync(new Workspace(ws.Root, null, []), "Example.Json", "1.0.0", "local", CancellationToken.None);
        Assert.Equal("git+https://github.com/owner/repo.git", details.RepositoryUrl?.AbsoluteUri);
        Assert.Equal("local", details.SourceName);
        Assert.Null(PackageLinkResolver.Resolve(new(ws.Root, "Example.Json", "1.0.0", "local"), details).NuGetPage);
        await ws.RestoreAsync(ws.WriteProject("App\\App.csproj", "net10.0", ("Example.Json", "1.0.0")));
        var cached = await reader.ReadAsync(new Workspace(ws.Root, null, []), "Example.Json", "1.0.0", null, CancellationToken.None);
        Assert.Equal(details.RepositoryUrl, cached.RepositoryUrl);
        Assert.Equal("local", cached.SourceName);
    }

    private sealed class ThrowingOpener : IExternalLinkOpener
    {
        public void Open(Uri uri) => throw new IOException("browser unavailable");
    }
}
