using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using NugetManager.Sdk;

namespace NugetManager.Tests;

/// <summary>
/// Isolated temporary directory with its own NuGet.Config, local feed and global packages folder.
/// Only the directory created here is ever deleted.
/// </summary>
internal sealed class TestWorkspace : IAsyncDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
        FeedDirectory = Path.Combine(root, "feed");
        PackagesDirectory = Path.Combine(root, ".gpf");
        Directory.CreateDirectory(FeedDirectory);
        Directory.CreateDirectory(PackagesDirectory);
    }

    public string Root { get; }
    public string FeedDirectory { get; }
    public string PackagesDirectory { get; }

    public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?>
    {
        ["NUGET_PACKAGES"] = PackagesDirectory,
        ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Root, ".http-cache"),
        ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(Root, ".plugins-cache"),
    };

    public static Task<TestWorkspace> CreateAsync(bool withFeedConfig = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "nm-tests", Guid.NewGuid().ToString("N")[..12] + " ws");
        Directory.CreateDirectory(root);
        var ws = new TestWorkspace(Path.GetFullPath(root));
        if (withFeedConfig)
        {
            ws.WriteNuGetConfig();
        }
        return Task.FromResult(ws);
    }

    public SdkClient CreateSdk() => new(new ProcessRunner(), Environment);

    public void WriteNuGetConfig(string? extraSources = null) => Write("NuGet.Config",
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="local" value="{FeedDirectory}" />
            {extraSources}
          </packageSources>
        </configuration>
        """);

    public string Write(string relativePath, string content)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string Path_(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

    public string WriteProject(string relativePath, string frameworks, params (string Id, string? Version)[] packages)
    {
        var tfm = frameworks.Contains(';') ? $"<TargetFrameworks>{frameworks}</TargetFrameworks>" : $"<TargetFramework>{frameworks}</TargetFramework>";
        var refs = string.Concat(packages.Select(p => p.Version is null
            ? $"\n    <PackageReference Include=\"{p.Id}\" />"
            : $"\n    <PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" />"));
        return Write(relativePath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {tfm}
              </PropertyGroup>
              <ItemGroup>{refs}
              </ItemGroup>
            </Project>
            """);
    }

    public string WriteCentralProps(params (string Id, string Version)[] packages)
    {
        var items = string.Concat(packages.Select(p => $"\n    <PackageVersion Include=\"{p.Id}\" Version=\"{p.Version}\" />"));
        return Write("Directory.Packages.props", $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>{items}
              </ItemGroup>
            </Project>
            """);
    }

    /// <summary>Creates a minimal package in the local feed.</summary>
    public string AddPackage(string id, string version, string[]? frameworks = null,
        (string Framework, string Id, string Range)[]? dependencies = null, string? description = null,
        string? authors = "Test Author", string? license = "MIT", string? projectUrl = null)
    {
        frameworks ??= ["net8.0", "net10.0"];
        var path = Path.Combine(FeedDirectory, $"{id}.{version}.nupkg");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var groups = new StringBuilder();
        foreach (var fw in frameworks)
        {
            groups.Append($"<group targetFramework=\"{fw}\">");
            foreach (var dep in dependencies?.Where(d => d.Framework == fw) ?? [])
            {
                groups.Append($"<dependency id=\"{dep.Id}\" version=\"{dep.Range}\" />");
            }
            groups.Append("</group>");
        }
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                {(authors is null ? "" : $"<authors>{authors}</authors>")}
                <description>{description ?? "Test package " + id}</description>
                {(license is null ? "" : $"<license type=\"expression\">{license}</license>")}
                {(projectUrl is null ? "" : $"<projectUrl>{projectUrl}</projectUrl>")}
                <dependencies>{groups}</dependencies>
              </metadata>
            </package>
            """;
        WriteEntry(zip, $"{id}.nuspec", nuspec);
        foreach (var fw in frameworks)
        {
            WriteEntry(zip, $"lib/{fw}/_._", "");
        }
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public async Task RestoreAsync(string projectOrDirectory)
    {
        var result = await new ProcessRunner().RunAsync(new ProcessRequest("dotnet",
            ["restore", projectOrDirectory], Root, TimeSpan.FromMinutes(3), Environment), CancellationToken.None);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"restore failed: {result.Stdout}\n{result.Stderr}");
        }
    }

    public static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            RemoveJunctions(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }

    private static void RemoveJunctions(string directory)
    {
        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete();
            }
            else
            {
                RemoveJunctions(dir);
            }
        }
    }
}
