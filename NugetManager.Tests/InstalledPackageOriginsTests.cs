using NugetManager.Domain;
using NugetManager.Packages;

namespace NugetManager.Tests;

public class InstalledPackageOriginsTests
{
    [Fact]
    public async Task ReadsAssetsFoldersAndNormalizedLibraryPathsWithoutGuessingMissingSources()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var cache = ws.Path_("custom-cache");
        ws.Write("custom-cache\\example.json\\1.0.0\\.nupkg.metadata", """{"source":"https://private.example/v3/index.json"}""");
        var assets = ws.Write("custom-obj\\project.assets.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [cache] = new { } },
            libraries = new Dictionary<string, object> { ["example.json/1.0.0"] = new { type = "package", path = "example.json/1.0.0" } },
        }));
        PackageOccurrence[] items = [
            new("project", "net10.0", "Example.Json", "1.0", "1.0.0", ReferenceKind.Direct, null),
            new("project", "net10.0", "Unknown", "1.0", "1.0.0", ReferenceKind.Transitive, null),
        ];
        var result = await InstalledPackageOrigins.ReadAsync(assets, items, CancellationToken.None);
        Assert.Equal("https://private.example/v3/index.json", result.Occurrences[0].SourceLocation);
        Assert.Null(result.Occurrences[1].SourceLocation);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task CorruptOriginPreservesPackagesAndReportsFailure()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        ws.Write("cache\\example\\1.0.0\\.nupkg.metadata", "{broken");
        var assets = ws.Write("project.assets.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [ws.Path_("cache")] = new { } },
            libraries = new Dictionary<string, object> { ["Example/1.0.0"] = new { type = "package", path = "example/1.0.0" } },
        }));
        var result = await InstalledPackageOrigins.ReadAsync(assets,
            [new("project", "net10.0", "Example", "1.0", "1.0.0", ReferenceKind.Direct, null)], CancellationToken.None);
        Assert.Single(result.Occurrences);
        Assert.Null(result.Occurrences[0].SourceLocation);
        Assert.Single(result.Problems);
    }
}
