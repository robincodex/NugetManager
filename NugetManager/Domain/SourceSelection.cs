using NugetManager.Packages;

namespace NugetManager.Domain;

internal sealed class SourceSelection : IEquatable<SourceSelection>
{
    private readonly string[] _names;
    public bool IsAll { get; }
    public static SourceSelection All { get; } = new(true, []);

    private SourceSelection(bool all, IEnumerable<string> names)
    {
        IsAll = all;
        _names = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static SourceSelection Subset(IEnumerable<string> names) => new(false, names);

    public IReadOnlyList<string> Resolve(IReadOnlyList<NuGetSource> sources) =>
        sources.Where(s => s.IsEnabled && (IsAll || _names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
            .Select(s => s.Name).ToArray();

    public SourceSelection Reconcile(IReadOnlyList<NuGetSource> sources) => IsAll ? All : Subset(Resolve(sources));

    public bool Equals(SourceSelection? other) => other is not null && IsAll == other.IsAll
        && _names.SequenceEqual(other._names, StringComparer.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is SourceSelection selection && Equals(selection);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsAll);
        foreach (var name in _names) hash.Add(name, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }
}
