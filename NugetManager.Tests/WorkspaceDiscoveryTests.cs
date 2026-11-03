using NugetManager.Workspaces;

namespace NugetManager.Tests;

public class WorkspaceDiscoveryTests
{
    [Fact]
    public async Task SlnxIncludesExternalProject()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var inside = ws.WriteProject("repo/src/App/App.csproj", "net10.0");
        var externalProjectPath = ws.WriteProject("shared lib/Lib.csproj", "net10.0");
        ws.Write("repo/App.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/App/App.csproj" />
              </Folder>
              <Project Path="../shared lib/Lib.csproj" />
            </Solution>
            """);

        var result = await new WorkspaceDiscovery().LoadAsync(ws.Path_("repo"), null, CancellationToken.None);

        Assert.Contains(result.Projects, p => p.Path == externalProjectPath);
        Assert.Contains(result.Projects, p => p.Path == inside);
        Assert.Equal(ws.Path_("repo/App.slnx"), result.SolutionPath);
    }

    [Fact]
    public async Task SolutionWinsOverLooseProjects()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var app = ws.WriteProject("App/App.csproj", "net10.0");
        var looseProjectPath = ws.WriteProject("Loose/Loose.csproj", "net10.0");
        ws.Write("Only.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder", "Folder", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var result = await new WorkspaceDiscovery().LoadAsync(ws.Root, null, CancellationToken.None);

        Assert.Equal(app, Assert.Single(result.Projects).Path);
        Assert.DoesNotContain(result.Projects, p => p.Path == looseProjectPath);
    }

    [Fact]
    public async Task DirectoryScanDeduplicatesAndSkipsLinks()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var a = ws.WriteProject("src/A/A.csproj", "net10.0");
        ws.WriteProject("src/A/bin/Debug/Copy.csproj", "net10.0");
        ws.WriteProject("src/A/obj/Gen.csproj", "net10.0");
        ws.WriteProject(".git/x/X.csproj", "net10.0");
        var b = ws.WriteProject("src/B/B.csproj", "net10.0");

        var linkCreated = TestWorkspace.TryCreateJunction(ws.Path_("src/B/loop"), ws.Path_("src"));
        Assert.True(linkCreated, "Directory junctions must be creatable on Windows without elevation.");

        var result = await new WorkspaceDiscovery().LoadAsync(ws.Root, null, CancellationToken.None);

        Assert.Equal(new[] { a, b }, result.Projects.Select(p => p.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Null(result.SolutionPath);
    }

    [Fact]
    public async Task MultipleSolutionsRequireSelection()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.WriteProject("A/A.csproj", "net10.0");
        ws.Write("One.slnx", "<Solution><Project Path=\"A/A.csproj\" /></Solution>");
        ws.Write("Two.slnx", "<Solution />");
        var discovery = new WorkspaceDiscovery();

        var solutions = await discovery.FindSolutionsAsync(ws.Root, CancellationToken.None);
        Assert.Equal(2, solutions.Count);

        var error = await Assert.ThrowsAsync<SolutionSelectionRequiredException>(
            () => discovery.LoadAsync(ws.Root, null, CancellationToken.None));
        Assert.Equal(2, error.Solutions.Count);

        var chosen = await discovery.LoadAsync(ws.Root, solutions.First(s => s.EndsWith("One.slnx")), CancellationToken.None);
        Assert.Single(chosen.Projects);
    }

    [Fact]
    public async Task SlnxRejectsExternalEntities()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.Write("Evil.slnx", """
            <?xml version="1.0"?>
            <!DOCTYPE Solution [ <!ENTITY x SYSTEM "file:///c:/windows/win.ini"> ]>
            <Solution><Project Path="&x;" /></Solution>
            """);

        await Assert.ThrowsAsync<WorkspaceLoadException>(
            () => new WorkspaceDiscovery().LoadAsync(ws.Root, null, CancellationToken.None));
    }
}
