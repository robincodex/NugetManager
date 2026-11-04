using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
using System.Text.Json;
using NugetManager.Domain;
using NugetManager.Sdk;

namespace NugetManager.Packages;

internal sealed record PopularReadResult(bool Supported, IReadOnlyList<SearchHit> Items, IReadOnlyList<SourceDiagnostic> Diagnostics);

internal interface IPopularPackageReader
{
    Task<PopularReadResult> ReadAsync(NuGetSource source, int take, bool includePrerelease, CancellationToken ct);
}

internal sealed class PopularPackageReader(HttpClient http) : IPopularPackageReader
{
    internal static bool IsOfficialSource(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.Equals(uri.Host, "api.nuget.org", StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.AbsolutePath, "/v3/index.json", StringComparison.OrdinalIgnoreCase)
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 && uri.IsDefaultPort;

    public async Task<PopularReadResult> ReadAsync(NuGetSource source, int take, bool includePrerelease, CancellationToken ct)
    {
        if (source.HasCredentials || !IsOfficialSource(source.Location))
        {
            return new PopularReadResult(false, [], []);
        }

        // NuGetGallery GallerySearchClient uses this endpoint and sort name. V3 /query ignores sortBy.
        // https://github.com/NuGet/NuGetGallery/blob/main/src/NuGetGallery/Infrastructure/Lucene/GallerySearchClient.cs
        var url = $"https://azuresearch-usnc.nuget.org/search/query?q=&skip=0&take={take}&sortBy=totalDownloads-desc&prerelease={includePrerelease.ToString().ToLowerInvariant()}&semVerLevel=2.0.0";
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var output = await JsonSerializer.DeserializeAsync(stream, SdkJsonContext.Default.PopularOutput, ct).ConfigureAwait(false);
            if (output?.Data is null || output.TotalHits is null
                || output.Data.Any(p => p.PackageRegistration?.Id is null || p.PackageRegistration.DownloadCount is null
                    || p.Version is null || !NuGetVersionComparer.TryParse(p.Version, out _)))
            {
                throw new SdkOutputException(M(TextKey.M072));
            }
            var counts = output.Data.Select(p => p.PackageRegistration!.DownloadCount!.Value).ToArray();
            if (!counts.SequenceEqual(counts.OrderDescending()))
            {
                throw new SdkOutputException(M(TextKey.M073));
            }
            var items = output.Data.Take(take)
                .Select(p => new SearchHit(p.PackageRegistration!.Id!, p.Version!, source.Name, p.Description)).ToList();
            return new PopularReadResult(true, items, []);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or SdkOutputException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new PopularReadResult(true, [], [new SourceDiagnostic(source.Name, "PopularityFailed", UiMessage.FromException(ex))]);
        }
    }
}
