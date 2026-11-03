using NugetManager.Domain;
using NugetManager.Sdk;

namespace NugetManager.Tests;

internal sealed class FakeProcessRunner(Func<ProcessRequest, ProcessResult> handler) : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        return Task.FromResult(handler(request));
    }
}

public class SdkClientTests
{
    [Fact]
    public async Task CapabilitiesPassOnInstalledSdk()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var version = await ws.CreateSdk().VerifyCapabilitiesAsync(ws.Root, CancellationToken.None);
        Assert.StartsWith("1", version);
    }

    [Fact]
    public async Task PinnedUnsupportedSdkIsBlocked()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.Write("global.json", """{ "sdk": { "version": "6.0.100", "rollForward": "disable" } }""");

        var error = await Assert.ThrowsAsync<SdkCapabilityException>(
            () => ws.CreateSdk().VerifyCapabilitiesAsync(ws.Root, CancellationToken.None));
        Assert.Contains("global.json", error.Message);
    }

    [Fact]
    public async Task OldSdkVersionIsBlocked()
    {
        var sdk = new SdkClient(new FakeProcessRunner(_ => new ProcessResult(0, "8.0.400\n", "")));
        await Assert.ThrowsAsync<SdkCapabilityException>(() => sdk.VerifyCapabilitiesAsync(Path.GetTempPath(), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJsonFails()
    {
        var sdk = new SdkClient(new FakeProcessRunner(_ => new ProcessResult(0, "this is not json", "")));
        var project = new ProjectDescriptor(Path.Combine(Path.GetTempPath(), "x.csproj"), "x");
        await Assert.ThrowsAsync<SdkOutputException>(() => sdk.EvaluateAsync(project, CancellationToken.None));
    }

    [Fact]
    public async Task CommandFailureCarriesExitCodeAndRedactsSecrets()
    {
        var sdk = new SdkClient(new FakeProcessRunner(_ => new ProcessResult(1, "", "error at https://user:s3cret@feed.example/v3 password=hunter2")));
        var project = new ProjectDescriptor(Path.Combine(Path.GetTempPath(), "x.csproj"), "x");
        var error = await Assert.ThrowsAsync<SdkCommandException>(() => sdk.EvaluateAsync(project, CancellationToken.None));
        Assert.Equal(1, error.ExitCode);
        Assert.DoesNotContain("s3cret", error.Message);
        Assert.DoesNotContain("hunter2", error.Message);
    }
}

public class PackageInventoryTests
{
    private static Workspace WorkspaceOf(TestWorkspace ws, params string[] projects) =>
        new(ws.Root, null, projects.Select(p => new ProjectDescriptor(p, Path.GetFileNameWithoutExtension(p))).ToList());

    [Fact]
    public async Task MultiFrameworkKeepsBothVersions()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        ws.AddPackage("Example.Json", "2.0.0");
        var project = ws.Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><PackageReference Include="Example.Json" Version="1.0.0" /></ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><PackageReference Include="Example.Json" Version="2.0.0" /></ItemGroup>
            </Project>
            """);
        var sdk = ws.CreateSdk();

        var inventory = await sdk.ReadInventoryAsync(WorkspaceOf(ws, project), CancellationToken.None);

        Assert.Empty(inventory.Problems);
        Assert.Equal(2, inventory.Occurrences.Count(p => p.PackageId == "Example.Json"));
        Assert.Contains(inventory.Occurrences, p => p.Framework == "net8.0" && p.ResolvedVersion == "1.0.0");
        Assert.Contains(inventory.Occurrences, p => p.Framework == "net10.0" && p.ResolvedVersion == "2.0.0");

        var evaluation = await sdk.EvaluateAsync(new ProjectDescriptor(project, "Lib"), CancellationToken.None);
        Assert.Equal(new[] { "net10.0", "net8.0" }, evaluation.Frameworks.Order().ToArray());
        Assert.Equal(2, evaluation.References.Count);
    }

    [Fact]
    public async Task CentralReferenceKeepsDeclarationAndVersionSource()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        var centralPropsPath = ws.WriteCentralProps(("Example.Json", "1.0.0"));
        var projectPath = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", null));

        var evaluation = await ws.CreateSdk().EvaluateAsync(new ProjectDescriptor(projectPath, "App"), CancellationToken.None);

        var reference = Assert.Single(evaluation.References);
        Assert.Equal(projectPath, reference.DeclarationPath);
        Assert.Equal(centralPropsPath, reference.CentralVersionPath);
        Assert.Equal("1.0.0", reference.VersionExpression);
        Assert.True(reference.IsWritable, reference.WriteBlockReason);
        Assert.True(evaluation.CentralPackageManagement);
        Assert.Equal(centralPropsPath, evaluation.CentralPackagesFile);
    }

    [Fact]
    public async Task ImportedDirectReferenceIsNotTransitive()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        var props = ws.Write("Directory.Build.props", """
            <Project><ItemGroup><PackageReference Include="Example.Json" Version="1.0.0" /></ItemGroup></Project>
            """);
        var project = ws.WriteProject("App/App.csproj", "net10.0");
        var sdk = ws.CreateSdk();
        var descriptor = new ProjectDescriptor(project, "App");

        var evaluation = await sdk.EvaluateAsync(descriptor, CancellationToken.None);
        var inventory = await sdk.ReadInventoryAsync(WorkspaceOf(ws, project), [evaluation], CancellationToken.None);

        var occurrence = Assert.Single(inventory.Occurrences, p => p.PackageId == "Example.Json");
        Assert.Equal(ReferenceKind.Direct, occurrence.Kind);
        Assert.Equal(props, occurrence.DeclarationPath);
        var reference = Assert.Single(evaluation.References);
        Assert.False(reference.IsWritable);
        Assert.Contains("Directory.Build.props", reference.WriteBlockReason);
    }

    [Fact]
    public async Task ExternallyEditedProjectIsNotReadFromStaleAssets()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        ws.AddPackage("Example.Json", "2.0.0");
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "2.0.0"));

        var inventory = await ws.CreateSdk().ReadInventoryAsync(WorkspaceOf(ws, project), CancellationToken.None);

        Assert.Empty(inventory.Problems);
        Assert.Equal("2.0.0", inventory.Occurrences.Single(p => p.PackageId == "Example.Json").ResolvedVersion);
    }

    [Fact]
    public async Task TransitiveDependencyIsClassified()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        ws.AddPackage("Example.Core", "1.0.0", dependencies: [("net10.0", "Example.Json", "1.0.0")]);
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Core", "1.0.0"));

        var inventory = await ws.CreateSdk().ReadInventoryAsync(WorkspaceOf(ws, project), CancellationToken.None);

        Assert.Equal(ReferenceKind.Direct, inventory.Occurrences.Single(p => p.PackageId == "Example.Core").Kind);
        Assert.Equal(ReferenceKind.Transitive, inventory.Occurrences.Single(p => p.PackageId == "Example.Json").Kind);
    }

    [Fact]
    public async Task SdkImplicitIsNotUserDirect()
    {
        const string json = """
            {"version":1,"parameters":"--include-transitive","projects":[{"path":"C:/p/App.csproj","frameworks":[{"framework":"net10.0",
            "topLevelPackages":[{"id":"Example.Json","requestedVersion":"1.0.0","resolvedVersion":"1.0.0"},
            {"id":"Microsoft.DotNet.ILCompiler","requestedVersion":"[10.0.14, )","resolvedVersion":"10.0.14","autoReferenced":"true"}],
            "transitivePackages":[{"id":"Example.Core","resolvedVersion":"1.0.0"}]}]}]}
            """;
        var sdk = new SdkClient(new FakeProcessRunner(_ => new ProcessResult(0, json, "")));
        var workspace = new Workspace("C:\\p", null, [new ProjectDescriptor("C:\\p\\App.csproj", "App")]);

        var inventory = await sdk.ReadInventoryAsync(workspace, CancellationToken.None);

        Assert.Equal(ReferenceKind.SdkImplicit, inventory.Occurrences.Single(p => p.PackageId == "Microsoft.DotNet.ILCompiler").Kind);
        Assert.Equal(ReferenceKind.Direct, inventory.Occurrences.Single(p => p.PackageId == "Example.Json").Kind);
        Assert.Equal(ReferenceKind.Transitive, inventory.Occurrences.Single(p => p.PackageId == "Example.Core").Kind);
    }

    [Fact]
    public async Task ConditionalOverrideIsNotFlattened()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        ws.AddPackage("Example.Core", "1.0.0");
        ws.WriteCentralProps(("Example.Json", "1.0.0"), ("Example.Core", "1.0.0"));
        var project = ws.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Json" VersionOverride="1.0.0" /></ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><PackageReference Include="Example.Core" /></ItemGroup>
            </Project>
            """);

        var evaluation = await ws.CreateSdk().EvaluateAsync(new ProjectDescriptor(project, "App"), CancellationToken.None);

        var overrides = evaluation.References.Where(r => r.PackageId == "Example.Json").ToList();
        Assert.Equal(2, overrides.Count);
        Assert.All(overrides, r => Assert.Equal("1.0.0", r.VersionOverride));
        Assert.All(overrides, r => Assert.False(r.IsWritable));
        var conditional = Assert.Single(evaluation.References, r => r.PackageId == "Example.Core");
        Assert.Equal("net10.0", conditional.Framework);
        Assert.False(conditional.IsWritable);
    }

    [Fact]
    public async Task ReadOnlyLoadDoesNotRewriteLockFile()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        var project = ws.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Json" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        await ws.RestoreAsync(project);
        var lockPath = ws.Path_("App/packages.lock.json");
        Assert.True(File.Exists(lockPath));
        Directory.Delete(ws.Path_("App/obj"), recursive: true);
        var originalLockBytes = await File.ReadAllBytesAsync(lockPath);
        var originalTime = File.GetLastWriteTimeUtc(lockPath);

        var inventory = await ws.CreateSdk().ReadInventoryAsync(WorkspaceOf(ws, project), CancellationToken.None);

        Assert.Single(inventory.Occurrences);
        Assert.Equal(originalLockBytes, await File.ReadAllBytesAsync(lockPath));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(lockPath));
    }

    [Fact]
    public async Task BrokenProjectIsReportedNotDropped()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Json", "1.0.0");
        var good = ws.WriteProject("Good/Good.csproj", "net10.0", ("Example.Json", "1.0.0"));
        var bad = ws.WriteProject("Bad/Bad.csproj", "net10.0", ("Example.Missing", "9.9.9"));

        var inventory = await ws.CreateSdk().ReadInventoryAsync(WorkspaceOf(ws, good, bad), CancellationToken.None);

        Assert.Contains(inventory.Occurrences, p => p.ProjectPath == good);
        Assert.Contains(inventory.Problems, p => p.ProjectPath == bad);
    }
}
