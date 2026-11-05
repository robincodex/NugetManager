using System.Xml;
using System.Xml.Linq;

namespace NugetManager.Packages;

internal sealed record NuGetSource(string Name, string Location, bool IsEnabled, bool HasCredentials)
{
    public bool IsHttp => Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public bool MatchesLocation(string location) =>
        string.Equals(NormalizeLocation(Location), NormalizeLocation(location), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLocation(string location) =>
        (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : location).TrimEnd('/', '\\');
}

internal sealed record NuGetSettings(
    IReadOnlyList<NuGetSource> Sources,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SourceMapping,
    string? GlobalPackagesFolder)
{
    /// <summary>Returns enabled sources allowed to serve the package under package source mapping rules.</summary>
    public IReadOnlyList<NuGetSource> SourcesFor(string packageId)
    {
        var enabled = Sources.Where(s => s.IsEnabled).ToList();
        if (SourceMapping.Count == 0)
        {
            return enabled;
        }
        var best = -1;
        var allowed = new List<NuGetSource>();
        foreach (var source in enabled)
        {
            if (!SourceMapping.TryGetValue(source.Name, out var patterns))
            {
                continue;
            }
            var score = patterns.Select(p => Score(p, packageId)).DefaultIfEmpty(-1).Max();
            if (score < 0)
            {
                continue;
            }
            if (score > best)
            {
                best = score;
                allowed.Clear();
            }
            if (score == best)
            {
                allowed.Add(source);
            }
        }
        return allowed;
    }

    private static int Score(string pattern, string id)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix.Length : -1;
        }
        return string.Equals(pattern, id, StringComparison.OrdinalIgnoreCase) ? int.MaxValue : -1;
    }
}

/// <summary>Reads the NuGet.Config hierarchy for a directory using NuGet's merge rules.</summary>
internal static class NuGetConfigReader
{
    private static readonly string[] FileNames = ["NuGet.Config", "nuget.config", "NuGet.config"];

    public static string DefaultUserConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config");

    public static NuGetSettings Load(string directory, string? userConfigPath = null)
    {
        var files = DiscoverFiles(directory, userConfigPath);
        return LoadFiles(files);
    }

    public static IReadOnlyList<string> DiscoverFiles(string directory, string? userConfigPath = null, string? machineConfigDirectory = null)
    {
        var files = new List<string>();
        var userConfig = userConfigPath ?? DefaultUserConfigPath;
        // An explicit user path isolates callers (including SDK fixtures) from machine settings.
        var machine = machineConfigDirectory ?? (userConfigPath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "NuGet", "Config") : null);
        if (machine is not null && Directory.Exists(machine))
            files.AddRange(Directory.EnumerateFiles(machine, "*.config").Order(StringComparer.OrdinalIgnoreCase));
        var additional = Path.Combine(Path.GetDirectoryName(userConfig)!, "config");
        if (Directory.Exists(additional))
            files.AddRange(Directory.EnumerateFiles(additional, "*.config").Order(StringComparer.OrdinalIgnoreCase));
        if (File.Exists(userConfig))
        {
            files.Add(userConfig);
        }
        var chain = new List<string>();
        for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
        {
            var file = FileNames.Select(n => Path.Combine(dir.FullName, n)).FirstOrDefault(File.Exists);
            if (file is not null)
            {
                chain.Add(file);
            }
        }
        chain.Reverse();
        files.AddRange(chain.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(userConfig), StringComparison.OrdinalIgnoreCase)));
        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static NuGetSettings LoadFiles(IReadOnlyList<string> files)
    {
        var sources = new List<(string Name, string Location)>();
        var disabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var credentials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapping = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? globalPackages = null;

        foreach (var file in files)
        {
            var root = LoadXml(file)?.Root;
            if (root is null)
            {
                continue;
            }
            var baseDir = Path.GetDirectoryName(file)!;
            foreach (var section in root.Elements())
            {
                switch (section.Name.LocalName)
                {
                    case "packageSources":
                        foreach (var e in section.Elements())
                        {
                            var key = (string?)e.Attribute("key");
                            if (e.Name.LocalName == "clear")
                            {
                                sources.Clear();
                            }
                            else if (key is not null && e.Name.LocalName == "remove")
                            {
                                sources.RemoveAll(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase));
                            }
                            else if (key is not null && e.Name.LocalName == "add" && (string?)e.Attribute("value") is { } value)
                            {
                                var location = ResolveLocation(value, baseDir);
                                var index = sources.FindIndex(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase));
                                if (index >= 0)
                                {
                                    sources[index] = (key, location);
                                }
                                else
                                {
                                    sources.Add((key, location));
                                }
                            }
                        }
                        break;
                    case "disabledPackageSources":
                        foreach (var e in section.Elements())
                        {
                            if (e.Name.LocalName == "clear")
                            {
                                disabled.Clear();
                            }
                            else if ((string?)e.Attribute("key") is { } key)
                            {
                                disabled[key] = e.Name.LocalName == "add"
                                    && string.Equals((string?)e.Attribute("value"), "true", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        break;
                    case "packageSourceCredentials":
                        foreach (var e in section.Elements())
                        {
                            // Element names are XML-encoded source keys; only presence is recorded, never the secret.
                            credentials.Add(XmlConvert.DecodeName(e.Name.LocalName));
                        }
                        break;
                    case "packageSourceMapping":
                        foreach (var e in section.Elements())
                        {
                            if (e.Name.LocalName == "clear")
                            {
                                mapping.Clear();
                            }
                            else if (e.Name.LocalName == "packageSource" && (string?)e.Attribute("key") is { } key)
                            {
                                if (!mapping.TryGetValue(key, out var patterns))
                                {
                                    mapping[key] = patterns = [];
                                }
                                patterns.AddRange(e.Elements().Where(p => p.Name.LocalName == "package")
                                    .Select(p => (string?)p.Attribute("pattern")).OfType<string>());
                            }
                        }
                        break;
                    case "config":
                        foreach (var e in section.Elements().Where(e => e.Name.LocalName == "add"))
                        {
                            if (string.Equals((string?)e.Attribute("key"), "globalPackagesFolder", StringComparison.OrdinalIgnoreCase)
                                && (string?)e.Attribute("value") is { } value)
                            {
                                globalPackages = ResolveLocation(value, baseDir);
                            }
                        }
                        break;
                }
            }
        }

        return new NuGetSettings(
            sources.Select(s => new NuGetSource(s.Name, s.Location, !disabled.GetValueOrDefault(s.Name), credentials.Contains(s.Name))).ToList(),
            mapping.ToDictionary(m => m.Key, m => (IReadOnlyList<string>)m.Value, StringComparer.OrdinalIgnoreCase),
            globalPackages);
    }

    private static string ResolveLocation(string value, string baseDir)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(expanded, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : expanded;
        }
        return Path.GetFullPath(Path.Combine(baseDir, expanded));
    }

    internal static XDocument? LoadXml(string file)
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
