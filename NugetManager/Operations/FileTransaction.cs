using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
using System.Security.Cryptography;
using System.Text.Json;
using NugetManager.Sdk;

namespace NugetManager.Operations;

/// <summary>
/// Byte-exact backup of files touched by a package operation, persisted so a crash can be recovered at next start.
/// A file is only restored when its current content is provably the original, our own recorded result, or a change
/// the validator recognises as ours; anything else is reported as a conflict and left untouched.
/// </summary>
internal sealed class FileTransaction : IAsyncDisposable
{
    private const string ManifestName = "manifest.json";
    private const string AbsentHash = "absent";

    private readonly string _directory;
    private readonly RecoveryManifest _manifest;
    private readonly ChangeValidator? _validator;
    private readonly Dictionary<string, byte[]?> _originals;
    private bool _finished;

    private FileTransaction(string directory, RecoveryManifest manifest, ChangeValidator? validator, Dictionary<string, byte[]?> originals)
    {
        _directory = directory;
        _manifest = manifest;
        _validator = validator;
        _originals = originals;
    }

    public string ManifestPath => Path.Combine(_directory, ManifestName);

    public bool HasConflicts => _manifest.Files.Any(f => f.Conflict);

    public IReadOnlyList<PlannedFile> Files =>
        _manifest.Files.Select(f => new PlannedFile(f.Path, _originals[f.Path]?.ToArray())).ToList();

