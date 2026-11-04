using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NugetManager.Domain;
using NugetManager.Packages;

namespace NugetManager.Sdk;

internal sealed class SdkCommandException(string command, int exitCode, string output)
    : Exception(MF(TextKey.M224, $"命令失败（退出码 {exitCode}）：dotnet {command}\n{output}").ToString().TrimEnd()), IUiException
{
    public UiMessage UiMessage { get; } = MF(TextKey.M224, $"命令失败（退出码 {exitCode}）：dotnet {command}\n{output.TrimEnd()}");
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}

internal sealed class SdkOutputException(UiMessage message, Exception? inner = null) : Exception(message.ToString(), inner), IUiException
{
    public UiMessage UiMessage { get; } = message;
}

internal sealed class SdkCapabilityException(UiMessage message, Exception? inner = null) : Exception(message.ToString(), inner), IUiException
{
    public UiMessage UiMessage { get; } = message;
}

internal interface ISdkClient
{
    IReadOnlyDictionary<string, string?>? Environment { get; }

    Task<string> VerifyCapabilitiesAsync(string directory, CancellationToken ct);

    Task<ProjectEvaluation> EvaluateAsync(ProjectDescriptor project, CancellationToken ct);

    Task<PackageInventory> ReadInventoryAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation>? evaluations, CancellationToken ct);

    Task RestoreAsync(IReadOnlyList<ProjectDescriptor> projects, bool lockedMode, CancellationToken ct);

    /// <summary>Restores without creating or rewriting lock files (locked mode when one exists).</summary>
    Task RestorePreservingLockFilesAsync(IReadOnlyList<ProjectEvaluation> evaluations, CancellationToken ct);

    Task<PackageListOutput> ListOutdatedAsync(ProjectDescriptor project, bool includePrerelease, CancellationToken ct);

    Task<SearchOutput> SearchAsync(string workingDirectory, string query, int skip, int take, bool includePrerelease, bool exactMatch, string? source, CancellationToken ct);

    Task AddPackageAsync(string projectPath, string packageId, string version, CancellationToken ct);

    Task RemovePackageAsync(string projectPath, string packageId, CancellationToken ct);

    Task<string?> GetGlobalPackagesFolderAsync(string directory, CancellationToken ct);
}

