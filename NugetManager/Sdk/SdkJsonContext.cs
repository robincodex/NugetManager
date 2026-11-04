using System.Text.Json;
using System.Text.Json.Serialization;

namespace NugetManager.Sdk;

internal sealed class PackageListOutput
{
    public int Version { get; set; }
    public List<string>? Sources { get; set; }
    public List<ListProblem>? Problems { get; set; }
    public List<ListProject>? Projects { get; set; }
}

internal sealed class ListProblem
{
    public string? Project { get; set; }
    public string? Text { get; set; }
    public string? Level { get; set; }
}

internal sealed class ListProject
{
    public string? Path { get; set; }
    public List<ListProblem>? Problems { get; set; }
    public List<ListFramework>? Frameworks { get; set; }
}

internal sealed class ListFramework
{
    public string? Framework { get; set; }
    public List<ListPackage>? TopLevelPackages { get; set; }
    public List<ListPackage>? TransitivePackages { get; set; }
}

internal sealed class ListPackage
{
    public string? Id { get; set; }
    public string? RequestedVersion { get; set; }
    public string? ResolvedVersion { get; set; }
    public string? LatestVersion { get; set; }
    public string? AutoReferenced { get; set; }
}

internal sealed class SearchOutput
{
    public int Version { get; set; }
    public List<SearchProblem>? Problems { get; set; }
    public List<SearchSourceResult>? SearchResult { get; set; }
}

internal sealed class SearchProblem
{
    public string? Text { get; set; }
    public string? ProblemType { get; set; }
}

internal sealed class SearchSourceResult
{
    public string? SourceName { get; set; }
    public List<SearchPackage>? Packages { get; set; }
}

internal sealed class SearchPackage
{
    public string? Id { get; set; }
    public string? LatestVersion { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Owners { get; set; }
    public long? TotalDownloads { get; set; }
}

internal sealed class MsBuildOutput
{
    public Dictionary<string, string>? Properties { get; set; }
    public Dictionary<string, List<Dictionary<string, string>>>? Items { get; set; }
}

internal sealed class V3ServiceIndex
{
    public List<V3Resource>? Resources { get; set; }
}

internal sealed class V3Resource
{
    [JsonPropertyName("@id")]
    public string? Id { get; set; }

    [JsonPropertyName("@type")]
    public JsonElement Type { get; set; }
}

internal sealed class RecoveryManifest
{
    public string? OperationId { get; set; }
    public string? Description { get; set; }
    public string? Phase { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public List<RecoveryEntry> Files { get; set; } = [];
}

internal sealed class RecoveryEntry
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string? BackupFile { get; set; }
    public string? OriginalHash { get; set; }
    public string? OwnedHash { get; set; }
    public bool Conflict { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

internal sealed class PopularOutput
{
    public long? TotalHits { get; set; }
    public List<PopularPackage>? Data { get; set; }
}

internal sealed class PopularPackage
{
    public PopularRegistration? PackageRegistration { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
}

internal sealed class PopularRegistration
{
    public string? Id { get; set; }
    public long? DownloadCount { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageListOutput))]
[JsonSerializable(typeof(SearchOutput))]
[JsonSerializable(typeof(MsBuildOutput))]
[JsonSerializable(typeof(V3ServiceIndex))]
[JsonSerializable(typeof(RecoveryManifest))]
[JsonSerializable(typeof(PopularOutput))]
internal sealed partial class SdkJsonContext : JsonSerializerContext;
