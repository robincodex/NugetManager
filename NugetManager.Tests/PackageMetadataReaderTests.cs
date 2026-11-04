using System.IO.Compression;
using System.Net;
using System.Text;
using NugetManager.Domain;
using NugetManager.Packages;

namespace NugetManager.Tests;

internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class PackageMetadataReaderTests
{
    private static Workspace WorkspaceOf(TestWorkspace ws) => new(ws.Root, null, []);

    private static PackageMetadataReader Reader(TestWorkspace ws, RecordingHandler? handler = null) =>
        new(new HttpClient(handler ?? new RecordingHandler()), ws.PackagesDirectory, Path.Combine(ws.Root, "no-user-config.xml"));

    [Fact]
    public async Task ExplicitSourceDoesNotUseAnotherSourcesCachedMetadata()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0", description: "cached local");
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var second = ws.Path_("second");
        Directory.CreateDirectory(second);
        var other = ws.AddPackage("Example.Json", "1.0.0", description: "from second");
        File.Move(other, Path.Combine(second, Path.GetFileName(other)));
        ws.WriteNuGetConfig($"""<add key="second" value="{second}" />""");
        var details = await Reader(ws).ReadAsync(WorkspaceOf(ws), "Example.Json", "1.0.0", "second", CancellationToken.None);
        Assert.Equal("from second", details.Description);
        Assert.Equal("本地包源 second", TestMessages.Zh(details.Origin));
    }

    [Fact]
    public async Task LocalPackageShowsFrameworkGroups()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "2.0.0", ["netstandard2.0", "net10.0"],
            [("net10.0", "Example.Core", "[1.0.0, )"), ("netstandard2.0", "Example.Core", "[1.0.0, )")],
            description: "JSON helpers", authors: "Contoso", license: "Apache-2.0", projectUrl: "https://example.test/json");

        var details = await Reader(ws).ReadAsync(WorkspaceOf(ws), "Example.Json", "2.0.0", "local", CancellationToken.None);

        Assert.Equal("JSON helpers", details.Description);
        Assert.Equal("Contoso", details.Authors);
        Assert.Equal("Apache-2.0", TestMessages.Zh(details.License));
        Assert.Equal(new Uri("https://example.test/json"), details.ProjectUrl);
        Assert.Contains("net10.0", details.Frameworks);
        Assert.Contains("netstandard2.0", details.Frameworks);
        Assert.Contains(details.Dependencies, g => g.Framework == "net10.0");
        Assert.Equal("Example.Core", details.Dependencies.First(g => g.Framework == "net10.0").Items[0].PackageId);
    }

    [Fact]
    public async Task CachedPackageIsReadFromGlobalPackagesFolder()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0", description: "cached copy");
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        File.Delete(Path.Combine(ws.FeedDirectory, "Example.Json.1.0.0.nupkg"));

        var details = await Reader(ws).ReadAsync(WorkspaceOf(ws), "Example.Json", "1.0.0", null, CancellationToken.None);

        Assert.Equal("cached copy", details.Description);
        Assert.Contains("net10.0", details.Frameworks);
    }

    [Fact]
    public async Task MappedSourceDoesNotLeakToOtherFeed()
    {
        await using var ws = await TestWorkspace.CreateAsync(withFeedConfig: false);
        ws.Write("NuGet.Config", $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{ws.FeedDirectory}" />
                <add key="remote" value="https://feed.example.test/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local"><package pattern="Example.*" /></packageSource>
                <packageSource key="remote"><package pattern="Other.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var handler = new RecordingHandler();

        var details = await Reader(ws, handler).ReadAsync(WorkspaceOf(ws), "Example.Missing", "1.0.0", null, CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(details.Diagnostics, d => d.Code == "NotFound");
    }

    [Fact]
    public async Task DisabledSourceIsNotQueried()
    {
        await using var ws = await TestWorkspace.CreateAsync(withFeedConfig: false);
        ws.Write("NuGet.Config", """
            <configuration>
              <packageSources>
                <clear />
                <add key="remote" value="https://feed.example.test/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="remote" value="true" />
              </disabledPackageSources>
            </configuration>
            """);
        var handler = new RecordingHandler();

        await Reader(ws, handler).ReadAsync(WorkspaceOf(ws), "Example.Json", "1.0.0", null, CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UncachedAuthenticatedDetailsAreExplicit()
    {
        await using var ws = await TestWorkspace.CreateAsync(withFeedConfig: false);
        ws.Write("NuGet.Config", """
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="https://private.example.test/v3/index.json" />
                <add key="open" value="https://open.example.test/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="me" />
                  <add key="ClearTextPassword" value="hunter2" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var details = await Reader(ws, handler).ReadAsync(WorkspaceOf(ws), "Example.Json", "1.0.0", null, CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, u => u.Host == "private.example.test");
        Assert.Contains(details.Diagnostics, d => d.SourceName == "private" && d.Code == "AuthRequired");
        Assert.Contains(details.Diagnostics, d => d.SourceName == "open" && d.Code == "AuthRequired");
        Assert.DoesNotContain(details.Diagnostics, d => TestMessages.Zh(d.Message)!.Contains("hunter2"));
        Assert.Null(details.Description);
    }

    [Fact]
    public async Task RemoteV3DetailsAreRead()
    {
        await using var ws = await TestWorkspace.CreateAsync(withFeedConfig: false);
        ws.Write("NuGet.Config", """
            <configuration>
              <packageSources>
                <clear />
                <add key="remote" value="https://feed.example.test/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        var handler = new RecordingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/v3/index.json" => Json("""{"resources":[{"@id":"https://feed.example.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}"""),
            "/flat/example.json/1.0.0/example.json.nuspec" => Xml("""
                <package><metadata><id>Example.Json</id><version>1.0.0</version><description>remote</description>
                <dependencies><group targetFramework=".NETStandard2.0" /></dependencies></metadata></package>
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var details = await Reader(ws, handler).ReadAsync(WorkspaceOf(ws), "Example.Json", "1.0.0", "remote", CancellationToken.None);

        Assert.Equal("remote", details.Description);
        Assert.Contains("netstandard2.0", details.Frameworks);
    }

    [Fact]
    public async Task XmlExternalEntityIsRejected()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var secret = ws.Write("secret.txt", "TOP-SECRET");
        var path = Path.Combine(ws.FeedDirectory, "Example.Evil.1.0.0.nupkg");
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Example.Evil.nuspec").Open(), new UTF8Encoding(false));
            writer.Write($"""
                <?xml version="1.0"?>
                <!DOCTYPE package [<!ENTITY xxe SYSTEM "file:///{secret.Replace('\\', '/')}">]>
                <package><metadata><id>Example.Evil</id><version>1.0.0</version><description>&xxe;</description></metadata></package>
                """);
        }

        var details = await Reader(ws).ReadAsync(WorkspaceOf(ws), "Example.Evil", "1.0.0", "local", CancellationToken.None);

        Assert.Null(details.Description);
        Assert.Contains(details.Diagnostics, d => d.Code == "InvalidMetadata");
    }

    [Fact]
    public async Task MissingMetadataIsNotInvented()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Bare", "1.0.0", authors: null, license: null);

        var details = await Reader(ws).ReadAsync(WorkspaceOf(ws), "Example.Bare", "1.0.0", "local", CancellationToken.None);
        var missing = await Reader(ws).ReadAsync(WorkspaceOf(ws), "Example.Nope", "1.0.0", "local", CancellationToken.None);

        Assert.Null(details.Authors);
        Assert.Null(TestMessages.Zh(details.License));
        Assert.Null(details.ProjectUrl);
        Assert.Null(missing.Description);
        Assert.Empty(missing.Frameworks);
        Assert.Contains(missing.Diagnostics, d => d.Code == "NotFound");
    }

    [Fact]
    public async Task InvalidIdentityIsRejected()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Reader(ws).ReadAsync(WorkspaceOf(ws), "..\\..\\evil", "1.0.0", null, CancellationToken.None));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
}
