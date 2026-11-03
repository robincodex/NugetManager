namespace NugetManager.Domain;

internal sealed record ProjectDescriptor(string Path, string Name);

internal sealed record Workspace(string Directory, string? SolutionPath, IReadOnlyList<ProjectDescriptor> Projects);

/// <summary>Null <see cref="ProjectPath"/> means every loaded project.</summary>
internal sealed record WorkspaceScope(string? ProjectPath)
{
    public static WorkspaceScope All { get; } = new((string?)null);

    public bool IsAll => ProjectPath is null;

    public bool Includes(string projectPath) =>
        ProjectPath is null || string.Equals(ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase);
}

internal static class PathUtil
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    public static bool AreEqual(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
