namespace NugetManager.Operations;

/// <summary>Snapshot of a file before an operation. Null bytes mean the file did not exist.</summary>
internal sealed record PlannedFile(string Path, byte[]? OriginalBytes);

internal enum RecoveryStatus
{
    Restored,
    Conflict,
    Failed,
}

internal sealed record FileRecovery(string Path, RecoveryStatus Status, string? Message);

/// <summary>Decides whether a change between two file states is one this operation is allowed to make.</summary>
internal delegate bool ChangeValidator(string path, byte[]? before, byte[]? after);
