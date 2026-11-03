using NugetManager.Domain;
using NugetManager.Operations;

namespace NugetManager.Presentation;

internal enum PackageGroupKind
{
    Upgrades,
    Installed,
    Implicit,
    SdkImplicit,
}

internal enum GroupActionKind
{
    SelectAll,
    UpgradeSelected,
}

/// <summary>A control that lives in a group header. <see cref="IsChecked"/> null means partially selected.</summary>
internal sealed record GroupAction(GroupActionKind Kind, string Label, bool IsEnabled, bool? IsChecked);

internal sealed record PackageRowModel(
    string PackageId,
    string VersionText,
    string ProjectsText,
    PackageGroupKind Group,
    bool CanSelect,
    bool IsSelected,
    string DetailVersion);

internal sealed record PackageGroupModel(PackageGroupKind Kind, string Title, IReadOnlyList<GroupAction> Actions, IReadOnlyList<PackageRowModel> Rows);

internal sealed record PackageListModel(IReadOnlyList<PackageGroupModel> Groups, string? Notice);

internal sealed record ProjectRowModel(
    string ProjectPath,
    string ProjectName,
    string VersionText,
    ProjectAction Action,
    string? ActionLabel,
    bool ActionEnabled,
    string? Note);

internal sealed record ConfirmationModel(
    string Title,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    bool CanConfirm);

/// <summary>Pure projections from controller state to what the window shows; kept free of MewUI so they are unit-testable.</summary>
internal static class PresentationModel
{
    public static PackageListModel BuildPackageList(WorkspaceViewState state)
    {
        var groups = new List<PackageGroupModel>();
        if (state.Upgrades.Count > 0)
        {
            var selectedCount = state.Upgrades.Count(u => state.SelectedPackageIds.Contains(u.PackageId));
            bool? allChecked = selectedCount == 0 ? false : selectedCount == state.Upgrades.Count ? true : null;
            var rows = state.Upgrades.Select(u => new PackageRowModel(
                u.PackageId,
                $"{string.Join(", ", u.FromVersions)} → {u.ToVersion}",
                ProjectsText(u.Upgrades.Select(x => x.Reference.ProjectPath), state),
                PackageGroupKind.Upgrades,
                CanSelect: true,
                IsSelected: state.SelectedPackageIds.Contains(u.PackageId),
                DetailVersion: u.ToVersion)).ToList();
            groups.Add(new PackageGroupModel(PackageGroupKind.Upgrades, "可升级",
            [
                new GroupAction(GroupActionKind.SelectAll, "全选", !state.IsBusy, allChecked),
                new GroupAction(GroupActionKind.UpgradeSelected, selectedCount > 0 ? $"升级所选 ({selectedCount})" : "升级所选",
                    !state.IsBusy && selectedCount > 0, null),
            ], rows));
        }

        AddOccurrenceGroup(groups, state, PackageGroupKind.Installed, "已安装", state.Installed);
        AddOccurrenceGroup(groups, state, PackageGroupKind.Implicit, "隐式依赖", state.Implicit);
        AddOccurrenceGroup(groups, state, PackageGroupKind.SdkImplicit, "SDK 自动引用", state.SdkImplicit);

        string? notice = state.Workspace is null ? null
            : state.Status == WorkspaceStatus.Loading ? "正在检测…"
            : !state.UpdatesComplete ? "更新检测未完成，部分包可能有可用更新。"
            : state.Upgrades.Count == 0 ? "所有直接引用均为最新版本。"
            : null;
        return new PackageListModel(groups, notice);
    }

    public static IReadOnlyList<ProjectRowModel> BuildProjectRows(WorkspaceViewState state) =>
        state.ProjectStates.Select(p =>
        {
            var label = p.Action switch
            {
                ProjectAction.Install => "安装",
                ProjectAction.Uninstall => "卸载",
                _ => null,
            };
            var enabled = label is not null && p.BlockReason is null && !state.IsBusy
                && (p.Action != ProjectAction.Install || !string.IsNullOrEmpty(state.SelectedVersion));
            return new ProjectRowModel(p.Project.Path, p.Project.Name, VersionText(p.Occurrences), p.Action, label, enabled, p.BlockReason);
        }).ToList();

    public static ConfirmationModel BuildConfirmation(OperationPlan plan, WorkspaceScope scope)
    {
        var changes = plan.Changes.Select(c => c.Action switch
        {
            PackageAction.Upgrade => $"升级 {c.PackageId} 到 {c.Version}（{ProjectName(c.ProjectPath)}）",
            PackageAction.Install => $"安装 {c.PackageId} {c.Version}（{ProjectName(c.ProjectPath)}）",
            _ => $"卸载 {c.PackageId}（{ProjectName(c.ProjectPath)}）",
        }).ToList();
        var affected = plan.AffectedProjects
            .Select(p => scope.Includes(p.Path) ? p.Name : $"{p.Name}（当前范围外，也会受影响）")
            .ToList();
        var title = plan.Changes.Select(c => c.Action).Distinct().Count() == 1
            ? plan.Changes[0].Action switch
            {
                PackageAction.Upgrade => $"确认升级 {plan.Changes.Select(c => c.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个包",
                PackageAction.Install => "确认安装",
                _ => "确认卸载",
            }
            : "确认更改";
        return new ConfirmationModel(title, changes, affected, plan.Files, plan.Warnings, plan.Blockers, plan.CanExecute);
    }

    private static void AddOccurrenceGroup(List<PackageGroupModel> groups, WorkspaceViewState state, PackageGroupKind kind, string title,
        IReadOnlyList<PackageOccurrence> occurrences)
    {
        if (occurrences.Count == 0)
        {
            return;
        }
        var rows = occurrences
            .GroupBy(o => o.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versions = g.Select(o => o.ResolvedVersion).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, NuGetVersionComparer.Instance).ToList();
                return new PackageRowModel(g.First().PackageId, string.Join(", ", versions),
                    ProjectsText(g.Select(o => o.ProjectPath), state), kind, CanSelect: false, IsSelected: false, DetailVersion: versions[^1]);
            })
            .ToList();
        groups.Add(new PackageGroupModel(kind, title, [], rows));
    }

    private static string ProjectsText(IEnumerable<string> projectPaths, WorkspaceViewState state)
    {
        var distinct = projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 1)
        {
            return state.Workspace?.Projects.FirstOrDefault(p => PathUtil.AreEqual(p.Path, distinct[0]))?.Name ?? ProjectName(distinct[0]);
        }
        return $"{distinct.Count} 个项目";
    }

    private static string VersionText(IReadOnlyList<PackageOccurrence> occurrences)
    {
        if (occurrences.Count == 0)
        {
            return "未安装";
        }
        var showFramework = occurrences.Select(o => o.Framework).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || occurrences.Any(o => o.Kind != ReferenceKind.Direct);
        return string.Join("；", occurrences.Select(o =>
        {
            var text = o.Kind switch
            {
                ReferenceKind.Direct => o.RequestedVersion is { } requested && requested != o.ResolvedVersion
                    ? $"{requested}（解析为 {o.ResolvedVersion}）"
                    : o.ResolvedVersion,
                ReferenceKind.Transitive => $"{o.ResolvedVersion}（隐式）",
                _ => $"{o.ResolvedVersion}（SDK 自动引用）",
            };
            return showFramework ? $"{o.Framework}: {text}" : text;
        }));
    }

    private static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);
}
