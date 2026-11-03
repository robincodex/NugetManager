using NugetManager.Domain;
using NugetManager.Sdk;

namespace NugetManager.Packages;

internal interface IPackageCatalog
{
    Task<UpdateReport> FindUpdatesAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation> evaluations, WorkspaceScope scope, bool includePrerelease, CancellationToken ct);

    Task<SearchPage> SearchAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct);

    Task<PackageVersions> GetVersionsAsync(Workspace workspace, string packageId, bool includePrerelease, CancellationToken ct);
}

internal sealed class PackageCatalog(ISdkClient sdk) : IPackageCatalog
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

    public async Task<SearchPage> SearchAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct)
    {
        var output = await sdk.SearchAsync(workspace.Directory, query, skip, take, includePrerelease, false, null, ct).ConfigureAwait(false);
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
                if (hits.Any(h => string.Equals(h.PackageId, package.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                hits.Add(new SearchHit(package.Id, package.LatestVersion ?? package.Version ?? "", source.SourceName ?? "", package.Description));
            }
        }
        return new SearchPage(hits, skip, take, hasMore, ToDiagnostics(output));
    }

    public async Task<PackageVersions> GetVersionsAsync(Workspace workspace, string packageId, bool includePrerelease, CancellationToken ct)
    {
        var output = await sdk.SearchAsync(workspace.Directory, packageId, 0, 0, includePrerelease, true, null, ct).ConfigureAwait(false);
        var versions = new List<string>();
        string? sourceName = null;
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
                sourceName ??= source.SourceName;
                if (!versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                {
                    versions.Add(version);
                }
            }
        }
        versions.Sort((a, b) => NuGetVersionComparer.Instance.Compare(b, a));
        return new PackageVersions(packageId, versions, ToDiagnostics(output)) { SourceName = sourceName };
    }

    private static List<SourceDiagnostic> ToDiagnostics(SearchOutput output) =>
        (output.Problems ?? []).Select(p => new SourceDiagnostic("", p.ProblemType ?? "SourceError", p.Text ?? "包源查询失败。")).ToList();
}