internal sealed partial class SdkClient : ISdkClient
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(10);
    private const int MaxParallel = 4;

    public IReadOnlyDictionary<string, string?>? Environment => _environment;

    public async Task<string> VerifyCapabilitiesAsync(string directory, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await RunRawAsync(directory, ["--version"], ShortTimeout, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            throw new SdkCapabilityException(M(TextKey.M225), ex);
        }
        var version = result.Stdout.Trim();
        if (result.ExitCode != 0 || !NuGetVersionComparer.TryParse(version, out var parsed))
        {
            var detail = Redact((result.Stdout + "\n" + result.Stderr).Trim());
            throw new SdkCapabilityException(
                MF(TextKey.M226, $"无法在此工作目录解析可用的 .NET SDK（可能由 global.json 固定了未安装的版本）。需要具备 .NET 10 命令能力的 SDK。\n{detail}"));
        }
        if (parsed.Major < 10)
        {
            throw new SdkCapabilityException(
                MF(TextKey.M227, $"当前工作目录使用的 .NET SDK 为 {version}（受 global.json 影响时以其为准），需要 .NET 10 或更高版本的 SDK 才能使用 package list/search/add/remove 的结构化输出。"));
        }
        return version;
    }

    public async Task<ProjectEvaluation> EvaluateAsync(ProjectDescriptor project, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(project.Path)!;
        string[] outerArgs =
        [
            "msbuild", project.Path, "-nologo",
            "-getProperty:TargetFramework", "-getProperty:TargetFrameworks",
            "-getProperty:ManagePackageVersionsCentrally", "-getProperty:DirectoryPackagesPropsPath",
            "-getProperty:NuGetLockFilePath", "-getProperty:RestorePackagesWithLockFile",
            "-getProperty:ProjectAssetsFile",
            "-getItem:PackageReference", "-getItem:PackageVersion",
        ];
        var outer = await RunJsonAsync(dir, outerArgs, EvalTimeout, SdkJsonContext.Default.MsBuildOutput, ct).ConfigureAwait(false);
        var props = outer.Properties ?? [];
        var frameworks = Split(Get(props, "TargetFrameworks"));
        if (frameworks.Count == 0)
        {
            frameworks = Split(Get(props, "TargetFramework"));
        }
        if (frameworks.Count == 0)
        {
            throw new SdkOutputException(MF(TextKey.M228, $"无法确定项目 {project.Name} 的目标框架。"));
        }

        var perFramework = new List<(string Framework, MsBuildOutput Output)>();
        if (frameworks.Count == 1)
        {
            perFramework.Add((frameworks[0], outer));
        }
        else
        {
            foreach (var fw in frameworks)
            {
                string[] args =
                [
                    "msbuild", project.Path, "-nologo", $"-p:TargetFramework={fw}",
                    "-getProperty:ManagePackageVersionsCentrally",
                    "-getItem:PackageReference", "-getItem:PackageVersion",
                ];
                perFramework.Add((fw, await RunJsonAsync(dir, args, EvalTimeout, SdkJsonContext.Default.MsBuildOutput, ct).ConfigureAwait(false)));
            }
        }

        var cpm = IsTrue(Get(props, "ManagePackageVersionsCentrally"));
        var centralFile = NullIfEmpty(Get(props, "DirectoryPackagesPropsPath"));
        var inspector = new DeclarationInspector();
        var references = new List<EvaluatedReference>();
        foreach (var (fw, output) in perFramework)
        {
            var fwCpm = IsTrue(Get(output.Properties ?? props, "ManagePackageVersionsCentrally"));
            var versions = Items(output, "PackageVersion")
                .GroupBy(i => Get(i, "Identity") ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items(output, "PackageReference"))
            {
                var id = Get(item, "Identity");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                references.Add(BuildReference(project, fw, fwCpm, id, item, versions, inspector));
            }
        }

        var lockPath = NullIfEmpty(Get(props, "NuGetLockFilePath"));
        lockPath = lockPath is null ? Path.Combine(dir, "packages.lock.json") : Path.GetFullPath(Path.Combine(dir, lockPath));
        var lockFiles = File.Exists(lockPath) || IsTrue(Get(props, "RestorePackagesWithLockFile")) ? new[] { lockPath } : [];

        var central = Items(outer, "PackageVersion")
            .Where(i => !string.IsNullOrEmpty(Get(i, "Identity")))
            .GroupBy(i => Get(i, "Identity")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Normalize(Get(g.Last(), "DefiningProjectFullPath")) ?? "", StringComparer.OrdinalIgnoreCase);

        return new ProjectEvaluation(project, frameworks, references, lockFiles)
        {
            CentralPackageManagement = cpm,
            CentralPackagesFile = cpm && centralFile is not null ? Path.GetFullPath(centralFile) : null,
            CentralVersions = central,
            AssetsFile = NullIfEmpty(Get(props, "ProjectAssetsFile")),
        };
    }

    private static EvaluatedReference BuildReference(ProjectDescriptor project, string framework, bool cpm, string id,
        Dictionary<string, string> item, Dictionary<string, Dictionary<string, string>> versions, DeclarationInspector inspector)
    {
        var declaration = Normalize(Get(item, "DefiningProjectFullPath")) ?? project.Path;
        var versionOverride = NullIfEmpty(Get(item, "VersionOverride"));
        var itemVersion = NullIfEmpty(Get(item, "Version"));
        string? centralPath = null;
        var expression = itemVersion;
        if (cpm && versionOverride is null && versions.TryGetValue(id, out var pv))
        {
            centralPath = Normalize(Get(pv, "DefiningProjectFullPath"));
            expression = NullIfEmpty(Get(pv, "Version"));
        }
        else if (versionOverride is not null)
        {
            expression = versionOverride;
        }

        UiMessage? block = null;
        if (IsTrue(Get(item, "IsImplicitlyDefined")))
        {
            block = M(TextKey.M229);
        }
        else if (!PathUtil.AreEqual(declaration, project.Path))
        {
            block = MF(TextKey.M230, $"引用声明位于导入文件 {Path.GetFileName(declaration)}（{declaration}），为避免产生重复引用，请在该文件中手动修改。");
        }
        else if (versionOverride is not null)
        {
            block = M(TextKey.M231);
        }
        else
        {
            var raw = inspector.Inspect(declaration, "PackageReference", id);
            if (raw.Problem is not null)
            {
                block = raw.Problem;
            }
            else if (cpm)
            {
                if (centralPath is null)
                {
                    block = M(TextKey.M232);
                }
                else if (raw.HasVersion)
                {
                    block = M(TextKey.M233);
                }
                else
                {
                    var centralRaw = inspector.Inspect(centralPath, "PackageVersion", id);
                    block = centralRaw.Problem is null ? null : M(TextKey.M234) + centralRaw.Problem;
                }
            }
        }

        return new EvaluatedReference(id, framework, declaration, centralPath, expression, versionOverride, block is null, block)
        {
            ProjectPath = project.Path,
        };
    }

    public Task<PackageInventory> ReadInventoryAsync(Workspace workspace, CancellationToken ct) =>
        ReadInventoryAsync(workspace, null, ct);

    public async Task<PackageInventory> ReadInventoryAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation>? evaluations, CancellationToken ct)
    {
        var results = new (List<PackageOccurrence>? Items, UiMessage? Error)[workspace.Projects.Count];

        // Always restore first: "package list --no-restore" happily reports stale assets after an
        // external edit, and a restore whose inputs are unchanged is a cheap no-op. Restores run
        // sequentially because projects may share referenced projects' obj folders.
        var restoreErrors = new UiMessage?[workspace.Projects.Count];
        for (var i = 0; i < restoreErrors.Length; i++)
        {
            var project = workspace.Projects[i];
            var evaluation = evaluations?.FirstOrDefault(e => PathUtil.AreEqual(e.Project.Path, project.Path));
            try
            {
                await ReadOnlyRestoreAsync(project, evaluation, ct).ConfigureAwait(false);
            }
            catch (SdkCommandException ex)
            {
                restoreErrors[i] = UiMessage.FromException(ex);
            }
        }

        await ParallelForAsync(workspace.Projects.Count, async i =>
        {
            results[i] = restoreErrors[i] is { } error
                ? (null, error)
                : await TryListAsync(workspace.Projects[i], ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var declarations = new Dictionary<(string, string, string), string>();
        foreach (var e in evaluations ?? [])
        {
            foreach (var r in e.References)
            {
                declarations[(e.Project.Path.ToUpperInvariant(), r.Framework.ToUpperInvariant(), r.PackageId.ToUpperInvariant())] = r.DeclarationPath;
            }
        }

        var occurrences = new List<PackageOccurrence>();
        var problems = new List<ProjectProblem>();
        for (var i = 0; i < results.Length; i++)
        {
            var project = workspace.Projects[i];
            if (results[i].Items is { } items)
            {
                try
                {
                    var evaluation = evaluations?.FirstOrDefault(e => PathUtil.AreEqual(e.Project.Path, project.Path));
                    var assetsFile = evaluation?.AssetsFile;
                    if (assetsFile is null)
                    {
                        var evaluated = await RunJsonAsync(Path.GetDirectoryName(project.Path)!,
                            ["msbuild", project.Path, "-nologo", "-getProperty:ProjectAssetsFile", "-getProperty:MSBuildProjectFullPath"],
                            EvalTimeout, SdkJsonContext.Default.MsBuildOutput, ct).ConfigureAwait(false);
                        assetsFile = NullIfEmpty(Get(evaluated.Properties ?? [], "ProjectAssetsFile"));
                    }
                    if (assetsFile is not null)
                    {
                        var origins = await InstalledPackageOrigins.ReadAsync(
                            Path.GetFullPath(assetsFile, Path.GetDirectoryName(project.Path)!), items, ct).ConfigureAwait(false);
                        items = origins.Occurrences.ToList();
                        problems.AddRange(origins.Problems);
                    }
                }
                catch (Exception ex) when (ex is SdkCommandException or SdkOutputException or ProcessTimeoutException
                    or ProcessStartException or ArgumentException or NotSupportedException)
                {
                    problems.Add(new(project.Path, MF(TextKey.M050, $"无法确认缓存来源：{UiMessage.FromException(ex)}")));
                }
                occurrences.AddRange(items.Select(o => o.Kind == ReferenceKind.Direct
                    && declarations.TryGetValue((project.Path.ToUpperInvariant(), o.Framework.ToUpperInvariant(), o.PackageId.ToUpperInvariant()), out var decl)
                    ? o with { DeclarationPath = decl } : o));
            }
            else
            {
                problems.Add(new ProjectProblem(project.Path, results[i].Error ?? M(TextKey.M235)));
            }
        }
        return new PackageInventory(occurrences) { Problems = problems };
    }

    private async Task<(List<PackageOccurrence>? Items, UiMessage? Error)> TryListAsync(ProjectDescriptor project, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(project.Path)!;
        var result = await RunRawAsync(dir,
            ["package", "list", "--project", project.Path, "--include-transitive", "--no-restore", "--format", "json", "--output-version", "1"],
            LongTimeout, ct).ConfigureAwait(false);
        PackageListOutput? output = TryParse(result.Stdout, SdkJsonContext.Default.PackageListOutput);
        if (output is null)
        {
            if (result.ExitCode == 0)
            {
                throw new SdkOutputException(MF(TextKey.M236, $"dotnet package list 返回了无法识别的输出：{Truncate(Redact(result.Stdout))}"));
            }
            return (null, Redact((result.Stdout + "\n" + result.Stderr).Trim()));
        }
        var errors = AllProblems(output).Where(p => !string.Equals(p.Level, "warning", StringComparison.OrdinalIgnoreCase)).ToList();
        if (result.ExitCode != 0 || errors.Count > 0 || output.Projects is null)
        {
            return (null, Redact(string.Join("\n", errors.Select(e => e.Text)).Trim() is { Length: > 0 } t ? t : result.Stderr.Trim()));
        }
        return (ToOccurrences(project, output), null);
    }

    private static List<PackageOccurrence> ToOccurrences(ProjectDescriptor project, PackageListOutput output)
    {
        var list = new List<PackageOccurrence>();
        foreach (var p in output.Projects ?? [])
        {
            foreach (var fw in p.Frameworks ?? [])
            {
                var framework = fw.Framework ?? "";
                foreach (var pkg in fw.TopLevelPackages ?? [])
                {
                    if (pkg.Id is null)
                    {
                        continue;
                    }
                    var kind = IsTrue(pkg.AutoReferenced) ? ReferenceKind.SdkImplicit : ReferenceKind.Direct;
                    list.Add(new PackageOccurrence(project.Path, framework, pkg.Id, pkg.RequestedVersion, pkg.ResolvedVersion ?? "", kind, null));
                }
                foreach (var pkg in fw.TransitivePackages ?? [])
                {
                    if (pkg.Id is null)
                    {
                        continue;
                    }
                    list.Add(new PackageOccurrence(project.Path, framework, pkg.Id, null, pkg.ResolvedVersion ?? "", ReferenceKind.Transitive, null));
                }
            }
        }
        return list;
    }

    private async Task ReadOnlyRestoreAsync(ProjectDescriptor project, ProjectEvaluation? evaluation, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(project.Path)!;
        var lockFiles = evaluation?.LockFilePaths ?? [Path.Combine(dir, "packages.lock.json")];
        var args = new List<string> { "restore", project.Path };
        if (lockFiles.Any(File.Exists))
        {
            args.Add("--locked-mode");
        }
        else if (lockFiles.Count > 0)
        {
            // Loading must not create a lock file the user has not seen yet.
            args.Add("-p:RestorePackagesWithLockFile=false");
        }
        await RunCheckedAsync(dir, args, LongTimeout, ct).ConfigureAwait(false);
    }

    public async Task RestorePreservingLockFilesAsync(IReadOnlyList<ProjectEvaluation> evaluations, CancellationToken ct)
    {
        foreach (var evaluation in evaluations)
        {
            await ReadOnlyRestoreAsync(evaluation.Project, evaluation, ct).ConfigureAwait(false);
        }
    }

    public async Task RestoreAsync(IReadOnlyList<ProjectDescriptor> projects, bool lockedMode, CancellationToken ct)
    {
        foreach (var project in projects)
        {
            var args = new List<string> { "restore", project.Path };
            if (lockedMode)
            {
                args.Add("--locked-mode");
            }
            await RunCheckedAsync(Path.GetDirectoryName(project.Path)!, args, LongTimeout, ct).ConfigureAwait(false);
        }
    }

    public async Task<PackageListOutput> ListOutdatedAsync(ProjectDescriptor project, bool includePrerelease, CancellationToken ct)
    {
        var args = new List<string> { "package", "list", "--project", project.Path, "--outdated", "--no-restore", "--format", "json", "--output-version", "1" };
        if (includePrerelease)
        {
            args.Add("--include-prerelease");
        }
        var result = await RunFreshQueryAsync(Path.GetDirectoryName(project.Path)!, args, ct).ConfigureAwait(false);
        var output = TryParse(result.Stdout, SdkJsonContext.Default.PackageListOutput);
        if (output is null)
        {
            throw result.ExitCode == 0
                ? new SdkOutputException(MF(TextKey.M237, $"dotnet package list --outdated 返回了无法识别的输出：{Truncate(Redact(result.Stdout))}"))
                : new SdkCommandException(ProcessRunner.Describe(args), result.ExitCode, Redact((result.Stdout + "\n" + result.Stderr).Trim()));
        }
        if (result.ExitCode != 0 && !AllProblems(output).Any())
        {
            output.Problems = [new ListProblem { Text = Redact(result.Stderr.Trim()), Level = "error" }];
        }
        return output;
    }

    public async Task<SearchOutput> SearchAsync(string workingDirectory, string query, int skip, int take, bool includePrerelease, bool exactMatch, string? source, CancellationToken ct)
    {
        var args = new List<string> { "package", "search", query, "--format", "json" };
        if (exactMatch)
        {
            args.Add("--exact-match");
        }
        else
        {
            args.AddRange(["--skip", skip.ToString(System.Globalization.CultureInfo.InvariantCulture), "--take", take.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        if (includePrerelease)
        {
            args.Add("--prerelease");
        }
        if (source is not null)
        {
            args.AddRange(["--source", source]);
        }
        var result = await RunFreshQueryAsync(workingDirectory, args, ct).ConfigureAwait(false);
        var output = TryParse(result.Stdout, SdkJsonContext.Default.SearchOutput);
        if (output is null)
        {
            throw result.ExitCode == 0
                ? new SdkOutputException(MF(TextKey.M238, $"dotnet package search 返回了无法识别的输出：{Truncate(Redact(result.Stdout))}"))
                : new SdkCommandException(ProcessRunner.Describe(args), result.ExitCode, Redact((result.Stdout + "\n" + result.Stderr).Trim()));
        }
        foreach (var p in output.Problems ?? [])
        {
            p.Text = Redact(p.Text ?? "");
        }
        return output;
    }

    public Task AddPackageAsync(string projectPath, string packageId, string version, CancellationToken ct) =>
        RunCheckedAsync(Path.GetDirectoryName(projectPath)!,
            ["package", "add", packageId, "--version", version, "--project", projectPath, "--no-restore"], LongTimeout, ct);

    public Task RemovePackageAsync(string projectPath, string packageId, CancellationToken ct) =>
        RunCheckedAsync(Path.GetDirectoryName(projectPath)!,
            ["package", "remove", packageId, "--project", projectPath], LongTimeout, ct);

    public async Task<string?> GetGlobalPackagesFolderAsync(string directory, CancellationToken ct)
    {
        if (_environment is not null && _environment.TryGetValue("NUGET_PACKAGES", out var configured) && !string.IsNullOrEmpty(configured))
        {
            return configured;
        }
        var result = await RunRawAsync(directory, ["nuget", "locals", "global-packages", "--list"], ShortTimeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }
        foreach (var line in result.Stdout.Split('\n'))
        {
            var idx = line.IndexOf("global-packages:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var path = line[(idx + "global-packages:".Length)..].Trim();
                return path.Length > 0 ? path : null;
            }
        }
        return null;
    }

    private async Task<ProcessResult> RunCheckedAsync(string directory, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await RunRawAsync(directory, args, timeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new SdkCommandException(ProcessRunner.Describe(args), result.ExitCode, Truncate(Redact((result.Stdout + "\n" + result.Stderr).Trim())));
        }
        return result;
    }

    private async Task<T> RunJsonAsync<T>(string directory, IReadOnlyList<string> args, TimeSpan timeout, JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        var result = await RunCheckedAsync(directory, args, timeout, ct).ConfigureAwait(false);
        return TryParse(result.Stdout, info)
            ?? throw new SdkOutputException(MF(TextKey.M239, $"dotnet {args[0]} 返回了无法识别的输出：{Truncate(Redact(result.Stdout))}"));
    }

    private Task<ProcessResult> RunRawAsync(string directory, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var workDir = Directory.Exists(directory) ? directory : Path.GetTempPath();
        return _runner.RunAsync(new ProcessRequest("dotnet", args, workDir, timeout, _environment), ct);
    }

    private async Task<ProcessResult> RunFreshQueryAsync(string directory, IReadOnlyList<string> args, CancellationToken ct)
    {
        // list/search have no --no-http-cache option. Isolate metadata queries without clearing
        // shared NuGet caches or changing the package cache, source configuration or credentials.
        var cache = Directory.CreateTempSubdirectory("NugetManager-query-");
        try
        {
            var environment = _environment is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(_environment, StringComparer.OrdinalIgnoreCase);
            environment["NUGET_HTTP_CACHE_PATH"] = cache.FullName;
            return await _runner.RunAsync(new ProcessRequest("dotnet", args, directory, LongTimeout, environment), ct).ConfigureAwait(false);
        }
        finally
        {
            cache.Delete(recursive: true);
        }
    }

    private static T? TryParse<T>(string text, JsonTypeInfo<T> info) where T : class
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(text.AsSpan(start), info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IEnumerable<ListProblem> AllProblems(PackageListOutput output) =>
        (output.Problems ?? []).Concat((output.Projects ?? []).SelectMany(p => p.Problems ?? []));

    private static async Task ParallelForAsync(int count, Func<int, Task> body, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await body(i).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static IEnumerable<Dictionary<string, string>> Items(MsBuildOutput output, string name) =>
        output.Items is not null && output.Items.TryGetValue(name, out var list) ? list : [];

    private static string? Get(Dictionary<string, string> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v : null;

    private static List<string> Split(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Normalize(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static string Truncate(string text) => text.Length > 4000 ? text[..4000] + "…" : text;

    [GeneratedRegex(@"(?<=://)[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)\b(password|pwd|token|apikey|api-key|secret|cleartextpassword)\s*[=:]\s*\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignments();

    internal static string Redact(string text) =>
        SecretAssignments().Replace(UrlCredentials().Replace(text, "***@"), m => m.Groups[1].Value + "=***");

    /// <summary>Reads raw project XML to detect declaration shapes that evaluation flattens.</summary>
    private sealed class DeclarationInspector
    {
        private readonly Dictionary<string, XDocument?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public (bool HasVersion, UiMessage? Problem) Inspect(string file, string elementName, string id)
        {
            if (!_cache.TryGetValue(file, out var doc))
            {
                doc = Load(file);
                _cache[file] = doc;
            }
            if (doc is null)
            {
                return (false, MF(TextKey.M240, $"无法读取声明文件 {file}。"));
            }
            var matches = doc.Descendants()
                .Where(e => e.Name.LocalName == elementName)
                .Where(e => ((string?)e.Attribute("Include") ?? "")
                    .Split(';', StringSplitOptions.TrimEntries).Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return (false, MF(TextKey.M241, $"无法在 {Path.GetFileName(file)} 中定位 {id} 的 {elementName} 声明（可能使用了通配符或属性生成）。"));
            }
            if (matches.Count > 1)
            {
                return (false, MF(TextKey.M242, $"{Path.GetFileName(file)} 中存在多个 {id} 的 {elementName} 声明，无法安全确定写入目标。"));
            }
            var element = matches[0];
            if (element.AncestorsAndSelf().Any(a => a.Attribute("Condition") is not null || a.Name.LocalName is "When" or "Otherwise"))
            {
                return (false, MF(TextKey.M243, $"{id} 在 {Path.GetFileName(file)} 中是条件引用，自动写入会丢失条件，请手动修改。"));
            }
            var version = (string?)element.Attribute("Version") ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            if (version is not null && (version.Contains("$(") || version.Contains("@(") || version.Contains("%(")))
            {
                return (true, MF(TextKey.M244, $"{id} 的版本使用了表达式 {version}，自动写入会替换表达式，请手动修改。"));
            }
            return (version is not null, null);
        }

        private static XDocument? Load(string file)
        {
            try
            {
                using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                return XDocument.Load(reader);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
