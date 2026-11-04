using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
using System.Xml;
using System.Xml.Linq;
using NugetManager.Domain;
using NugetManager.Sdk;

namespace NugetManager.Operations;

internal interface IPackageOperations
{
    Task<OperationPlan> PlanAsync(Workspace workspace, IReadOnlyList<PackageChange> changes, CancellationToken ct);

    Task<OperationResult> ExecuteAsync(OperationPlan plan, IProgress<UiMessage> progress, CancellationToken ct);
}

internal sealed class PackageOperations(ISdkClient sdk, string recoveryDirectory) : IPackageOperations
{
    public string RecoveryDirectory { get; } = recoveryDirectory;

    public async Task<OperationPlan> PlanAsync(Workspace workspace, IReadOnlyList<PackageChange> changes, CancellationToken ct)
    {
        var evaluations = await EvaluateAllAsync(workspace, ct).ConfigureAwait(false);
        var warnings = new List<UiMessage>();
        var blockers = new List<UiMessage>();
        var files = new List<string>();
        var affected = new List<ProjectDescriptor>();
        var commands = new List<PlannedCommand>();
        var centralCommands = new Dictionary<(string File, string Id), int>();

        foreach (var (project, error) in evaluations.Where(e => e.Value.Error is not null).Select(e => (e.Key, e.Value.Error)))
        {
            warnings.Add(MF(TextKey.M007, $"项目 {project.Name} 无法求值，若它也使用相关包或集中版本，本次修改可能同样影响它：{error}"));
        }

        foreach (var change in changes)
        {
            var project = workspace.Projects.FirstOrDefault(p => PathUtil.AreEqual(p.Path, change.ProjectPath));
            if (project is null || !evaluations.TryGetValue(project, out var entry) || entry.Evaluation is null)
            {
                blockers.Add(MF(TextKey.M008, $"{change.PackageId}：项目 {Path.GetFileName(change.ProjectPath)} 不在当前工作区或无法求值。"));
                continue;
            }
            var evaluation = entry.Evaluation;
            var refs = evaluation.References.Where(r => string.Equals(r.PackageId, change.PackageId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (change.Action != PackageAction.Uninstall
                && (change.Version is null || !NuGetVersionComparer.TryParse(change.Version, out _)))
            {
                blockers.Add(MF(TextKey.M009, $"{change.PackageId}：目标版本无效（{change.Version ?? M(TextKey.M010)}）。"));
                continue;
            }
            AddUnique(affected, project);
            AddUnique(files, project.Path);

            switch (change.Action)
            {
                case PackageAction.Upgrade:
                case PackageAction.Downgrade:
                case PackageAction.SwitchVersion:
                case PackageAction.Uninstall:
                    if (refs.Count == 0)
                    {
                        blockers.Add(MF(TextKey.M011, $"{change.PackageId}：{project.Name} 没有直接引用该包。"));
                        continue;
                    }
                    if (refs.FirstOrDefault(r => !r.IsWritable) is { } blocked)
                    {
                        blockers.Add(UiMessage.Join("", [change.PackageId, " (", project.Name, "): ", blocked.WriteBlockReason!]));
                        continue;
                    }
                    break;
                case PackageAction.Install:
                    if (refs.Count > 0)
                    {
                        blockers.Add(refs.FirstOrDefault(r => !r.IsWritable) is { } existing
                            ? MF(TextKey.M012, $"{change.PackageId}（{project.Name}）：已存在引用，{existing.WriteBlockReason}")
                            : MF(TextKey.M013, $"{change.PackageId}：{project.Name} 已直接引用该包，请使用升级或切换版本。"));
                        continue;
                    }
                    break;
            }

            if (change.Action == PackageAction.Uninstall)
            {
                commands.Add(new PlannedCommand(PackageAction.Uninstall, project.Path, change.PackageId, null, MF(TextKey.M014, $"从 {project.Name} 卸载 {change.PackageId}")));
                continue;
            }

            var version = change.Version!;
            var current = refs.Select(r => r.VersionExpression).FirstOrDefault(v => v is not null);
            if (current is not null && NuGetVersionComparer.TryParse(current, out _) && NuGetVersionComparer.IsMajorChange(current, version))
            {
                AddUnique(warnings, new UiMessage(TextKey.MajorVersionChange, change.PackageId, current, version));
            }
            if (current is not null && NuGetVersionComparer.TryParse(current, out _) && !NuGetVersionComparer.IsNewer(version, current)
                && !string.Equals(current, version, StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(warnings, MF(TextKey.M016, $"{change.PackageId} 将从 {current} 降级到 {version}。"));
            }

            var centralFile = evaluation.CentralPackageManagement
                ? refs.Select(r => r.CentralVersionPath).FirstOrDefault(p => p is not null)
                  ?? (evaluation.CentralVersions.TryGetValue(change.PackageId, out var defined) ? defined : evaluation.CentralPackagesFile)
                : null;
            if (evaluation.CentralPackageManagement && centralFile is null)
            {
                blockers.Add(MF(TextKey.M017, $"{change.PackageId}（{project.Name}）：启用了集中版本管理，但找不到 Directory.Packages.props。"));
                continue;
            }

            if (centralFile is null)
            {
                commands.Add(new PlannedCommand(change.Action, project.Path, change.PackageId, version,
                    MF(TextKey.M018, $"{Verb(change.Action)} {project.Name} 的 {change.PackageId} → {version}")));
                continue;
            }

            AddUnique(files, Path.GetFullPath(centralFile));
            var sharedBy = evaluations.Values
                .Where(e => e.Evaluation is { } other && other.CentralVersions.TryGetValue(change.PackageId, out var path)
                    && PathUtil.AreEqual(path, centralFile)
                    && other.References.Any(r => string.Equals(r.PackageId, change.PackageId, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Evaluation!.Project)
                .ToList();
            foreach (var other in sharedBy)
            {
                AddUnique(affected, other);
            }
            var outside = sharedBy.Where(p => !changes.Any(c => PathUtil.AreEqual(c.ProjectPath, p.Path)
                    && string.Equals(c.PackageId, change.PackageId, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Name).ToList();
            if (outside.Count > 0)
            {
                AddUnique(warnings, MF(TextKey.M019, $"{change.PackageId} 的版本由 {Path.GetFileName(centralFile)} 集中管理，修改还会影响：{string.Join("、", outside)}。"));
            }

            if (change.Action == PackageAction.Install)
            {
                commands.Add(new PlannedCommand(PackageAction.Install, project.Path, change.PackageId, version,
                    MF(TextKey.M020, $"在 {project.Name} 安装 {change.PackageId} {version}（集中版本）")));
                // Later upgrades of the same entry must not write a second, conflicting version.
                centralCommands[(Path.GetFullPath(centralFile).ToUpperInvariant(), change.PackageId.ToUpperInvariant())] = commands.Count - 1;
                continue;
            }

            var key = (Path.GetFullPath(centralFile).ToUpperInvariant(), change.PackageId.ToUpperInvariant());
            if (centralCommands.TryGetValue(key, out var index))
            {
                var existing = commands[index];
                if (!string.Equals(existing.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    var max = NuGetVersionComparer.Instance.Compare(version, existing.Version) > 0 ? version : existing.Version!;
                    AddUnique(warnings, MF(TextKey.M021, $"{change.PackageId} 共享同一集中版本条目，所选目标版本不同，统一使用 {max}。"));
                    commands[index] = existing with { Version = max, Description = MF(TextKey.M022, $"更新集中版本 {change.PackageId} → {max}") };
                }
                continue;
            }
            centralCommands[key] = commands.Count;
            commands.Add(new PlannedCommand(PackageAction.Upgrade, project.Path, change.PackageId, version,
                MF(TextKey.M023, $"更新集中版本 {change.PackageId} → {version}（{Path.GetFileName(centralFile)}）")));
        }

        var lockFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedEvaluations = new List<ProjectEvaluation>();
        foreach (var project in affected)
        {
            if (evaluations.TryGetValue(project, out var e) && e.Evaluation is { } evaluation)
            {
                affectedEvaluations.Add(evaluation);
                foreach (var lockFile in evaluation.LockFilePaths)
                {
                    lockFiles.Add(lockFile);
                    AddUnique(files, lockFile);
                }
            }
        }

        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            fingerprints[file] = await FingerprintAsync(file, ct).ConfigureAwait(false);
        }

        return new OperationPlan(changes, files, affected, warnings, blockers)
        {
            Commands = commands,
            Fingerprints = fingerprints,
            PackageIds = changes.Select(c => c.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            LockFiles = lockFiles,
            AffectedEvaluations = affectedEvaluations,
        };
    }

    public async Task<OperationResult> ExecuteAsync(OperationPlan plan, IProgress<UiMessage> progress, CancellationToken ct)
    {
        if (plan.Blockers.Count > 0)
        {
            throw UiErrors.InvalidOperation(M(TextKey.M024) + UiMessage.Join("\n", plan.Blockers));
        }
        if (plan.Commands.Count == 0)
        {
            throw UiErrors.InvalidOperation(M(TextKey.M025));
        }
        foreach (var (file, hash) in plan.Fingerprints)
        {
            if (await FingerprintAsync(file, ct).ConfigureAwait(false) != hash)
            {
                throw new PlanStaleException(MF(TextKey.M026, $"{Path.GetFileName(file)} 在预览后被修改，请重新预览后再确认。"));
            }
        }

        progress.Report(M(TextKey.M027));
        await using var transaction = await FileTransaction.BeginAsync(plan.Files, RecoveryDirectory, ct,
            (path, before, after) => IsExpectedChange(plan, path, before, after),
            UiMessage.Join("; ", plan.Commands.Select(c => c.Description))).ConfigureAwait(false);
        try
        {
            foreach (var command in plan.Commands)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(command.Description + "…");
                if (command.Action == PackageAction.Uninstall)
                {
                    await sdk.RemovePackageAsync(command.ProjectPath, command.PackageId, ct).ConfigureAwait(false);
                }
                else
                {
                    await sdk.AddPackageAsync(command.ProjectPath, command.PackageId, command.Version!, ct).ConfigureAwait(false);
                }
            }
            await RecordAsync(transaction, ct).ConfigureAwait(false);

            progress.Report(MF(TextKey.M028, $"正在还原 {plan.AffectedProjects.Count} 个受影响项目…"));
            ct.ThrowIfCancellationRequested();
            await transaction.BeginWriteWindowAsync().ConfigureAwait(false);
            await sdk.RestoreAsync(plan.AffectedProjects, lockedMode: false, ct).ConfigureAwait(false);
            await RecordAsync(transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            progress.Report(M(TextKey.M029));
            return new OperationResult(OperationStatus.Succeeded, null, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var error = ex is OperationCanceledException ? M(TextKey.M030) : UiMessage.FromException(ex);
            progress.Report(M(TextKey.M031));
            var recovery = await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            var restored = recovery.All(r => r.Status == RecoveryStatus.Restored);
            if (restored)
            {
                try
                {
                    await sdk.RestorePreservingLockFilesAsync(plan.AffectedEvaluations, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError) when (restoreError is SdkCommandException or SdkOutputException or ProcessTimeoutException)
                {
                    error += MF(TextKey.M032, $"\n文件已撤回，但重新还原原配置失败，请手动执行 dotnet restore：{UiMessage.FromException(restoreError)}");
                }
            }
            return new OperationResult(restored ? OperationStatus.RolledBack : OperationStatus.RecoveryRequired, error, recovery)
            {
                RecoveryManifestPath = restored ? null : transaction.ManifestPath,
            };
        }
    }

    private static async Task RecordAsync(FileTransaction transaction, CancellationToken ct)
    {
        await transaction.RecordOwnedChangesAsync(CancellationToken.None).ConfigureAwait(false);
        if (transaction.HasConflicts)
        {
            throw UiErrors.IO(M(TextKey.M033));
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// A project/props change is ours only if, after removing the package elements this plan targets (and any
    /// ItemGroup left empty), the document is unchanged. Lock files are regenerated wholesale by restore.
    /// </summary>
    internal static bool IsExpectedChange(OperationPlan plan, string path, byte[]? before, byte[]? after)
    {
        if (plan.LockFiles.Contains(path))
        {
            return true;
        }
        if (before is null || after is null)
        {
            return false;
        }
        var a = Strip(before, plan.PackageIds);
        var b = Strip(after, plan.PackageIds);
        return a is not null && b is not null && XNode.DeepEquals(a, b);
    }

    private static XElement? Strip(byte[] bytes, IReadOnlySet<string> ids)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
            var root = XElement.Load(reader);
            root.Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion" && ids.Contains((string?)e.Attribute("Include") ?? ""))
                .ToList()
                .ForEach(e => e.Remove());
            root.Descendants().Where(e => e.Name.LocalName == "ItemGroup" && !e.HasElements && !e.HasAttributes).ToList().ForEach(e => e.Remove());
            return root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private async Task<Dictionary<ProjectDescriptor, (ProjectEvaluation? Evaluation, UiMessage? Error)>> EvaluateAllAsync(Workspace workspace, CancellationToken ct)
    {
        var results = new (ProjectEvaluation? Evaluation, UiMessage? Error)[workspace.Projects.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(workspace.Projects.Select(async (project, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[i] = (await sdk.EvaluateAsync(project, ct).ConfigureAwait(false), null);
            }
            catch (Exception ex) when (ex is SdkCommandException or SdkOutputException or ProcessTimeoutException)
            {
                results[i] = (null, UiMessage.FromException(ex));
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return workspace.Projects.Select((p, i) => (p, results[i])).ToDictionary(x => x.p, x => x.Item2);
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken ct) =>
        FileTransaction.Hash(File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null);

    private static UiMessage Verb(PackageAction action) => action switch
    {
        PackageAction.Install => M(TextKey.M034),
        PackageAction.Uninstall => M(TextKey.M035),
        PackageAction.Downgrade => M(TextKey.Downgrade),
        PackageAction.SwitchVersion => M(TextKey.SwitchVersion),
        _ => M(TextKey.M036),
    };

    private static void AddUnique(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }

    }

    private static void AddUnique(List<UiMessage> list, UiMessage value)
    {
        if (!list.Contains(value)) list.Add(value);
    }

    private static void AddUnique(List<ProjectDescriptor> list, ProjectDescriptor project)
    {
        if (!list.Any(p => PathUtil.AreEqual(p.Path, project.Path)))
        {
            list.Add(project);
        }
    }
}
