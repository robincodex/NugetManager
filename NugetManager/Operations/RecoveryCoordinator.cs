using NugetManager.Sdk;

namespace NugetManager.Operations;

internal enum PendingFileState
{
    /// <summary>Current content is byte-identical to the backup; nothing to undo.</summary>
    Unchanged,

    /// <summary>Current content is exactly what the interrupted operation wrote; it can be reverted safely.</summary>
    OwnedChange,

    /// <summary>Content cannot be proven to be ours; it will never be overwritten automatically.</summary>
    Conflict,

    Unreadable,
}

internal sealed record PendingRecoveryFile(string Path, PendingFileState State, string Description);

/// <summary>An operation that neither committed nor rolled back, found at startup.</summary>
internal sealed record PendingRecovery(
    string ManifestPath,
    string BackupDirectory,
    string? Description,
    string? Phase,
    DateTimeOffset? StartedAt,
    IReadOnlyList<PendingRecoveryFile> Files,
    string? Error)
{
    public bool CanRecover => Error is null && Files.Any(f => f.State == PendingFileState.OwnedChange);
}

/// <summary>Startup entry for unfinished operations. Classifies files with the same rules FileTransaction uses to restore.</summary>
internal static class RecoveryCoordinator
{
    public static async Task<IReadOnlyList<PendingRecovery>> FindAsync(string recoveryDirectory, CancellationToken ct)
    {
        var result = new List<PendingRecovery>();
        foreach (var manifestPath in await FileTransaction.FindIncompleteAsync(recoveryDirectory, ct).ConfigureAwait(false))
        {
            result.Add(await InspectAsync(manifestPath, ct).ConfigureAwait(false));
        }
        return result;
    }

    /// <summary>Reverts only files proven to hold the operation's own result; conflicts stay untouched and keep the record.</summary>
    public static Task<IReadOnlyList<FileRecovery>> RecoverAsync(PendingRecovery pending, CancellationToken ct) =>
        FileTransaction.RecoverAsync(pending.ManifestPath, ct);

    /// <summary>Keeps every file as it is now and deletes the backup record.</summary>
    public static Task DiscardAsync(PendingRecovery pending, CancellationToken ct) =>
        FileTransaction.DiscardAsync(pending.ManifestPath, ct);

    private static async Task<PendingRecovery> InspectAsync(string manifestPath, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        RecoveryManifest manifest;
        try
        {
            manifest = await FileTransaction.ReadManifestAsync(manifestPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new PendingRecovery(manifestPath, directory, null, null, null, [], $"恢复记录无法读取：{ex.Message}");
        }

        var files = new List<PendingRecoveryFile>();
        foreach (var entry in manifest.Files)
        {
            files.Add(await ClassifyAsync(entry, ct).ConfigureAwait(false));
        }
        return new PendingRecovery(manifestPath, directory, manifest.Description, manifest.Phase, manifest.StartedAt, files, null);
    }

    private static async Task<PendingRecoveryFile> ClassifyAsync(RecoveryEntry entry, CancellationToken ct)
    {
        string currentHash;
        try
        {
            currentHash = FileTransaction.Hash(File.Exists(entry.Path) ? await File.ReadAllBytesAsync(entry.Path, ct).ConfigureAwait(false) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.Unreadable, $"无法读取当前文件：{ex.Message}");
        }

        if (currentHash == entry.OriginalHash)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.Unchanged, "与操作前一致，无需恢复");
        }
        if (entry.Conflict)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.Conflict, entry.Message ?? "文件存在外部修改，不会自动覆盖");
        }
        if (entry.OwnedHash is not null && currentHash == entry.OwnedHash)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.OwnedChange, "内容为该操作写入的结果，可撤回到操作前");
        }
        return new PendingRecoveryFile(entry.Path, PendingFileState.Conflict,
            entry.OwnedHash is null
                ? "操作中断时尚未确认此文件的写入归属，为避免覆盖他人改动不会自动撤回"
                : "操作写入后文件又被修改，为避免覆盖他人改动不会自动撤回");
    }
}
