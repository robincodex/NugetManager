using NugetManager.Packages;

namespace NugetManager.Domain;

internal enum ReferenceKind
{
    Direct,
    Transitive,
    SdkImplicit,
}

internal sealed record PackageOccurrence(
    string ProjectPath,
    string Framework,
    string PackageId,
    string? RequestedVersion,
    string ResolvedVersion,
    ReferenceKind Kind,
    string? DeclarationPath);

internal sealed record EvaluatedReference(
    string PackageId,
    string Framework,
    string DeclarationPath,
    string? CentralVersionPath,
    string? VersionExpression,
    string? VersionOverride,
    bool IsWritable,
    string? WriteBlockReason)
{
    public string ProjectPath { get; init; } = DeclarationPath;
}

internal sealed record ProjectEvaluation(
    ProjectDescriptor Project,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<EvaluatedReference> References,
    IReadOnlyList<string> LockFilePaths)
{
    public bool CentralPackageManagement { get; init; }

    /// <summary>Effective Directory.Packages.props when central package management is enabled.</summary>
    public string? CentralPackagesFile { get; init; }

    /// <summary>Every central PackageVersion entry visible to the project: id → defining file.</summary>
    public IReadOnlyDictionary<string, string> CentralVersions { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ProjectProblem(string ProjectPath, string Message);

internal sealed record PackageInventory(IReadOnlyList<PackageOccurrence> Occurrences)
{
    public IReadOnlyList<ProjectProblem> Problems { get; init; } = [];
}

internal sealed record PackageUpgrade(EvaluatedReference Reference, string FromVersion, string ToVersion);

internal sealed record UpdateCandidate(string PackageId, IReadOnlyList<PackageUpgrade> Upgrades)
{
    public string ToVersion => Upgrades.Select(u => u.ToVersion).OrderByDescending(v => v, NuGetVersionComparer.Instance).First();

    public IReadOnlyList<string> FromVersions => Upgrades.Select(u => u.FromVersion).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, NuGetVersionComparer.Instance).ToList();
}

internal sealed record SourceDiagnostic(string SourceName, string Code, string Message);

internal sealed record UpdateReport(IReadOnlyList<UpdateCandidate> Candidates, IReadOnlyList<SourceDiagnostic> Diagnostics, bool IsComplete);

internal sealed record SearchHit(string PackageId, string Version, string SourceName, string? Description);

internal sealed record SearchPage(IReadOnlyList<SearchHit> Items, int Skip, int Take, bool HasMore, IReadOnlyList<SourceDiagnostic> Diagnostics);

internal enum BrowseKind { Popular, Ordinary }

internal sealed record BrowseSourcePage(NuGetSource Source, BrowseKind Kind, SearchPage Page);

internal sealed record BrowsePage(IReadOnlyList<BrowseSourcePage> Sources);

internal sealed record DependencyItem(string PackageId, string VersionRange);

internal sealed record DependencyGroup(string Framework, IReadOnlyList<DependencyItem> Items);

internal sealed record PackageDetails(
    string PackageId,
    string Version,
    string? Authors,
    string? Description,
    string? License,
    Uri? ProjectUrl,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<DependencyGroup> Dependencies,
    IReadOnlyList<SourceDiagnostic> Diagnostics)
{
    /// <summary>Where the metadata came from (cache, local feed, or remote source).</summary>
    public string? Origin { get; init; }
}

internal sealed record PackageVersions(string PackageId, IReadOnlyList<string> Versions, IReadOnlyList<SourceDiagnostic> Diagnostics)
{
    public string? SourceName { get; init; }
}
