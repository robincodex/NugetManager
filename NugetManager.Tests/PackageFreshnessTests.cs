using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NugetManager.Domain;
using NugetManager.Packages;
using NugetManager.Sdk;

namespace NugetManager.Tests;

public class PackageFreshnessTests
{
    [Theory]
    [InlineData("outdated", "success")]
    [InlineData("outdated", "failure")]
    [InlineData("outdated", "cancel")]
    [InlineData("versions", "success")]
    [InlineData("versions", "failure")]
    [InlineData("versions", "cancel")]
    [InlineData("search", "success")]
    [InlineData("search", "failure")]
    [InlineData("search", "cancel")]
    public async Task FreshQueriesPreserveEnvironmentAndCleanOnlyTheirOwnCache(string query, string outcome)
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var sentinel = ws.Write(".http-cache/keep.txt", "keep");
        var environment = ws.Environment;
        var caches = new List<string>();
        var runner = new FakeProcessRunner(request =>
        {
            Assert.NotNull(request.Environment);
            Assert.Equal(ws.PackagesDirectory, request.Environment["NUGET_PACKAGES"]);
            Assert.Equal(environment["NUGET_PLUGINS_CACHE_PATH"], request.Environment["NUGET_PLUGINS_CACHE_PATH"]);
            var cache = request.Environment["NUGET_HTTP_CACHE_PATH"]!;
            Assert.True(Directory.Exists(cache));
            Assert.NotEqual(environment["NUGET_HTTP_CACHE_PATH"], cache);
            Assert.DoesNotContain(cache, caches);
            caches.Add(cache);
            File.WriteAllText(Path.Combine(cache, "metadata.dat"), "old metadata");
            if (outcome == "cancel") throw new OperationCanceledException();
            return outcome == "failure" ? new(1, "", "query failed")
                : new(0, """{"version":1,"projects":[],"searchResult":[]}""", "");
        });
        var sdk = new SdkClient(runner, environment);
        async Task Query()
        {
            if (query == "outdated")
                await sdk.ListOutdatedAsync(new(Path.Combine(ws.Root, "App.csproj"), "App"), false, CancellationToken.None);
            else
                await sdk.SearchAsync(ws.Root, "Example.Json", 0, 20, false, query == "versions", null, CancellationToken.None);
        }

        for (var i = 0; i < 2; i++)
        {
            if (outcome == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(Query);
            else if (outcome == "failure") await Assert.ThrowsAsync<SdkCommandException>(Query);
            else await Query();
            Assert.All(caches, cache => Assert.False(Directory.Exists(cache)));
        }
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(Path.Combine(ws.Root, ".http-cache"), environment["NUGET_HTTP_CACHE_PATH"]);
    }

    [Fact]
    public async Task VersionQuerySeesNewlyPublishedVersionAcrossSdkInstances()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        await using var feed = new MutableFeed();
        ws.Write("NuGet.Config", $"""
            <configuration><packageSources><clear />
            <add key="private" value="{feed.Url}v3/index.json" allowInsecureConnections="true" />
            </packageSources></configuration>
            """);
        var workspace = new Workspace(ws.Root, null, []);
        async Task<PackageVersions> Query() => await new PackageCatalog(ws.CreateSdk())
            .GetVersionsAsync(workspace, "Example.Json", false, CancellationToken.None);
        var initial = await Query();
        Assert.Empty(initial.Diagnostics);
        Assert.Equal(["1.0.0"], initial.Versions);
        feed.LatestVersion = "2.0.0";

        var refreshed = await Query();

        Assert.Empty(refreshed.Diagnostics);
        Assert.Equal(["2.0.0", "1.0.0"], refreshed.Versions);
    }

    [Fact]
    public async Task OutdatedQuerySeesNewlyPublishedVersionAcrossSdkInstances()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        var path = ws.WriteProject("App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(path);
        await using var feed = new MutableFeed();
        ws.Write("NuGet.Config", $"""
            <configuration><packageSources><clear />
            <add key="private" value="{feed.Url}v3/index.json" allowInsecureConnections="true" />
            </packageSources></configuration>
            """);
        var project = new ProjectDescriptor(path, "App");
        var workspace = new Workspace(ws.Root, null, [project]);
        var evaluation = await ws.CreateSdk().EvaluateAsync(project, CancellationToken.None);
        async Task<UpdateReport> Query() => await new PackageCatalog(ws.CreateSdk())
            .FindUpdatesAsync(workspace, [evaluation], WorkspaceScope.All, false, CancellationToken.None);

        var initial = await Query();
        Assert.True(initial.IsComplete, string.Join("\n", initial.Diagnostics));
        Assert.Empty(initial.Candidates);
        feed.LatestVersion = "2.0.0";

        var refreshed = await Query();

        Assert.True(refreshed.IsComplete, string.Join("\n", refreshed.Diagnostics));
        Assert.Equal("2.0.0", Assert.Single(refreshed.Candidates).ToVersion);
    }

    private sealed class MutableFeed : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        public string Url { get; }
        public volatile string LatestVersion = "1.0.0";

        public MutableFeed()
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
            port.Stop();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                var body = context.Request.Url!.AbsolutePath switch
                {
                    "/v3/index.json" => $$"""
                        {"version":"3.0.0","resources":[
                        {"@id":"{{Url}}flat/","@type":"PackageBaseAddress/3.0.0"},
                        {"@id":"{{Url}}registration/","@type":"RegistrationsBaseUrl/3.6.0"}]}
                        """,
                    "/flat/example.json/index.json" => LatestVersion == "1.0.0"
                        ? """{"versions":["1.0.0"]}"""
                        : """{"versions":["1.0.0","2.0.0"]}""",
                    "/registration/example.json/index.json" => Registration(),
                    _ => null,
                };
                context.Response.StatusCode = body is null ? 404 : 200;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private string Registration()
        {
            var versions = LatestVersion == "1.0.0" ? new[] { "1.0.0" } : ["1.0.0", "2.0.0"];
            return JsonSerializer.Serialize(new
            {
                count = 1,
                items = new[]
                {
                    new
                    {
                        count = versions.Length, lower = "1.0.0", upper = versions[^1],
                        items = versions.Select(version => new
                        {
                            catalogEntry = new
                            {
                                id = "Example.Json", version, listed = true,
                                published = "2026-01-01T00:00:00Z", dependencyGroups = Array.Empty<object>(),
                            },
                            packageContent = $"{Url}flat/example.json/{version}/example.json.{version}.nupkg",
                        }),
                    },
                },
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _serve; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _listener.Close(); _stop.Dispose(); }
        }
    }
}
