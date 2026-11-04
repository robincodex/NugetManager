using NugetManager.Presentation;

namespace NugetManager.Operations;

/// <summary>Snapshot of a file before an operation. Null bytes mean the file did not exist.</summary>
internal sealed record PlannedFile(string Path, byte[]? OriginalBytes);

internal enum RecoveryStatus
{
    Restored,
    Conflict,
    Failed,
}

internal sealed record FileRecovery(string Path, RecoveryStatus Status, UiMessage? Message);

/// <summary>Decides whether a change between two file states is one this operation is allowed to make.</summary>
internal delegate bool ChangeValidator(string path, byte[]? before, byte[]? after);

internal enum PackageAction
{
    Upgrade,
    Install,
    Uninstall,
    Downgrade,
    SwitchVersion,
}

internal sealed record PackageChange(PackageAction Action, string ProjectPath, string PackageId, string? Version);

/// <summary>One SDK write command. Shared central entries produce a single command on a representative project.</summary>
internal sealed record PlannedCommand(PackageAction Action, string ProjectPath, string PackageId, string? Version, UiMessage Description);

internal sealed record OperationPlan(
    IReadOnlyList<PackageChange> Changes,
    IReadOnlyList<string> Files,
    IReadOnlyList<Domain.ProjectDescriptor> AffectedProjects,
    IReadOnlyList<UiMessage> Warnings,
    IReadOnlyList<UiMessage> Blockers)
{
    public IReadOnlyList<PlannedCommand> Commands { get; init; } = [];

    /// <summary>Content hash of every planned file when the preview was produced.</summary>
    public IReadOnlyDictionary<string, string> Fingerprints { get; init; } = new Dictionary<string, string>();

    public IReadOnlySet<string> PackageIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> LockFiles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Evaluations of the affected projects, used for restore after a rollback.</summary>
    public IReadOnlyList<Domain.ProjectEvaluation> AffectedEvaluations { get; init; } = [];

    public bool CanExecute => Blockers.Count == 0 && Commands.Count > 0;
}

internal enum OperationStatus
{
    Succeeded,
    RolledBack,
    RecoveryRequired,
}

internal sealed record OperationResult(OperationStatus Status, UiMessage? Error, IReadOnlyList<FileRecovery> Recovery)
{
    public string? RecoveryManifestPath { get; init; }
}

/// <summary>Planned files changed after the preview; nothing was written.</summary>
internal sealed class PlanStaleException(UiMessage message) : Exception(message.ToString()), IUiException
{
    public UiMessage UiMessage { get; } = message;
}
