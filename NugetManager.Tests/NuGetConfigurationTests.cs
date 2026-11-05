using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using NugetManager.Packages;
using NugetManager.Sdk;

namespace NugetManager.Tests;

public class NuGetConfigurationTests
{
    [Fact]
    public async Task DiscoveryAndAggregationRetainDeclarationsAcrossClear()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var machine = ws.Write("machine/offline.config", """<configuration><packageSources><add key="same" value="C:\feed" /></packageSources></configuration>""");
        var extra = ws.Write("user/config/extra.config", """<configuration><packageSources><add key="extra" value="https://example.test/v3/index.json" /></packageSources></configuration>""");
        var user = ws.Write("user/NuGet.Config", """<configuration><packageSources><add key="same" value="https://example.test/index.json" /></packageSources></configuration>""");
        var local = ws.Write("project/NuGet.Config", """<configuration><packageSources><clear/><add key="same" value="..\feed" allowInsecureConnections="true" /></packageSources><disabledPackageSources><add key="same" value="true" /></disabledPackageSources></configuration>""");
        var files = NuGetConfigReader.DiscoverFiles(Path.GetDirectoryName(local)!, user, Path.GetDirectoryName(machine)!);
        Assert.Contains(machine, files);
        Assert.Contains(extra, files);
        Assert.Contains(user, files);
        Assert.Contains(local, files);
        var rows = files.SelectMany(file => NuGetConfiguration.Read(file).Sources).ToList();
        Assert.Equal(3, rows.Count(row => row.Name == "same"));
        var row = Assert.Single(NuGetConfiguration.Read(local).Sources);
        Assert.Equal(@"..\feed", row.Location);
        Assert.False(row.Enabled);
        Assert.True(row.AllowInsecure);
    }

    [Fact]
    public async Task RenamePreservesSettingsAndCredentialsAndSdkReadsSavedSource()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var file = ws.Write("edit.config", """
            <configuration><!--keep--><config><add key="other" value="value"/></config>
            <packageSources><add key="old" value="https://example.test/index.json" protocolVersion="3" custom="keep"/></packageSources>
            <disabledPackageSources><add key="old" value="true" custom="keep"/></disabledPackageSources>
            <packageSourceCredentials><old><add key="Username" value="user" custom="keep"/><add key="Password" value="opaque"/><add key="ValidAuthenticationTypes" value="basic"/></old></packageSourceCredentials>
            <packageSourceMapping><packageSource key="old"><package pattern="Example.*"/></packageSource></packageSourceMapping></configuration>
            """);
        var snapshot = NuGetConfiguration.Read(file);
        var original = Assert.Single(snapshot.Sources);
        await NuGetConfiguration.SaveAsync(snapshot, original, original with
        {
            Name = "new name", Enabled = true, AllowInsecure = true, DisableTlsValidation = true,
        }, null, false, CancellationToken.None);
        var xml = XDocument.Load(file);
        Assert.Contains("keep", xml.ToString());
        Assert.Equal("opaque", (string?)xml.Descendants("new_x0020_name").Single().Elements("add").Single(e => (string?)e.Attribute("key") == "Password").Attribute("value"));
        Assert.Equal("new name", (string?)xml.Descendants("packageSource").Single().Attribute("key"));
        Assert.Equal("keep", (string?)xml.Descendants("packageSources").Single().Element("add")!.Attribute("custom"));
        Assert.Equal("false", (string?)xml.Descendants("disabledPackageSources").Single().Element("add")!.Attribute("value"));
        Assert.Equal("keep", (string?)xml.Descendants("disabledPackageSources").Single().Element("add")!.Attribute("custom"));
        Assert.Equal("keep", (string?)xml.Descendants("new_x0020_name").Single().Elements("add")
            .Single(e => (string?)e.Attribute("key") == "Username").Attribute("custom"));
        var result = await new ProcessRunner().RunAsync(new("dotnet", ["nuget", "list", "source", "--configfile", file],
            ws.Root, TimeSpan.FromSeconds(30), ws.Environment), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("new name", result.Stdout);
        snapshot = NuGetConfiguration.Read(file);
        await NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0], null, null, false, CancellationToken.None);
        xml = XDocument.Load(file);
        Assert.Empty(xml.Descendants("new_x0020_name"));
        Assert.Empty(xml.Descendants("packageSource"));
        Assert.Empty(xml.Descendants("disabledPackageSources").Single().Elements("add"));
        Assert.Equal("value", (string?)xml.Descendants("config").Single().Element("add")!.Attribute("value"));
    }

    [Fact]
    public async Task ChangedFileIsNotOverwritten()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var snapshot = NuGetConfiguration.Read(ws.Path_("NuGet.Config"));
        await File.AppendAllTextAsync(snapshot.Path, "\n<!--external-->");
        var before = await File.ReadAllBytesAsync(snapshot.Path);
        await Assert.ThrowsAsync<IOException>(() => NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0],
            snapshot.Sources[0] with { Enabled = false }, null, false, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(snapshot.Path));
    }

    [Fact]
    public async Task NewConfigurationCanBeCreatedAndSourceDeleted()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var file = ws.Path_("new/NuGet.Config");
        var empty = NuGetConfiguration.Read(file);
        var row = new ConfigSource("local", ws.FeedDirectory, true, false, false, "", false);
        await NuGetConfiguration.SaveAsync(empty, null, row, null, false, CancellationToken.None);
        var snapshot = NuGetConfiguration.Read(file);
        Assert.Single(snapshot.Sources);
        await NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0], null, null, false, CancellationToken.None);
        Assert.Empty(NuGetConfiguration.Read(file).Sources);
    }

    [Fact]
    public async Task MalformedAndDuplicateConfigurationsAreReported()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var broken = ws.Write("broken.config", "<configuration>");
        Assert.ThrowsAny<Exception>(() => NuGetConfiguration.Read(broken));
        var file = ws.Path_("NuGet.Config");
        var snapshot = NuGetConfiguration.Read(file);
        await Assert.ThrowsAsync<ArgumentException>(() => NuGetConfiguration.SaveAsync(snapshot, null, snapshot.Sources[0],
            null, false, CancellationToken.None));
    }

    [Fact]
    public async Task CredentialsMatchSdkEncryptionAndCanBeRetainedOrRemoved()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        const string password = "fixture-password-not-a-secret";
        var file = ws.Write("auth.config", """<configuration><packageSources><add key="private" value="https://example.test/index.json"/></packageSources></configuration>""");
        var sdk = await new ProcessRunner().RunAsync(new("dotnet",
            ["nuget", "update", "source", "private", "--configfile", file, "--username", "fixture", "--password", password],
            ws.Root, TimeSpan.FromSeconds(30), ws.Environment), CancellationToken.None);
        Assert.Equal(0, sdk.ExitCode);
        var sdkCipher = Password(file);
        Assert.Equal(password, Decrypt(sdkCipher));
        var snapshot = NuGetConfiguration.Read(file);
        var source = Assert.Single(snapshot.Sources);
        await NuGetConfiguration.SaveAsync(snapshot, source, source with { Enabled = false }, null, false, CancellationToken.None);
        Assert.Equal(sdkCipher, Password(file));
        snapshot = NuGetConfiguration.Read(file);
        await NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0], snapshot.Sources[0], password, false, CancellationToken.None);
        Assert.Equal(password, Decrypt(Password(file)));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(file));
        snapshot = NuGetConfiguration.Read(file);
        await NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0], snapshot.Sources[0] with { Username = "" },
            null, true, CancellationToken.None);
        Assert.False(NuGetConfiguration.Read(file).Sources[0].HasCredentials);
    }

    [Theory]
    [InlineData("packageSourceCredentials", "<target><add key=\"Password\" value=\"opaque\"/></target>")]
    [InlineData("disabledPackageSources", "<add key=\"target\" value=\"true\" custom=\"keep\"/>")]
    [InlineData("packageSourceMapping", "<packageSource key=\"target\"><package pattern=\"*\"/></packageSource>")]
    public async Task RenameDoesNotHijackOrphanSettings(string section, string content)
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var file = ws.Write("collision.config", $"<configuration><packageSources><add key=\"old\" value=\"https://example.test\"/></packageSources><{section}>{content}</{section}></configuration>");
        var before = await File.ReadAllBytesAsync(file);
        var snapshot = NuGetConfiguration.Read(file);
        await Assert.ThrowsAsync<ArgumentException>(() => NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0],
            snapshot.Sources[0] with { Name = "target" }, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => NuGetConfiguration.SaveAsync(snapshot, null,
            snapshot.Sources[0] with { Name = "target" }, null, false, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task UnsupportedRootAndUnsafeSourcesAreRejected()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        Assert.Throws<InvalidDataException>(() => NuGetConfiguration.Read(ws.Write("namespace.config",
            """<configuration xmlns="urn:unexpected"><packageSources/></configuration>""")));
        var snapshot = NuGetConfiguration.Read(ws.Path_("NuGet.Config"));
        foreach (var address in new[] { "http://example.test/v3/index.json", "https://user:password@example.test/index.json", "ftp://example.test" })
            await Assert.ThrowsAsync<ArgumentException>(() => NuGetConfiguration.SaveAsync(snapshot, null,
                new("unsafe", address, true, false, false, "", false), null, false, CancellationToken.None));
        await NuGetConfiguration.SaveAsync(snapshot, null, new("opt-in", "http://example.test", true, true, true, "", false),
            null, false, CancellationToken.None);
        Assert.True(NuGetConfiguration.Read(snapshot.Path).Sources.Single(s => s.Name == "opt-in").DisableTlsValidation);
    }

    [Fact]
    public async Task LockedAndCancelledSavesPreserveBytesAndCleanTemporaryFiles()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var snapshot = NuGetConfiguration.Read(ws.Path_("NuGet.Config"));
        var before = await File.ReadAllBytesAsync(snapshot.Path);
        using (var locked = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0],
                snapshot.Sources[0] with { Enabled = false }, null, false, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NuGetConfiguration.SaveAsync(snapshot, snapshot.Sources[0],
            snapshot.Sources[0] with { Enabled = false }, null, false, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(snapshot.Path));
        Assert.Empty(Directory.EnumerateFiles(ws.Root, ".nugetmgr-*.tmp"));
    }

    private static string Password(string file) => (string)XDocument.Load(file).Descendants("packageSourceCredentials")
        .Single().Descendants("add").Single(e => (string?)e.Attribute("key") == "Password").Attribute("value")!;
    private static string Decrypt(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(value), Encoding.UTF8.GetBytes("NuGet"), DataProtectionScope.CurrentUser));
    }
}