    public static async Task<FileTransaction> BeginAsync(IReadOnlyList<string> paths, string recoveryDirectory, CancellationToken ct,
        ChangeValidator? validator = null, UiMessage? description = null)
    {
        var id = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..27];
        var directory = Path.Combine(Path.GetFullPath(recoveryDirectory), id);
        Directory.CreateDirectory(directory);
        var manifest = new RecoveryManifest
        {
            OperationId = id,
            Description = description?.ToString(),
            LocalizedDescription = description,
            Phase = "Prepared",
            StartedAt = DateTimeOffset.Now,
        };
        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var index = 0;
            foreach (var raw in paths)
            {
                var path = Path.GetFullPath(raw);
                if (originals.ContainsKey(path))
                {
                    continue;
                }
                var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
                var entry = new RecoveryEntry { Path = path, Existed = bytes is not null, OriginalHash = Hash(bytes) };
                if (bytes is not null)
                {
                    entry.BackupFile = $"{index++}.bak";
                    var backup = Path.Combine(directory, entry.BackupFile);
                    await File.WriteAllBytesAsync(backup, bytes, ct).ConfigureAwait(false);
                    if (Hash(await File.ReadAllBytesAsync(backup, ct).ConfigureAwait(false)) != entry.OriginalHash)
                    {
                        throw UiErrors.IO(MF(TextKey.M001, $"备份 {path} 校验失败。"));
                    }
                }
                originals[path] = bytes;
                manifest.Files.Add(entry);
            }
            await SaveAsync(directory, manifest).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
        return new FileTransaction(directory, manifest, validator, originals);
    }

    /// <summary>Called right after the write commands finish: records which current states belong to this operation.</summary>
    public async Task RecordOwnedChangesAsync(CancellationToken ct)
    {
        foreach (var entry in _manifest.Files)
        {
            var current = await ReadAsync(entry.Path, ct).ConfigureAwait(false);
            var currentHash = Hash(current);
            if (currentHash == entry.OriginalHash || _validator is null || _validator(entry.Path, _originals[entry.Path], current))
            {
                entry.OwnedHash = currentHash;
            }
            else
            {
                entry.Conflict = true;
                entry.LocalizedMessage = M(TextKey.M002);
                entry.Message = entry.LocalizedMessage.ToString();
            }
        }
        _manifest.Phase = "Applied";
        await SaveAsync(_directory, _manifest).ConfigureAwait(false);
    }

    /// <summary>Opens another write window (e.g. restore). Its changes are judged by the validator until recorded.</summary>
    public async Task BeginWriteWindowAsync()
    {
        _manifest.Phase = "Writing";
        await SaveAsync(_directory, _manifest).ConfigureAwait(false);
    }

    /// <summary>Restores every provably-owned file. Ignores the caller's token so cancellation cannot interrupt recovery.</summary>
    public async Task<IReadOnlyList<FileRecovery>> RollbackAsync(CancellationToken ct)
    {
        _ = ct;
        _finished = true;
        return await RestoreAsync(_directory, _manifest, _validator, _originals).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _finished = true;
        _manifest.Phase = "Committed";
        await SaveAsync(_directory, _manifest).ConfigureAwait(false);
        TryDeleteDirectory(_directory);
    }

    /// <summary>Leaves the recovery record in place when neither commit nor rollback completed.</summary>
    public ValueTask DisposeAsync()
    {
        if (!_finished && File.Exists(ManifestPath))
        {
            _manifest.Phase = "Interrupted";
            try
            {
                SaveAsync(_directory, _manifest).GetAwaiter().GetResult();
            }
            catch (IOException)
            {
            }
        }
        return ValueTask.CompletedTask;
    }

    public static Task<IReadOnlyList<string>> FindIncompleteAsync(string recoveryDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(recoveryDirectory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        IReadOnlyList<string> found = Directory.EnumerateDirectories(recoveryDirectory)
            .Select(d => Path.Combine(Path.GetFullPath(d), ManifestName))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(found);
    }

    public static async Task<RecoveryManifest> ReadManifestAsync(string manifestPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync(stream, SdkJsonContext.Default.RecoveryManifest, ct).ConfigureAwait(false)
            ?? throw UiErrors.InvalidData(MF(TextKey.M003, $"恢复记录无效：{manifestPath}"));
    }

    /// <summary>Startup recovery: restores files that are still original or exactly in the recorded owned state.</summary>
    public static async Task<IReadOnlyList<FileRecovery>> RecoverAsync(string manifestPath, CancellationToken ct)
    {
        var manifest = await ReadManifestAsync(manifestPath, ct).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(manifestPath)!;
        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            originals[entry.Path] = entry.Existed && entry.BackupFile is not null
                ? await File.ReadAllBytesAsync(Path.Combine(directory, entry.BackupFile), ct).ConfigureAwait(false)
                : null;
            if (Hash(originals[entry.Path]) != entry.OriginalHash)
            {
                throw UiErrors.InvalidData(MF(TextKey.M004, $"{entry.Path} 的备份已损坏，无法自动恢复。备份目录：{directory}"));
            }
        }
        return await RestoreAsync(directory, manifest, null, originals, trustUnrecorded: false).ConfigureAwait(false);
    }

    /// <summary>Accepts the current files as they are and deletes the recovery record.</summary>
    public static Task DiscardAsync(string manifestPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TryDeleteDirectory(Path.GetDirectoryName(manifestPath)!);
        return Task.CompletedTask;
    }

    private static async Task<IReadOnlyList<FileRecovery>> RestoreAsync(string directory, RecoveryManifest manifest, ChangeValidator? validator,
        Dictionary<string, byte[]?> originals, bool trustUnrecorded = true)
    {
        var results = new List<FileRecovery>();
        var windowOpen = manifest.Phase is "Prepared" or "Writing";
        foreach (var entry in manifest.Files)
        {
            FileRecovery result;
            try
            {
                var current = await ReadAsync(entry.Path, CancellationToken.None).ConfigureAwait(false);
                var currentHash = Hash(current);
                var original = originals[entry.Path];
                if (currentHash == entry.OriginalHash)
                {
                    result = new FileRecovery(entry.Path, RecoveryStatus.Restored, null);
                }
                else if (entry.Conflict)
                {
                    result = new FileRecovery(entry.Path, RecoveryStatus.Conflict, entry.LocalizedMessage ?? entry.Message ?? M(TextKey.M005));
                }
                else if (currentHash == entry.OwnedHash
                         || (trustUnrecorded && windowOpen && (validator is null || validator(entry.Path, original, current))))
                {
                    await WriteOriginalAsync(entry.Path, original).ConfigureAwait(false);
                    result = new FileRecovery(entry.Path, RecoveryStatus.Restored, null);
                }
                else
                {
                    result = new FileRecovery(entry.Path, RecoveryStatus.Conflict,
                        M(TextKey.M006));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = new FileRecovery(entry.Path, RecoveryStatus.Failed, UiMessage.FromException(ex));
            }
            entry.Status = result.Status.ToString();
            entry.LocalizedMessage = result.Message;
            entry.Message = result.Message?.ToString();
            results.Add(result);
        }

        if (results.All(r => r.Status == RecoveryStatus.Restored))
        {
            TryDeleteDirectory(directory);
        }
        else
        {
            manifest.Phase = "RecoveryRequired";
            try
            {
                await SaveAsync(directory, manifest).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
        return results;
    }

    private static async Task WriteOriginalAsync(string path, byte[]? original)
    {
        if (original is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".nm-restore";
        await File.WriteAllBytesAsync(temp, original).ConfigureAwait(false);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;

    internal static string Hash(byte[]? bytes) => bytes is null ? AbsentHash : Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task SaveAsync(string directory, RecoveryManifest manifest)
    {
        var path = Path.Combine(directory, ManifestName);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(manifest, SdkJsonContext.Default.RecoveryManifest)).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
