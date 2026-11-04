using NugetManager.Domain;
using NugetManager.Sdk;

namespace NugetManager.Packages;

internal interface IPackageCatalog
{
    Task<UpdateReport> FindUpdatesAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation> evaluations, WorkspaceScope scope, bool includePrerelease, CancellationToken ct);

    Task<SearchPage> SearchAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct, SourceSelection? selection = null);

    Task<PackageVersions> GetVersionsAsync(Workspace workspace, string packageId, bool includePrerelease, CancellationToken ct, string? sourceName = null);

    Task<BrowsePage> BrowseAsync(Workspace workspace, SourceSelection selection, bool includePrerelease, CancellationToken ct);
}

internal sealed class PackageCatalog(ISdkClient sdk, IPopularPackageReader? popular = null) : IPackageCatalog
{
    public async Task<UpdateReport> FindUpdatesAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation> evaluations, WorkspaceScope scope,
        bool includePrerelease, CancellationToken ct)
    {
        var inScope = evaluations.Where(e => scope.Includes(e.Project.Path)).ToList();
        var outputs = new (ProjectEvaluation Evaluation, PackageListOutput? Output, Exception? Error)[inScope.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(inScope.Select(async (evaluation, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                outputs[i] = (evaluation, await sdk.ListOutdatedAsync(evaluation.Project, includePrerelease, ct).ConfigureAwait(false), null);
            }
            catch (Exception ex) when (ex is SdkCommandException or SdkOutputException)
            {
                outputs[i] = (evaluation, null, ex);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var diagnostics = new List<SourceDiagnostic>();
        var upgrades = new List<PackageUpgrade>();
        foreach (var (evaluation, output, error) in outputs)
        {
            if (output is null)
            {
                diagnostics.Add(new SourceDiagnostic(evaluation.Project.Name, "UpdateCheckFailed", error?.Message ?? "无法检查更新。"));
                continue;
            }
            foreach (var problem in SdkClient.AllProblems(output))
            {
                diagnostics.Add(new SourceDiagnostic(evaluation.Project.Name,
                    string.Equals(problem.Level, "warning", StringComparison.OrdinalIgnoreCase) ? "SourceWarning" : "SourceError",
                    problem.Text ?? "包源查询失败。"));
            }
            foreach (var framework in (output.Projects ?? []).SelectMany(p => p.Frameworks ?? []))
            {
                foreach (var package in framework.TopLevelPackages ?? [])
                {
                    if (package.Id is null || package.ResolvedVersion is null || package.LatestVersion is null
                        || string.Equals(package.AutoReferenced, "true", StringComparison.OrdinalIgnoreCase)
                        || !NuGetVersionComparer.IsNewer(package.LatestVersion, package.ResolvedVersion))
                    {
                        continue;
                    }
                    var reference = FindReference(evaluation, package.Id, framework.Framework);
                    if (reference is null)
                    {
                        continue;
                    }
                    upgrades.Add(new PackageUpgrade(reference, package.ResolvedVersion, package.LatestVersion));
                }
            }
        }

        var candidates = upgrades
            .GroupBy(u => u.Reference.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UpdateCandidate(g.First().Reference.PackageId, g.ToList()))
            .OrderBy(c => c.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new UpdateReport(candidates, diagnostics, diagnostics.Count == 0);
    }

    private static EvaluatedReference? FindReference(ProjectEvaluation evaluation, string id, string? framework)
    {
        var matches = evaluation.References.Where(r => string.Equals(r.PackageId, id, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.FirstOrDefault(r => string.Equals(r.Framework, framework, StringComparison.OrdinalIgnoreCase))
            ?? (matches.Count > 0 && evaluation.Frameworks.Count == 1 ? matches[0] : null);
    }

    public async Task<SearchPage> SearchAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct, SourceSelection? selection = null)
    {
        var sources = NuGetConfigReader.Load(workspace.Directory).Sources;
        var names = (selection ?? SourceSelection.All).Resolve(sources);
        using var gate = new SemaphoreSlim(4);
        var pages = await Task.WhenAll(names.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SearchSourceAsync(workspace, query, skip, take, includePrerelease, ct, name).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SdkCommandException or SdkOutputException)
            {
                return new SearchPage([], skip, take, false, [new SourceDiagnostic(name, "SearchFailed", ex.Message)]);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return new SearchPage(pages.SelectMany(p => p.Items).ToList(), skip, take, pages.Any(p => p.HasMore),
            pages.SelectMany(p => p.Diagnostics).ToList());
    }

    private async Task<SearchPage> SearchSourceAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct, string sourceName)
    {
        var selected = FindSource(workspace, sourceName);
        var output = await sdk.SearchAsync(workspace.Directory, query, skip, take, includePrerelease, false, selected?.Location, ct).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        var hasMore = false;
        foreach (var source in output.SearchResult ?? [])
        {
            var packages = source.Packages ?? [];
            hasMore |= packages.Count >= take;
            foreach (var package in packages)
            {
                if (package.Id is null)
                {
                    continue;
                }
                var name = selected?.Name ?? source.SourceName ?? "";
                if (hits.Any(h => string.Equals(h.PackageId, package.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(h.SourceName, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                hits.Add(new SearchHit(package.Id, package.LatestVersion ?? package.Version ?? "", name, package.Description));
            }
        }
        return new SearchPage(hits, skip, take, hasMore,
            ToDiagnostics(output).Select(d => d with { SourceName = sourceName }).Concat(UnavailableLocalSources(workspace, sourceName)).ToList());
    }

    public async Task<PackageVersions> GetVersionsAsync(Workspace workspace, string packageId, bool includePrerelease, CancellationToken ct, string? sourceName = null)
    {
        var selected = FindSource(workspace, sourceName);
        var output = await sdk.SearchAsync(workspace.Directory, packageId, 0, 0, includePrerelease, true, selected?.Location, ct).ConfigureAwait(false);
        var versions = new List<string>();
        string? resultSourceName = selected?.Name;
        foreach (var source in output.SearchResult ?? [])
        {
            foreach (var package in source.Packages ?? [])
            {
                var version = package.Version ?? package.LatestVersion;
                if (version is null || !string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase)
                    || !NuGetVersionComparer.TryParse(version, out _)
                    || (!includePrerelease && NuGetVersionComparer.IsPrerelease(version)))
                {
                    continue;
                }
                resultSourceName ??= source.SourceName;
                if (!versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                {
                    versions.Add(version);
                }
            }
        }
        versions.Sort((a, b) => NuGetVersionComparer.Instance.Compare(b, a));
        return new PackageVersions(packageId, versions, ToDiagnostics(output).Concat(UnavailableLocalSources(workspace, sourceName)).ToList())
            { SourceName = resultSourceName };
    }

    public async Task<BrowsePage> BrowseAsync(Workspace workspace, SourceSelection selection, bool includePrerelease, CancellationToken ct)
    {
        var settings = NuGetConfigReader.Load(workspace.Directory);
        var names = selection.Resolve(settings.Sources);
        var sources = settings.Sources.Where(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var take = sources.Count == 1 ? 30 : 10;
        using var gate = new SemaphoreSlim(4);
        var groups = await Task.WhenAll(sources.Select(async source =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = popular is null ? null : await popular.ReadAsync(source, take, includePrerelease, ct).ConfigureAwait(false);
                if (result is { Supported: true })
                {
                    return new BrowseSourcePage(source, BrowseKind.Popular, new SearchPage(result.Items.Take(take).ToList(), 0, take, false, result.Diagnostics));
                }
                var page = await SearchSourceAsync(workspace, "", 0, take, includePrerelease, ct, source.Name).ConfigureAwait(false);
                return new BrowseSourcePage(source, BrowseKind.Ordinary, page with { Items = page.Items.Take(take).ToList(), HasMore = false });
            }
            catch (Exception ex) when (ex is SdkCommandException or SdkOutputException)
            {
                return new BrowseSourcePage(source, BrowseKind.Ordinary,
                    new SearchPage([], 0, take, false, [new SourceDiagnostic(source.Name, "BrowseFailed", ex.Message)]));
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return new BrowsePage(groups);
    }

    private static NuGetSource? FindSource(Workspace workspace, string? name) => name is null ? null
        : NuGetConfigReader.Load(workspace.Directory).Sources.FirstOrDefault(s => s.IsEnabled && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new SdkOutputException($"包源不存在或已禁用：{name}");

    private static List<SourceDiagnostic> ToDiagnostics(SearchOutput output) =>
        (output.Problems ?? []).Select(p => new SourceDiagnostic("", p.ProblemType ?? "SourceError", p.Text ?? "包源查询失败。")).ToList();

    private static IEnumerable<SourceDiagnostic> UnavailableLocalSources(Workspace workspace, string? sourceName) =>
        NuGetConfigReader.Load(workspace.Directory).Sources
            .Where(s => s.IsEnabled && (sourceName is null || string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                && !s.IsHttp && !Directory.Exists(s.Location))
            .Select(s => new SourceDiagnostic(s.Name, "SourceUnavailable", $"本地包源目录不存在或无法访问：{s.Location}"));
}
