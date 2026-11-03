using System.Text;
using NugetManager.Operations;

namespace NugetManager.Tests;

public class FileTransactionTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static string RecoveryDir(TestWorkspace ws) => Path.Combine(ws.Root, ".recovery");

    /// <summary>Accepts only changes that keep the marker text, rejecting unrelated external edits.</summary>
    private static bool OnlyVersionChanges(string path, byte[]? before, byte[]? after) =>
        after is not null && !Encoding.UTF8.GetString(after).Contains("EXTERNAL", StringComparison.Ordinal);

    [Fact]
    public async Task PreservesDirtyOriginalBytes()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Path_("App/App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        byte[] dirtyOriginalBytes = [.. Bom, .. Encoding.UTF8.GetBytes("<Project>\r\n  <!-- unsaved 中文 -->\n</Project>\r\n\r\n")];
        await File.WriteAllBytesAsync(projectPath, dirtyOriginalBytes);

        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None);
        await File.WriteAllTextAsync(projectPath, "<Project>rewritten</Project>");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        var recovery = await tx.RollbackAsync(CancellationToken.None);

        Assert.All(recovery, r => Assert.Equal(RecoveryStatus.Restored, r.Status));
        Assert.Equal(dirtyOriginalBytes, await File.ReadAllBytesAsync(projectPath));
        Assert.Empty(await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotDoesNotExposeMutableBackup()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var path = ws.Write("a.txt", "original");

        var tx = await FileTransaction.BeginAsync([path], RecoveryDir(ws), CancellationToken.None);
        tx.Files[0].OriginalBytes![0] = (byte)'X';
        await File.WriteAllTextAsync(path, "changed");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        await tx.RollbackAsync(CancellationToken.None);

        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NewFileRemovedOnlyWhenOwned()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var created = ws.Path_("App/packages.lock.json");
        var externallyCreated = ws.Path_("Other/packages.lock.json");

        var tx = await FileTransaction.BeginAsync([created, externallyCreated], RecoveryDir(ws), CancellationToken.None);
        Assert.Null(tx.Files[0].OriginalBytes);
        ws.Write("App/packages.lock.json", "{ }");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        ws.Write("Other/packages.lock.json", "{ \"user\": true }");
        var recovery = await tx.RollbackAsync(CancellationToken.None);

        Assert.False(File.Exists(created));
        Assert.True(File.Exists(externallyCreated));
        Assert.Contains(recovery, r => r.Path == created && r.Status == RecoveryStatus.Restored);
        Assert.Contains(recovery, r => r.Path == externallyCreated && r.Status == RecoveryStatus.Conflict);
    }

    [Fact]
    public async Task ConcurrentEditDuringCommandBlocksRollback()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "<Project Version=\"1.0.0\" />");

        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None, OnlyVersionChanges);
        // The SDK command and an external editor both write during the command window.
        var externalEditBytes = Encoding.UTF8.GetBytes("<Project Version=\"2.0.0\" /><!-- EXTERNAL -->");
        await File.WriteAllBytesAsync(projectPath, externalEditBytes);
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        var recovery = await tx.RollbackAsync(CancellationToken.None);

        Assert.Contains(recovery, r => r.Path == projectPath && r.Status == RecoveryStatus.Conflict);
        Assert.Equal(externalEditBytes, await File.ReadAllBytesAsync(projectPath));
        Assert.NotEmpty(await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None));
    }

    [Fact]
    public async Task EditAfterCommandAlsoBlocksRollback()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "<Project Version=\"1.0.0\" />");

        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None, OnlyVersionChanges);
        ws.Write("App/App.csproj", "<Project Version=\"2.0.0\" />");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        ws.Write("App/App.csproj", "<Project Version=\"2.0.0\" Note=\"user typed this\" />");
        var recovery = await tx.RollbackAsync(CancellationToken.None);

        Assert.Equal(RecoveryStatus.Conflict, Assert.Single(recovery).Status);
        Assert.Contains("user typed this", await File.ReadAllTextAsync(projectPath));
    }

    [Fact]
    public async Task UnrecordedWindowUsesValidatorBeforeRestoring()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "<Project Version=\"1.0.0\" />");

        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None, OnlyVersionChanges);
        ws.Write("App/App.csproj", "<Project Version=\"2.0.0\" />");
        var recovery = await tx.RollbackAsync(CancellationToken.None);

        Assert.Equal(RecoveryStatus.Restored, Assert.Single(recovery).Status);
        Assert.Equal("<Project Version=\"1.0.0\" />", await File.ReadAllTextAsync(projectPath));
    }

    [Fact]
    public async Task CrashManifestSurvivesRestart()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "<Project Version=\"1.0.0\" />");

        string manifestPath;
        await using (var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None, description: "升级 Example.Json"))
        {
            manifestPath = tx.ManifestPath;
            ws.Write("App/App.csproj", "<Project Version=\"2.0.0\" />");
            await tx.RecordOwnedChangesAsync(CancellationToken.None);
        }

        var incomplete = await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None);
        Assert.Contains(manifestPath, incomplete);

        var recovery = await FileTransaction.RecoverAsync(manifestPath, CancellationToken.None);
        Assert.Equal(RecoveryStatus.Restored, Assert.Single(recovery).Status);
        Assert.Equal("<Project Version=\"1.0.0\" />", await File.ReadAllTextAsync(projectPath));
        Assert.Empty(await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None));
    }

    [Fact]
    public async Task DiscardKeepsCurrentFiles()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "before");
        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None);
        ws.Write("App/App.csproj", "after");

        await FileTransaction.DiscardAsync(tx.ManifestPath, CancellationToken.None);

        Assert.Equal("after", await File.ReadAllTextAsync(projectPath));
        Assert.Empty(await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None));
    }

    [Fact]
    public async Task CommitRemovesRecoveryRecord()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "before");
        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None);
        ws.Write("App/App.csproj", "after");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);

        Assert.Equal("after", await File.ReadAllTextAsync(projectPath));
        Assert.Empty(await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None));
    }

    [Fact]
    public async Task BackupFailurePreventsAnyWrite()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "original");
        var blockedRecovery = ws.Write("recovery-is-a-file", "not a directory");

        await Assert.ThrowsAnyAsync<IOException>(() => FileTransaction.BeginAsync([projectPath], blockedRecovery, CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(projectPath));
    }

    [Fact]
    public async Task RollbackFailureRemainsVisible()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "original");
        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None);
        ws.Write("App/App.csproj", "changed");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);

        IReadOnlyList<FileRecovery> recovery;
        await using (new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            recovery = await tx.RollbackAsync(CancellationToken.None);
        }

        var failed = Assert.Single(recovery);
        Assert.Equal(RecoveryStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrEmpty(failed.Message));
        Assert.Contains(tx.ManifestPath, await FileTransaction.FindIncompleteAsync(RecoveryDir(ws), CancellationToken.None));
    }

    [Fact]
    public async Task RollbackIgnoresCancelledToken()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var projectPath = ws.Write("App/App.csproj", "original");
        var tx = await FileTransaction.BeginAsync([projectPath], RecoveryDir(ws), CancellationToken.None);
        ws.Write("App/App.csproj", "changed");
        await tx.RecordOwnedChangesAsync(CancellationToken.None);

        var recovery = await tx.RollbackAsync(new CancellationToken(canceled: true));

        Assert.Equal(RecoveryStatus.Restored, Assert.Single(recovery).Status);
        Assert.Equal("original", await File.ReadAllTextAsync(projectPath));
    }
}
