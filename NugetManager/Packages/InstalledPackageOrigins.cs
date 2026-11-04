using System.Text.Json;
using NugetManager.Domain;
using NugetManager.Presentation;

namespace NugetManager.Packages;

internal static class InstalledPackageOrigins
{
    public static async Task<PackageInventory> ReadAsync(string assetsFile, IReadOnlyList<PackageOccurrence> occurrences, CancellationToken ct)
    {
        var problems = new List<ProjectProblem>();
        var result = new List<PackageOccurrence>(occurrences.Count);
        try
        {
            await using var stream = File.OpenRead(assetsFile);
            using var assets = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
            var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var origins = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var occurrence in occurrences)
            {
                ct.ThrowIfCancellationRequested();
                var identity = occurrence.PackageId + "/" + occurrence.ResolvedVersion;
                if (!origins.TryGetValue(identity, out var source))
                {
                    try
                    {
                        if (libraries.TryGetValue(identity, out var library) && library.TryGetProperty("path", out var path))
                        {
                            var relative = path.GetString() ?? throw new JsonException();
                            foreach (var folder in folders)
                            {
                                var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                                var directory = Path.GetFullPath(Path.Combine(root, relative));
                                if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new JsonException();
                                if (!Directory.Exists(directory)) continue;
                                var metadataFile = Path.Combine(directory, ".nupkg.metadata");
                                if (File.Exists(metadataFile))
                                {
                                    await using var metadataStream = File.OpenRead(metadataFile);
                                    using var metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: ct).ConfigureAwait(false);
                                    source = metadata.RootElement.GetProperty("source").GetString();
                                    if (string.IsNullOrWhiteSpace(source)) throw new JsonException();
                                }
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                        or InvalidOperationException or ArgumentException or NotSupportedException)
                    {
                        problems.Add(Problem(occurrence.ProjectPath, ex));
                    }
                    origins[identity] = source;
                }
                result.Add(occurrence with { SourceLocation = source });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(occurrences) { Problems = [Problem(occurrences.FirstOrDefault()?.ProjectPath ?? assetsFile, ex)] };
        }
        return new(result) { Problems = problems };
    }

    private static ProjectProblem Problem(string project, Exception error) => new(project,
        UiMessage.FromFormat(TextKey.M050, $"无法确认缓存来源：{UiMessage.FromException(error)}"));
}
