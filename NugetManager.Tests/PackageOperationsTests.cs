using NugetManager.Presentation;
using System.Text;
using NugetManager.Domain;
using NugetManager.Operations;
using NugetManager.Sdk;

namespace NugetManager.Tests;

/// <summary>Real SDK client with hooks for fault injection around write commands.</summary>
internal sealed class HookedSdk(ISdkClient inner) : ISdkClient
{
    public int AddCalls;
    public int RestoreCalls;
    public Func<int, string, Task>? BeforeAdd;
    public Func<int, string, Task>? AfterAdd;
    public Func<int, Task>? BeforeRestore;
    public Func<int, Task>? AfterRestore;

    public IReadOnlyDictionary<string, string?>? Environment => inner.Environment;

    public Task<string> VerifyCapabilitiesAsync(string directory, CancellationToken ct) => inner.VerifyCapabilitiesAsync(directory, ct);

    public Task<ProjectEvaluation> EvaluateAsync(ProjectDescriptor project, CancellationToken ct) => inner.EvaluateAsync(project, ct);

    public Task<PackageInventory> ReadInventoryAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation>? evaluations, CancellationToken ct) =>
        inner.ReadInventoryAsync(workspace, evaluations, ct);

    public async Task RestoreAsync(IReadOnlyList<ProjectDescriptor> projects, bool lockedMode, CancellationToken ct)
    {
        var call = ++RestoreCalls;
        if (BeforeRestore is not null)
        {
            await BeforeRestore(call);
        }
        await inner.RestoreAsync(projects, lockedMode, ct);
        if (AfterRestore is not null)
        {
            await AfterRestore(call);
        }
    }

    public Task<PackageListOutput> ListOutdatedAsync(ProjectDescriptor project, bool includePrerelease, CancellationToken ct) =>
        inner.ListOutdatedAsync(project, includePrerelease, ct);

    public Task RestorePreservingLockFilesAsync(IReadOnlyList<ProjectEvaluation> evaluations, CancellationToken ct) =>
        inner.RestorePreservingLockFilesAsync(evaluations, ct);

    public Task<SearchOutput> SearchAsync(string workingDirectory, string query, int skip, int take, bool includePrerelease, bool exactMatch, string? source, CancellationToken ct) =>
        inner.SearchAsync(workingDirectory, query, skip, take, includePrerelease, exactMatch, source, ct);

    public async Task AddPackageAsync(string projectPath, string packageId, string version, CancellationToken ct)
    {
        var call = ++AddCalls;
        if (BeforeAdd is not null)
        {
            await BeforeAdd(call, projectPath);
        }
        await inner.AddPackageAsync(projectPath, packageId, version, ct);
        if (AfterAdd is not null)
        {
            await AfterAdd(call, projectPath);
        }
    }

    public Task RemovePackageAsync(string projectPath, string packageId, CancellationToken ct) => inner.RemovePackageAsync(projectPath, packageId, ct);

    public Task<string?> GetGlobalPackagesFolderAsync(string directory, CancellationToken ct) => inner.GetGlobalPackagesFolderAsync(directory, ct);
}

public class PackageOperationsTests
{
    private static readonly IProgress<UiMessage> NoProgress = new Progress<UiMessage>();

    private static Workspace WorkspaceOf(TestWorkspace ws, params string[] projects) =>
        new(ws.Root, null, projects.Select(p => new ProjectDescriptor(p, Path.GetFileNameWithoutExtension(p))).ToList());

    private static async Task<TestWorkspace> FeedAsync()
    {
        var ws = await TestWorkspace.CreateAsync();
        foreach (var v in new[] { "1.0.0", "2.0.0", "3.0.0" })
        {
            ws.AddPackage("Example.Json", v);
            ws.AddPackage("Example.Core", v);
        }
        return ws;
    }

    private static async Task<(string Props, string Api, string Core)> CentralAsync(TestWorkspace ws)
    {
        var props = ws.WriteCentralProps(("Example.Json", "1.0.0"), ("Example.Core", "1.0.0"));
        var api = ws.WriteProject("Api/Api.csproj", "net10.0", ("Example.Json", null), ("Example.Core", null));
        var core = ws.WriteProject("Core/Core.csproj", "net8.0;net10.0", ("Example.Json", null));
        await ws.RestoreAsync(api);
        await ws.RestoreAsync(core);
        return (props, api, core);
    }

