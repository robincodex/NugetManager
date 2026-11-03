using System.Text.RegularExpressions;
using System.Xml;
using NugetManager.Domain;

namespace NugetManager.Workspaces;

internal sealed class WorkspaceLoadException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class SolutionSelectionRequiredException(IReadOnlyList<string> solutions)
    : Exception("工作目录包含多个解决方案，请选择一个。")
{
    public IReadOnlyList<string> Solutions { get; } = solutions;
}

internal interface IWorkspaceDiscovery
{
    Task<IReadOnlyList<string>> FindSolutionsAsync(string directory, CancellationToken ct);

    Task<Workspace> LoadAsync(string directory, string? solutionPath, CancellationToken ct);
}

internal sealed partial class WorkspaceDiscovery : IWorkspaceDiscovery
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
    };

    public Task<IReadOnlyList<string>> FindSolutionsAsync(string directory, CancellationToken ct)
    {
        var root = RequireDirectory(directory);
        IReadOnlyList<string> result = Directory.EnumerateFiles(root)
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(PathUtil.Normalize)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task<Workspace> LoadAsync(string directory, string? solutionPath, CancellationToken ct)
    {
        var root = RequireDirectory(directory);
        if (solutionPath is null)
        {
            var solutions = await FindSolutionsAsync(root, ct).ConfigureAwait(false);
            if (solutions.Count > 1)
            {
                throw new SolutionSelectionRequiredException(solutions);
            }
            solutionPath = solutions.Count == 1 ? solutions[0] : null;
        }

        return await Task.Run(() =>
        {
            if (solutionPath is not null)
            {
                var sln = PathUtil.Normalize(solutionPath);
                if (!File.Exists(sln))
                {
                    throw new WorkspaceLoadException($"解决方案不存在：{sln}");
                }
                var projects = sln.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? ReadSlnx(sln) : ReadSln(sln);
                return new Workspace(root, sln, Distinct(projects));
            }
            return new Workspace(root, null, Distinct(Scan(root, ct)));
        }, ct).ConfigureAwait(false);
    }

    private static string RequireDirectory(string directory)
    {
        var root = PathUtil.Normalize(directory);
        if (!Directory.Exists(root))
        {
            throw new WorkspaceLoadException($"工作目录不存在：{root}");
        }
        return root;
    }

    private static List<ProjectDescriptor> Distinct(IEnumerable<string> paths) =>
        paths.Select(PathUtil.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProjectDescriptor(p, Path.GetFileNameWithoutExtension(p)))
            .ToList();

    private static List<string> ReadSlnx(string sln)
    {
        var baseDir = Path.GetDirectoryName(sln)!;
        var result = new List<string>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(sln, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Project")
                {
                    var path = reader.GetAttribute("Path");
                    if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar))));
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            throw new WorkspaceLoadException($"无法读取解决方案 {sln}：{ex.Message}", ex);
        }
        return result;
    }

    [GeneratedRegex("""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,""", RegexOptions.CultureInvariant)]
    private static partial Regex SlnProjectLine();

    private static List<string> ReadSln(string sln)
    {
        var baseDir = Path.GetDirectoryName(sln)!;
        var result = new List<string>();
        foreach (var line in File.ReadLines(sln))
        {
            var match = SlnProjectLine().Match(line.Trim());
            if (match.Success)
            {
                var path = match.Groups["path"].Value;
                if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
        }
        return result;
    }

    private static List<string> Scan(string root, CancellationToken ct)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            if (!visited.Add(PathUtil.Normalize(dir)))
            {
                continue;
            }
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.csproj").ToList();
                dirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            result.AddRange(files);
            foreach (var child in dirs)
            {
                var info = new DirectoryInfo(child);
                if (ExcludedDirectories.Contains(info.Name) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
                pending.Push(child);
            }
        }
        return result;
    }
}
