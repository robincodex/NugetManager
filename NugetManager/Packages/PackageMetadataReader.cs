using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NugetManager.Domain;
using NugetManager.Sdk;

namespace NugetManager.Packages;

internal interface IPackageMetadataReader
{
    Task<PackageDetails> ReadAsync(Workspace workspace, string packageId, string version, string? sourceName, CancellationToken ct);
}

internal sealed partial class PackageMetadataReader(HttpClient http, string? globalPackagesFolder = null, string? userConfigPath = null)
    : IPackageMetadataReader
{
    private readonly Dictionary<string, string?> _baseAddresses = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9][A-Za-z0-9.\-+]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public async Task<PackageDetails> ReadAsync(Workspace workspace, string packageId, string version, string? sourceName, CancellationToken ct)
    {
        if (!IdPattern().IsMatch(packageId) || packageId.Contains("..", StringComparison.Ordinal))
        {
            throw UiErrors.Argument(MF(TextKey.M048, $"无效的包 ID：{packageId}"), nameof(packageId));
        }
        if (!VersionPattern().IsMatch(version))
        {
            throw UiErrors.Argument(MF(TextKey.M049, $"无效的版本：{version}"), nameof(version));
        }

        var settings = NuGetConfigReader.Load(workspace.Directory, userConfigPath);
        var diagnostics = new List<SourceDiagnostic>();
        var idLower = packageId.ToLowerInvariant();
        var versionLower = NormalizeVersion(version).ToLowerInvariant();

        var gpf = globalPackagesFolder ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? settings.GlobalPackagesFolder
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var cacheDir = Path.Combine(gpf, idLower, versionLower);
        var cachedNuspec = Path.Combine(cacheDir, idLower + ".nuspec");
        var cacheAllowed = sourceName is null;
        NuGetSource? cachedOrigin = null;
        var cacheMetadata = Path.Combine(cacheDir, ".nupkg.metadata");
        if (File.Exists(cacheMetadata))
        {
            try
            {
                await using var metadataStream = File.OpenRead(cacheMetadata);
                using var metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: ct).ConfigureAwait(false);
                var cachedSource = metadata.RootElement.TryGetProperty("source", out var origin) && origin.ValueKind == JsonValueKind.String
                    ? origin.GetString() : null;
                cachedOrigin = cachedSource is null ? null : settings.SourcesFor(packageId).FirstOrDefault(s =>
                    string.Equals(s.Location.TrimEnd('/', '\\'), cachedSource.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));
                cacheAllowed = sourceName is null || cachedOrigin is not null
                    && string.Equals(cachedOrigin.Name, sourceName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                diagnostics.Add(new SourceDiagnostic(sourceName!, "CacheSourceUnknown", MF(TextKey.M050, $"无法确认缓存来源：{UiMessage.FromException(ex)}")));
            }
        }
        if (cacheAllowed && File.Exists(cachedNuspec))
        {
            await using var stream = File.OpenRead(cachedNuspec);
            var frameworks = FrameworkFolders(Directory.EnumerateDirectories(cacheDir)
                .Where(d => Path.GetFileName(d) is "lib" or "ref")
                .SelectMany(Directory.EnumerateDirectories)
                .Select(d => Path.GetFileName(d)!));
            var cached = Parse(packageId, version, stream, frameworks, M(TextKey.M051), "", diagnostics);
            if (cached is not null)
            {
                return cached with { SourceName = cachedOrigin?.Name, SourceLocation = cachedOrigin?.Location };
            }
        }

        var candidates = settings.SourcesFor(packageId);
        if (sourceName is not null)
        {
            var selected = candidates.Where(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (selected.Count == 0)
            {
                diagnostics.Add(new SourceDiagnostic(sourceName, "SourceNotAllowed",
                    MF(TextKey.M052, $"包源 {sourceName} 未启用，或包源映射不允许从该源获取 {packageId}。")));
            }
            candidates = selected;
        }

        foreach (var source in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var details = source.IsHttp
                ? await ReadRemoteAsync(source, packageId, version, idLower, versionLower, diagnostics, ct).ConfigureAwait(false)
                : ReadLocal(source, packageId, version, idLower, versionLower, diagnostics);
            if (details is not null)
            {
                return details with { SourceName = source.Name, SourceLocation = source.Location };
            }
        }

        if (!diagnostics.Any(d => d.Code is "AuthRequired" or "InvalidMetadata"))
        {
            diagnostics.Add(new SourceDiagnostic(sourceName ?? "", "NotFound", MF(TextKey.M053, $"在缓存和可用包源中未找到 {packageId} {version} 的元数据。")));
        }
        return Empty(packageId, version, diagnostics);
    }

    private static PackageDetails? ReadLocal(NuGetSource source, string id, string version, string idLower, string versionLower,
        List<SourceDiagnostic> diagnostics)
    {
        if (!Directory.Exists(source.Location))
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "SourceUnavailable", MF(TextKey.M054, $"本地包源不存在：{source.Location}")));
            return null;
        }
        string[] paths =
        [
            Path.Combine(source.Location, $"{id}.{version}.nupkg"),
            Path.Combine(source.Location, $"{idLower}.{versionLower}.nupkg"),
            Path.Combine(source.Location, idLower, versionLower, $"{idLower}.{versionLower}.nupkg"),
        ];
        var path = paths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var nuspec = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\')
                && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspec is null)
            {
                diagnostics.Add(new SourceDiagnostic(source.Name, "InvalidMetadata", MF(TextKey.M055, $"{Path.GetFileName(path)} 中没有 nuspec 清单。")));
                return null;
            }
            var frameworks = FrameworkFolders(zip.Entries
                .Select(e => e.FullName.Replace('\\', '/').Split('/'))
                .Where(parts => parts.Length >= 3 && parts[0] is "lib" or "ref")
                .Select(parts => parts[1]));
            using var stream = nuspec.Open();
            return Parse(id, version, stream, frameworks, MF(TextKey.M056, $"本地包源 {source.Name}"), source.Name, diagnostics);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "InvalidMetadata", MF(TextKey.M057, $"无法读取 {Path.GetFileName(path)}：{UiMessage.FromException(ex)}")));
            return null;
        }
    }

    private async Task<PackageDetails?> ReadRemoteAsync(NuGetSource source, string id, string version, string idLower, string versionLower,
        List<SourceDiagnostic> diagnostics, CancellationToken ct)
    {
        if (source.HasCredentials)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "AuthRequired",
                MF(TextKey.M058, $"包源 {source.Name} 需要认证，且本地缓存中没有该版本；请先还原或在 IDE 中查看详情。")));
            return null;
        }
        try
        {
            var baseAddress = await GetBaseAddressAsync(source, diagnostics, ct).ConfigureAwait(false);
            if (baseAddress is null)
            {
                return null;
            }
            using var response = await http.GetAsync(new Uri($"{baseAddress.TrimEnd('/')}/{idLower}/{versionLower}/{idLower}.nuspec"), ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                diagnostics.Add(new SourceDiagnostic(source.Name, "AuthRequired", MF(TextKey.M059, $"包源 {source.Name} 拒绝访问（{(int)response.StatusCode}），需要认证。")));
                return null;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                diagnostics.Add(new SourceDiagnostic(source.Name, "SourceError", MF(TextKey.M060, $"包源 {source.Name} 返回 {(int)response.StatusCode}。")));
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return Parse(id, version, stream, [], MF(TextKey.M061, $"包源 {source.Name}"), source.Name, diagnostics);
        }
        catch (HttpRequestException ex)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "SourceUnavailable", MF(TextKey.M062, $"无法连接包源 {source.Name}：{SdkClient.Redact(ex.Message)}")));
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "SourceUnavailable", MF(TextKey.M063, $"连接包源 {source.Name} 超时。")));
            return null;
        }
    }

    private async Task<string?> GetBaseAddressAsync(NuGetSource source, List<SourceDiagnostic> diagnostics, CancellationToken ct)
    {
        lock (_baseAddresses)
        {
            if (_baseAddresses.TryGetValue(source.Location, out var known))
            {
                return known;
            }
        }
        if (!source.Location.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "UnsupportedSource", MF(TextKey.M064, $"包源 {source.Name} 不是 NuGet V3 源，无法读取详情。")));
            return null;
        }
        using var response = await http.GetAsync(new Uri(source.Location), ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "AuthRequired", MF(TextKey.M059, $"包源 {source.Name} 拒绝访问（{(int)response.StatusCode}），需要认证。")));
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "SourceError", MF(TextKey.M065, $"包源 {source.Name} 服务索引返回 {(int)response.StatusCode}。")));
            return null;
        }
        V3ServiceIndex? index;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            index = await JsonSerializer.DeserializeAsync(stream, SdkJsonContext.Default.V3ServiceIndex, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            index = null;
        }
        var address = index?.Resources?.FirstOrDefault(r => TypeNames(r.Type).Any(t => t.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal)))?.Id;
        if (address is null)
        {
            diagnostics.Add(new SourceDiagnostic(source.Name, "UnsupportedSource", MF(TextKey.M066, $"包源 {source.Name} 未提供 PackageBaseAddress 资源。")));
        }
        lock (_baseAddresses)
        {
            _baseAddresses[source.Location] = address;
        }
        return address;
    }

    private static IEnumerable<string> TypeNames(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => [type.GetString()!],
        JsonValueKind.Array => type.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!),
        _ => [],
    };

    private static PackageDetails? Parse(string id, string version, Stream stream, IReadOnlyList<string> frameworks, UiMessage origin, string sourceName,
        List<SourceDiagnostic> diagnostics)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new SourceDiagnostic(sourceName, "InvalidMetadata", MF(TextKey.M067, $"{id} {version} 的清单无效或包含不允许的 DTD：{UiMessage.FromException(ex)}")));
            return null;
        }
        var metadata = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata is null)
        {
            diagnostics.Add(new SourceDiagnostic(sourceName, "InvalidMetadata", MF(TextKey.M068, $"{id} {version} 的清单缺少 metadata。")));
            return null;
        }
        string? Value(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        UiMessage? license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license") is { } licenseElement
            ? ((string?)licenseElement.Attribute("type") == "file" ? MF(TextKey.M069, $"许可证文件：{licenseElement.Value.Trim()}") : licenseElement.Value.Trim())
            : Value("licenseUrl") is { } licenseUrl ? (UiMessage)licenseUrl : null;
        Uri? projectUrl = Value("projectUrl") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) ? parsed : null;
        var repositoryValue = (string?)metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("url");
        Uri? repositoryUrl = Uri.TryCreate(repositoryValue, UriKind.Absolute, out var repository) ? repository : null;

        var groups = new List<DependencyGroup>();
        if (metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies") is { } deps)
        {
            var groupElements = deps.Elements().Where(e => e.Name.LocalName == "group").ToList();
            foreach (var group in groupElements)
            {
                groups.Add(new DependencyGroup(ShortFramework((string?)group.Attribute("targetFramework")) ?? "", Dependencies(group)));
            }
            if (groupElements.Count == 0 && deps.Elements().Any(e => e.Name.LocalName == "dependency"))
            {
                groups.Add(new DependencyGroup("", Dependencies(deps)));
            }
        }
        var allFrameworks = frameworks.Count > 0
            ? frameworks
            : FrameworkFolders(groups.Select(g => g.Framework).Where(f => f.Length > 0));

        return new PackageDetails(Value("id") ?? id, Value("version") ?? version, Value("authors"), Value("description"),
            license, projectUrl, allFrameworks, groups, diagnostics)
        {
            Origin = origin,
            RepositoryUrl = repositoryUrl,
        };
    }

    private static List<DependencyItem> Dependencies(XElement parent) =>
        parent.Elements().Where(e => e.Name.LocalName == "dependency")
            .Select(e => new DependencyItem((string?)e.Attribute("id") ?? "", (string?)e.Attribute("version") ?? "(,)"))
            .Where(d => d.PackageId.Length > 0)
            .ToList();

    private static IReadOnlyList<string> FrameworkFolders(IEnumerable<string> names) =>
        names.Select(ShortFramework).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    internal static string? ShortFramework(string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework))
        {
            return null;
        }
        var f = framework.Trim();
        static string Digits(string v) => v.TrimStart('v', 'V');
        if (f.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            return "netstandard" + Digits(f[".NETStandard".Length..]);
        }
        if (f.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            var v = Digits(f[".NETCoreApp".Length..]);
            return int.TryParse(v.Split('.')[0], out var major) && major >= 5 ? "net" + v : "netcoreapp" + v;
        }
        if (f.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            return "net" + Digits(f[".NETFramework".Length..]).Replace(".", "", StringComparison.Ordinal);
        }
        return f.ToLowerInvariant();
    }

    private static string NormalizeVersion(string version)
    {
        var core = version.Split('+')[0];
        var dash = core.IndexOf('-');
        var numbers = (dash >= 0 ? core[..dash] : core).Split('.');
        if (numbers.Length == 4 && numbers[3] == "0")
        {
            numbers = numbers[..3];
        }
        return string.Join('.', numbers) + (dash >= 0 ? core[dash..] : "");
    }

    private static PackageDetails Empty(string id, string version, List<SourceDiagnostic> diagnostics) =>
        new(id, version, null, null, null, null, [], [], diagnostics);
}
