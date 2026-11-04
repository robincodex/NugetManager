using System.Diagnostics;
using System.Text.RegularExpressions;
using NugetManager.Domain;
using NugetManager.Packages;

namespace NugetManager.Presentation;

internal sealed record PackageLinkIdentity(string WorkspaceDirectory, string PackageId, string Version, string? SourceName);
internal sealed record PackageLinks(Uri? NuGetPage, Uri? GitHubRepository);
internal sealed record PackageLinkState(PackageLinkIdentity? Identity, PackageLinks Links, bool Loading, string? Error);

internal static partial class PackageLinkResolver
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPart();

    public static bool IsWebUri(Uri uri) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0
        && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);

    public static PackageLinks Resolve(PackageLinkIdentity identity, PackageDetails? details)
    {
        if (details is null || !string.Equals(identity.PackageId, details.PackageId, StringComparison.OrdinalIgnoreCase)
            || NuGetVersionComparer.Instance.Compare(identity.Version, details.Version) != 0
            || identity.SourceName is not null && !string.Equals(identity.SourceName, details.SourceName, StringComparison.OrdinalIgnoreCase))
            return new(null, null);
        Uri? page = details.PackagePageUrl is { } supplied && IsWebUri(supplied) ? supplied : null;
        if (page is null && details.SourceLocation is { } location && PopularPackageReader.IsOfficialSource(location))
            page = new Uri($"https://www.nuget.org/packages/{Uri.EscapeDataString(identity.PackageId)}/{Uri.EscapeDataString(identity.Version)}");
        var repository = details.RepositoryUrl is not null ? GitHubRepository(details.RepositoryUrl.OriginalString)
            : details.ProjectUrl is not null ? GitHubRepository(details.ProjectUrl.OriginalString) : null;
        return new(page, repository);
    }

    public static Uri? GitHubRepository(string input)
    {
        if (input.StartsWith("git+https://", StringComparison.OrdinalIgnoreCase)) input = input[4..];
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || !IsWebUri(uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || input.Contains('\\') || uri.AbsolutePath.Contains('%'))
            return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2) return null;
        var owner = parts[0];
        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!RepositoryPart().IsMatch(owner) || !RepositoryPart().IsMatch(repository) || repository is "." or "..") return null;
        return new Uri($"https://github.com/{owner}/{repository}");
    }
}

internal interface IExternalLinkOpener
{
    void Open(Uri uri);
}

internal sealed class ExternalLinkOpener : IExternalLinkOpener
{
    public void Open(Uri uri)
    {
        if (!PackageLinkResolver.IsWebUri(uri)) throw new ArgumentException("Only absolute HTTP(S) links without credentials can be opened.", nameof(uri));
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })
            ?? throw new IOException("The system browser did not start.");
    }
}

internal sealed class PackageLinkMenu(IPackageMetadataReader metadata, IExternalLinkOpener opener) : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _pending;
    public PackageLinkState State { get; private set; } = new(null, new(null, null), false, null);
    public event Action<PackageLinkState>? Changed;

    public async Task LoadAsync(Workspace workspace, PackageLinkIdentity identity, CancellationToken ct)
    {
        Close();
        var generation = Interlocked.Increment(ref _generation);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pending = pending;
        Set(new(identity, new(null, null), true, null));
        try
        {
            var details = await metadata.ReadAsync(workspace, identity.PackageId, identity.Version, identity.SourceName, pending.Token);
            if (generation == Interlocked.Read(ref _generation))
                Set(new(identity, PackageLinkResolver.Resolve(identity, details), false,
                    details.Diagnostics.Count == 0 ? null : string.Join(Environment.NewLine, details.Diagnostics.Select(d => d.Message))));
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            if (generation == Interlocked.Read(ref _generation)) Set(State with { Loading = false });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == Interlocked.Read(ref _generation)) Set(new(identity, new(null, null), false, ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }

    public Task OpenAsync(Uri uri)
    {
        if (!PackageLinkResolver.IsWebUri(uri)) return Task.FromException(new ArgumentException("Only absolute HTTP(S) links can be opened.", nameof(uri)));
        try { opener.Open(uri); return Task.CompletedTask; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return Task.FromException(ex); }
    }

    public void Close()
    {
        Interlocked.Increment(ref _generation);
        _pending?.Cancel();
        _pending = null;
        Set(new(null, new(null, null), false, null));
    }

    private void Set(PackageLinkState state) { State = state; Changed?.Invoke(state); }
    public void Dispose() => Close();
}
