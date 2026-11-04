using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
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

internal sealed record PendingRecoveryFile(string Path, PendingFileState State, UiMessage Description);

/// <summary>An operation that neither committed nor rolled back, found at startup.</summary>
internal sealed record PendingRecovery(
    string ManifestPath,
    string BackupDirectory,
    UiMessage? Description,
    string? Phase,
    DateTimeOffset? StartedAt,
    IReadOnlyList<PendingRecoveryFile> Files,
    UiMessage? Error)
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
            return new PendingRecovery(manifestPath, directory, null, null, null, [], MF(TextKey.M037, $"恢复记录无法读取：{UiMessage.FromException(ex)}"));
        }

        var files = new List<PendingRecoveryFile>();
        foreach (var entry in manifest.Files)
        {
            files.Add(await ClassifyAsync(entry, ct).ConfigureAwait(false));
        }
        return new PendingRecovery(manifestPath, directory, manifest.LocalizedDescription
            ?? (manifest.Description is { } description ? (UiMessage)description : null), manifest.Phase, manifest.StartedAt, files, null);
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
            return new PendingRecoveryFile(entry.Path, PendingFileState.Unreadable, MF(TextKey.M038, $"无法读取当前文件：{UiMessage.FromException(ex)}"));
        }

        if (currentHash == entry.OriginalHash)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.Unchanged, M(TextKey.M039));
        }
        if (entry.Conflict)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.Conflict, entry.LocalizedMessage ?? entry.Message ?? M(TextKey.M040));
        }
        if (entry.OwnedHash is not null && currentHash == entry.OwnedHash)
        {
            return new PendingRecoveryFile(entry.Path, PendingFileState.OwnedChange, M(TextKey.M041));
        }
        return new PendingRecoveryFile(entry.Path, PendingFileState.Conflict,
            entry.OwnedHash is null
                ? M(TextKey.M042)
                : M(TextKey.M043));
    }
}
