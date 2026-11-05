using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;

namespace NugetManager.Packages;

internal sealed record ConfigSource(string Name, string Location, bool Enabled, bool AllowInsecure,
    bool DisableTlsValidation, string Username, bool HasCredentials);

internal sealed class ConfigSnapshot(string path, byte[]? original, IReadOnlyList<ConfigSource> sources)
{
    public string Path { get; } = path;
    internal byte[]? Original { get; } = original;
    public IReadOnlyList<ConfigSource> Sources { get; } = sources;
    public bool Exists => Original is not null;
    public bool IsReadOnly { get; } = original is not null && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
}

internal static class NuGetConfiguration
{
    private static readonly SemaphoreSlim SaveGate = new(1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NuGet");

    public static ConfigSnapshot Read(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) return new(path, null, []);
        var bytes = File.ReadAllBytes(path);
        var root = Parse(bytes);
        var sources = new List<ConfigSource>();
        foreach (var entry in root.Element("packageSources")?.Elements("add") ?? [])
        {
            var name = (string?)entry.Attribute("key");
            var location = (string?)entry.Attribute("value");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location)
                || sources.Any(source => Equal(source.Name, name)))
                throw UiErrors.InvalidData(M(TextKey.ConfigInvalid));
            var credentials = root.Element("packageSourceCredentials")?.Elements()
                .SingleOrDefault(element => Equal(XmlConvert.DecodeName(element.Name.LocalName), name));
            var disabled = root.Element("disabledPackageSources")?.Elements("add")
                .LastOrDefault(element => Equal((string?)element.Attribute("key"), name));
            sources.Add(new(name, location, !Flag(disabled, "value"), Flag(entry, "allowInsecureConnections"),
                Flag(entry, "disableTLSCertificateValidation"), Value(credentials, "Username") ?? "", credentials is not null));
        }
        return new(path, bytes, sources);
    }

    public static async Task SaveAsync(ConfigSnapshot snapshot, ConfigSource? original, ConfigSource? replacement,
        string? password, bool removeCredentials, CancellationToken ct)
    {
        await SaveGate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var root = snapshot.Original is null ? new XElement("configuration") : Parse(snapshot.Original);
            var document = root.Document ?? new XDocument(root);
            if (replacement is not null) Validate(replacement);
            var sources = Section(root, "packageSources");
            var entry = original is null ? null : sources.Elements("add")
                .SingleOrDefault(element => Equal((string?)element.Attribute("key"), original.Name));
            if (original is not null && entry is null) throw UiErrors.IO(M(TextKey.ConfigChanged));
            if (replacement is not null && sources.Elements("add").Any(element => element != entry
                    && Equal((string?)element.Attribute("key"), replacement.Name)))
                throw UiErrors.Argument(M(TextKey.ConfigDuplicate));
            var oldName = original?.Name;
            if (replacement is not null && !Equal(oldName, replacement.Name)
                && ((root.Element("packageSourceCredentials")?.Elements().Any(element =>
                        Equal(XmlConvert.DecodeName(element.Name.LocalName), replacement.Name)) ?? false)
                    || (root.Element("disabledPackageSources")?.Elements("add").Any(element =>
                        Equal((string?)element.Attribute("key"), replacement.Name)) ?? false)
                    || (root.Element("packageSourceMapping")?.Elements("packageSource").Any(element =>
                        Equal((string?)element.Attribute("key"), replacement.Name)) ?? false)))
                throw UiErrors.Argument(M(TextKey.ConfigDuplicate));
            var credential = root.Element("packageSourceCredentials")?.Elements()
                .SingleOrDefault(element => oldName is not null && Equal(XmlConvert.DecodeName(element.Name.LocalName), oldName));
            if (replacement is null)
            {
                entry?.Remove();
                credential?.Remove();
                RemoveKeys(root.Element("disabledPackageSources"), oldName);
                foreach (var mapping in root.Element("packageSourceMapping")?.Elements("packageSource")
                             .Where(element => Equal((string?)element.Attribute("key"), oldName)).ToList() ?? [])
                    mapping.Remove();
            }
            else
            {
                if (entry is null) { entry = new XElement("add"); sources.Add(entry); }
                entry.SetAttributeValue("key", replacement.Name);
                entry.SetAttributeValue("value", replacement.Location.Trim());
                entry.SetAttributeValue("allowInsecureConnections", replacement.AllowInsecure ? "true" : null);
                entry.SetAttributeValue("disableTLSCertificateValidation", replacement.DisableTlsValidation ? "true" : null);
                var disabled = Section(root, "disabledPackageSources");
                var disabledEntry = disabled.Elements("add").SingleOrDefault(element =>
                    Equal((string?)element.Attribute("key"), oldName));
                if (disabledEntry is null) { disabledEntry = new XElement("add"); disabled.Add(disabledEntry); }
                disabledEntry.SetAttributeValue("key", replacement.Name);
                disabledEntry.SetAttributeValue("value", replacement.Enabled ? "false" : "true");
                foreach (var mapping in root.Element("packageSourceMapping")?.Elements("packageSource")
                             .Where(element => Equal((string?)element.Attribute("key"), oldName)).ToList() ?? [])
                    mapping.SetAttributeValue("key", replacement.Name);
                if (removeCredentials)
                    credential?.Remove();
                else if (credential is not null || !string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(replacement.Username))
                {
                    if (credential is null)
                    {
                        credential = new XElement(XmlConvert.EncodeLocalName(replacement.Name));
                        Section(root, "packageSourceCredentials").Add(credential);
                    }
                    credential.Name = XmlConvert.EncodeLocalName(replacement.Name);
                    if (!string.IsNullOrWhiteSpace(replacement.Username)) SetValue(credential, "Username", replacement.Username);
                    if (!string.IsNullOrEmpty(password))
                    {
                        if (string.IsNullOrWhiteSpace(replacement.Username)) throw UiErrors.Argument(M(TextKey.ConfigUsernameRequired));
                        SetValue(credential, "Password", Encrypt(password));
                        RemoveKeys(credential, "ClearTextPassword");
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            var directory = System.IO.Path.GetDirectoryName(snapshot.Path)!;
            Directory.CreateDirectory(directory);
            temporary = System.IO.Path.Combine(directory, $".nugetmgr-{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
                document.Save(writer);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            var current = File.Exists(snapshot.Path) ? await File.ReadAllBytesAsync(snapshot.Path, ct).ConfigureAwait(false) : null;
            if ((current is null) != (snapshot.Original is null)
                || current is not null && !current.AsSpan().SequenceEqual(snapshot.Original))
                throw UiErrors.IO(M(TextKey.ConfigChanged));
            ct.ThrowIfCancellationRequested();
            if (snapshot.Original is null) File.Move(temporary, snapshot.Path);
            else File.Replace(temporary, snapshot.Path, null);
            temporary = null;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { SaveGate.Release(); }
        }
    }

    internal static void Validate(ConfigSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Location))
            throw UiErrors.Argument(M(TextKey.ConfigNameAddressRequired));
        var location = Environment.ExpandEnvironmentVariables(source.Location.Trim());
        if (location.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http" or "file") || uri.UserInfo.Length > 0)
                throw UiErrors.Argument(M(TextKey.ConfigAddressInvalid));
            if (uri.Scheme == "http" && !source.AllowInsecure)
                throw UiErrors.Argument(M(TextKey.ConfigHttpRequiresOptIn));
        }
        else
            _ = System.IO.Path.GetFullPath(location);
    }

    internal static string Encrypt(string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var bytes = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static XElement Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        return document.Root is { } root && root.Name == "configuration"
            ? root : throw UiErrors.InvalidData(M(TextKey.ConfigInvalid));
    }

    private static bool Equal(string? first, string? second) =>
        first is not null && second is not null && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool Flag(XElement? element, string attribute) => Equal((string?)element?.Attribute(attribute), "true");
    private static string? Value(XElement? section, string key) =>
        (string?)section?.Elements("add").LastOrDefault(element => Equal((string?)element.Attribute("key"), key))?.Attribute("value");
    private static XElement Section(XElement root, string name)
    {
        if (root.Element(name) is { } section) return section;
        section = new XElement(name);
        root.Add(section);
        return section;
    }
    private static void RemoveKeys(XElement? section, string? key)
    {
        foreach (var element in section?.Elements("add").Where(element => Equal((string?)element.Attribute("key"), key)).ToList() ?? [])
            element.Remove();
    }
    private static void SetValue(XElement section, string key, string value)
    {
        var entry = section.Elements("add").SingleOrDefault(element => Equal((string?)element.Attribute("key"), key));
        if (entry is null) { entry = new XElement("add", new XAttribute("key", key)); section.Add(entry); }
        entry.SetAttributeValue("value", value);
    }
}