    private static PackageOperations Operations(TestWorkspace ws, ISdkClient? sdk = null) =>
        new(sdk ?? ws.CreateSdk(), Path.Combine(ws.Root, ".recovery"));

    private static async Task<string?> ResolvedAsync(TestWorkspace ws, string project, string id)
    {
        var inventory = await ws.CreateSdk().ReadInventoryAsync(WorkspaceOf(ws, project), null, CancellationToken.None);
        return inventory.Occurrences.FirstOrDefault(o => o.PackageId == id && o.Kind == ReferenceKind.Direct)?.ResolvedVersion;
    }

    [Fact]
    public async Task CentralBatchWritesEntryOnce()
    {
        await using var ws = await FeedAsync();
        var (props, api, core) = await CentralAsync(ws);
        var apiBytes = await File.ReadAllBytesAsync(api);
        var ops = Operations(ws);

        var plan = await ops.PlanAsync(WorkspaceOf(ws, api, core),
            [new(PackageAction.Upgrade, api, "Example.Json", "3.0.0"), new(PackageAction.Upgrade, core, "Example.Json", "3.0.0")], CancellationToken.None);
        var result = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);

        Assert.Empty(plan.Blockers);
        Assert.Single(plan.Commands);
        Assert.Equal(OperationStatus.Succeeded, result.Status);
        var text = await File.ReadAllTextAsync(props);
        Assert.Single(text.Split("Include=\"Example.Json\"").Skip(1));
        Assert.Contains("Include=\"Example.Json\" Version=\"3.0.0\"", text);
        Assert.Equal(apiBytes, await File.ReadAllBytesAsync(api));
        Assert.Equal("3.0.0", await ResolvedAsync(ws, core, "Example.Json"));
    }

    [Fact]
    public async Task SingleProjectCentralUpdateRestoresAllAffected()
    {
        await using var ws = await FeedAsync();
        var (_, apiPath, corePath) = await CentralAsync(ws);

        var plan = await Operations(ws).PlanAsync(WorkspaceOf(ws, apiPath, corePath),
            [new(PackageAction.Upgrade, apiPath, "Example.Json", "3.0.0")], CancellationToken.None);

        Assert.Equal(new[] { apiPath, corePath }.Order().ToArray(), plan.AffectedProjects.Select(p => p.Path).Order().ToArray());
        Assert.Contains(plan.Warnings, w => TestMessages.Zh(w)!.Contains("Core", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => TestMessages.Zh(w)!.Contains("主版本", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallTransitiveAddsDirectReference()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.AddPackage("Example.Core", "1.0.0");
        ws.AddPackage("Example.Core", "2.0.0");
        ws.AddPackage("Example.Json", "1.0.0", dependencies: [("net10.0", "Example.Core", "1.0.0"), ("net8.0", "Example.Core", "1.0.0")]);
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var ops = Operations(ws);

        var plan = await ops.PlanAsync(WorkspaceOf(ws, project), [new(PackageAction.Install, project, "Example.Core", "2.0.0")], CancellationToken.None);
        var result = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Contains("Include=\"Example.Core\" Version=\"2.0.0\"", await File.ReadAllTextAsync(project));
        Assert.Equal("2.0.0", await ResolvedAsync(ws, project, "Example.Core"));
    }

    [Fact]
    public async Task UninstallKeepsSharedVersionEntry()
    {
        await using var ws = await FeedAsync();
        var (props, api, core) = await CentralAsync(ws);
        var ops = Operations(ws);

        var plan = await ops.PlanAsync(WorkspaceOf(ws, api, core), [new(PackageAction.Uninstall, api, "Example.Json", null)], CancellationToken.None);
        var result = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.DoesNotContain("Example.Json", await File.ReadAllTextAsync(api));
        Assert.Contains("Include=\"Example.Json\" Version=\"1.0.0\"", await File.ReadAllTextAsync(props));
        Assert.Contains("Example.Json", await File.ReadAllTextAsync(core));
    }

    [Fact]
    public async Task ConditionalOverrideDoesNotCreateUnconditionalDuplicate()
    {
        await using var ws = await FeedAsync();
        var project = ws.Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Example.Json" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        await ws.RestoreAsync(project);
        var original = await File.ReadAllBytesAsync(project);
        var ops = Operations(ws);

        var upgrade = await ops.PlanAsync(WorkspaceOf(ws, project), [new(PackageAction.Upgrade, project, "Example.Json", "3.0.0")], CancellationToken.None);
        var install = await ops.PlanAsync(WorkspaceOf(ws, project), [new(PackageAction.Install, project, "Example.Json", "3.0.0")], CancellationToken.None);

        Assert.NotEmpty(upgrade.Blockers);
        Assert.NotEmpty(install.Blockers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.ExecuteAsync(upgrade, NoProgress, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(project));
    }

    [Fact]
    public async Task PreflightDetectsStalePreview()
    {
        await using var ws = await FeedAsync();
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var ops = Operations(ws);
        var plan = await ops.PlanAsync(WorkspaceOf(ws, project), [new(PackageAction.Upgrade, project, "Example.Json", "3.0.0")], CancellationToken.None);

        var edited = (await File.ReadAllTextAsync(project)).Replace("</Project>", "<!-- edited after preview --></Project>");
        await File.WriteAllTextAsync(project, edited);

        await Assert.ThrowsAsync<PlanStaleException>(() => ops.ExecuteAsync(plan, NoProgress, CancellationToken.None));
        Assert.Equal(edited, await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task SecondCommandFailureRollsBackFirst()
    {
        await using var ws = await FeedAsync();
        var (centralPropsPath, api, core) = await CentralAsync(ws);
        var originalPropsBytes = await File.ReadAllBytesAsync(centralPropsPath);
        var sdk = new HookedSdk(ws.CreateSdk())
        {
            BeforeAdd = (call, _) => call == 2 ? throw new SdkCommandException("package add", 1, "injected failure") : Task.CompletedTask,
        };
        var ops = Operations(ws, sdk);

        var plan = await ops.PlanAsync(WorkspaceOf(ws, api, core),
            [new(PackageAction.Upgrade, api, "Example.Json", "3.0.0"), new(PackageAction.Upgrade, api, "Example.Core", "3.0.0")], CancellationToken.None);
        var result = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);

        Assert.Equal(2, sdk.AddCalls);
        Assert.Equal(OperationStatus.RolledBack, result.Status);
        Assert.Contains("injected failure", TestMessages.Zh(result.Error));
        Assert.Equal(originalPropsBytes, await File.ReadAllBytesAsync(centralPropsPath));
        Assert.Equal("1.0.0", await ResolvedAsync(ws, core, "Example.Json"));
    }

    [Fact]
    public async Task RestoreFailureRollsBackBatch()
    {
        await using var ws = await FeedAsync();
        var a = ws.WriteProject("A/A.csproj", "net10.0", ("Example.Json", "1.0.0"));
        var b = ws.WriteProject("B/B.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(a);
        await ws.RestoreAsync(b);
        var originalA = await File.ReadAllBytesAsync(a);
        var originalB = await File.ReadAllBytesAsync(b);
        var sdk = new HookedSdk(ws.CreateSdk())
        {
            AfterRestore = call => call == 1 ? throw new SdkCommandException("restore", 1, "restore exploded") : Task.CompletedTask,
        };
        var ops = Operations(ws, sdk);

        var plan = await ops.PlanAsync(WorkspaceOf(ws, a, b),
            [new(PackageAction.Upgrade, a, "Example.Json", "3.0.0"), new(PackageAction.Upgrade, b, "Example.Json", "3.0.0")], CancellationToken.None);
        var result = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);

        Assert.Equal(OperationStatus.RolledBack, result.Status);
        Assert.Equal(originalA, await File.ReadAllBytesAsync(a));
        Assert.Equal(originalB, await File.ReadAllBytesAsync(b));
        Assert.Equal("1.0.0", await ResolvedAsync(ws, a, "Example.Json"));
    }

    [Fact]
    public async Task CancellationRollsBackWithFreshToken()
    {
        await using var ws = await FeedAsync();
        var a = ws.WriteProject("A/A.csproj", "net10.0", ("Example.Json", "1.0.0"));
        var b = ws.WriteProject("B/B.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(a);
        await ws.RestoreAsync(b);
        var originalA = await File.ReadAllBytesAsync(a);
        using var cts = new CancellationTokenSource();
        var sdk = new HookedSdk(ws.CreateSdk())
        {
            AfterAdd = (call, _) =>
            {
                if (call == 1)
                {
                    cts.Cancel();
                }
                return Task.CompletedTask;
            },
        };
        var ops = Operations(ws, sdk);
        var plan = await ops.PlanAsync(WorkspaceOf(ws, a, b),
            [new(PackageAction.Upgrade, a, "Example.Json", "3.0.0"), new(PackageAction.Upgrade, b, "Example.Json", "3.0.0")], CancellationToken.None);

        var result = await ops.ExecuteAsync(plan, NoProgress, cts.Token);

        Assert.Equal(OperationStatus.RolledBack, result.Status);
        Assert.Contains("取消", TestMessages.Zh(result.Error));
        Assert.Equal(originalA, await File.ReadAllBytesAsync(a));
    }

    [Fact]
    public async Task ConflictReturnsRecoveryRequired()
    {
        await using var ws = await FeedAsync();
        var project = ws.WriteProject("App/App.csproj", "net10.0", ("Example.Json", "1.0.0"));
        await ws.RestoreAsync(project);
        var sdk = new HookedSdk(ws.CreateSdk())
        {
            AfterAdd = async (_, path) =>
            {
                var text = await File.ReadAllTextAsync(path);
                await File.WriteAllTextAsync(path, text.Replace("</Project>", "  <PropertyGroup><Nullable>enable</Nullable></PropertyGroup>\n</Project>"));
            },
        };
        var ops = Operations(ws, sdk);
        var plan = await ops.PlanAsync(WorkspaceOf(ws, project), [new(PackageAction.Upgrade, project, "Example.Json", "3.0.0")], CancellationToken.None);

        var result = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);

        Assert.Equal(OperationStatus.RecoveryRequired, result.Status);
        Assert.Contains(result.Recovery, r => r.Path == project && r.Status == RecoveryStatus.Conflict);
        Assert.Contains("<Nullable>enable</Nullable>", await File.ReadAllTextAsync(project));
        Assert.NotNull(result.RecoveryManifestPath);
        Assert.True(File.Exists(result.RecoveryManifestPath));
    }

    [Fact]
    public async Task LockFileChangesUseSameTransaction()
    {
        await using var ws = await FeedAsync();
        string LockProject(string name) => ws.Write($"{name}/{name}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Example.Json" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        var existing = LockProject("Locked");
        var fresh = LockProject("Fresh");
        await ws.RestoreAsync(existing);
        var existingLock = Path.Combine(Path.GetDirectoryName(existing)!, "packages.lock.json");
        var freshLock = Path.Combine(Path.GetDirectoryName(fresh)!, "packages.lock.json");
        var originalLock = await File.ReadAllBytesAsync(existingLock);
        Assert.False(File.Exists(freshLock));
        var sdk = new HookedSdk(ws.CreateSdk())
        {
            AfterRestore = call => call == 1 ? throw new SdkCommandException("restore", 1, "late failure") : Task.CompletedTask,
        };
        var ops = Operations(ws, sdk);

        var plan = await ops.PlanAsync(WorkspaceOf(ws, existing, fresh),
            [new(PackageAction.Upgrade, existing, "Example.Json", "3.0.0"), new(PackageAction.Upgrade, fresh, "Example.Json", "3.0.0")], CancellationToken.None);
        Assert.Contains(existingLock, plan.Files);
        Assert.Contains(freshLock, plan.Files);

        var failed = await ops.ExecuteAsync(plan, NoProgress, CancellationToken.None);
        Assert.Equal(OperationStatus.RolledBack, failed.Status);
        Assert.Equal(originalLock, await File.ReadAllBytesAsync(existingLock));
        Assert.False(File.Exists(freshLock));

        var ok = Operations(ws);
        var retry = await ok.PlanAsync(WorkspaceOf(ws, existing), [new(PackageAction.Upgrade, existing, "Example.Json", "3.0.0")], CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, (await ok.ExecuteAsync(retry, NoProgress, CancellationToken.None)).Status);
        Assert.Contains("3.0.0", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(existingLock)));
    }
}
