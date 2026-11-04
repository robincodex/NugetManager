using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
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
internal sealed record GroupAction(GroupActionKind Kind, UiMessage Label, bool IsEnabled, bool? IsChecked);

internal sealed record PackageRowModel(
    string PackageId,
    string VersionText,
    UiMessage ProjectsText,
    PackageGroupKind Group,
    bool CanSelect,
    bool IsSelected,
    string DetailVersion);

internal sealed record PackageGroupModel(PackageGroupKind Kind, UiMessage Title, IReadOnlyList<GroupAction> Actions, IReadOnlyList<PackageRowModel> Rows);

internal sealed record PackageListModel(IReadOnlyList<PackageGroupModel> Groups, UiMessage? Notice);

internal sealed record ProjectRowModel(
    string ProjectPath,
    string ProjectName,
    UiMessage VersionText,
    ProjectAction Action,
    UiMessage? ActionLabel,
    bool ActionEnabled,
    UiMessage? Note);

internal sealed record ConfirmationModel(
    UiMessage Title,
    IReadOnlyList<UiMessage> Changes,
    IReadOnlyList<UiMessage> AffectedProjects,
    IReadOnlyList<string> Files,
    IReadOnlyList<UiMessage> Warnings,
    IReadOnlyList<UiMessage> Blockers,
    bool CanConfirm);

internal sealed record ActivityModel(bool Workspace, bool Search, bool Details, bool Versions, bool SourceSelection,
    bool InstalledWorkspace, bool InstalledCheck);

/// <summary>Pure projections from controller state to what the window shows; kept free of MewUI so they are unit-testable.</summary>
internal static class PresentationModel
{
    public static ActivityModel BuildActivity(WorkspaceViewState state, bool browseMode) =>
        new(state.IsBusy, browseMode && state.SearchLoading, state.SelectedPackageId is not null && state.DetailsLoading,
            state.VersionsLoading, browseMode && state.Workspace is not null && !state.IsBusy,
            !browseMode && state.Workspace is null && state.Status == WorkspaceStatus.Loading,
            !browseMode && state.Workspace is not null && state.IsBusy
                && state.Status is WorkspaceStatus.Loading or WorkspaceStatus.Ready);

    public static UiMessage BrowseGroupTitle(BrowseSourcePage group) =>
        UiMessage.Join("", [group.Source.Name, " · ", group.Kind == BrowseKind.Popular ? M(TextKey.M170) : M(TextKey.M171),
            $"（{group.Page.Items.Count}）"]);

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
            groups.Add(new PackageGroupModel(PackageGroupKind.Upgrades, M(TextKey.M172),
            [
                new GroupAction(GroupActionKind.SelectAll, M(TextKey.M173), !state.IsBusy, allChecked),
                new GroupAction(GroupActionKind.UpgradeSelected, selectedCount > 0 ? MF(TextKey.M174, $"升级所选 ({selectedCount})") : M(TextKey.M175),
                    !state.IsBusy && selectedCount > 0, null),
            ], rows));
        }

        AddOccurrenceGroup(groups, state, PackageGroupKind.Installed, M(TextKey.Installed), state.Installed);
        AddOccurrenceGroup(groups, state, PackageGroupKind.Implicit, M(TextKey.M177), state.Implicit);
        AddOccurrenceGroup(groups, state, PackageGroupKind.SdkImplicit, M(TextKey.M178), state.SdkImplicit);

        UiMessage? notice = state.Workspace is null ? null
            : state.Status == WorkspaceStatus.Loading ? M(TextKey.M179)
            : !state.UpdatesComplete ? M(TextKey.M180)
            : state.Upgrades.Count == 0 ? M(TextKey.M181)
            : null;
        return new PackageListModel(groups, notice);
    }

    public static IReadOnlyList<ProjectRowModel> BuildProjectRows(WorkspaceViewState state) =>
        state.ProjectStates.Select(p =>
        {
            var label = p.Action switch
            {
                ProjectAction.Install => M(TextKey.M182),
                ProjectAction.Uninstall => M(TextKey.M035),
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
            PackageAction.Upgrade => MF(TextKey.M183, $"升级 {c.PackageId} 到 {c.Version}（{ProjectName(c.ProjectPath)}）"),
            PackageAction.Install => MF(TextKey.M184, $"安装 {c.PackageId} {c.Version}（{ProjectName(c.ProjectPath)}）"),
            _ => MF(TextKey.M185, $"卸载 {c.PackageId}（{ProjectName(c.ProjectPath)}）"),
        }).ToList();
        var affected = plan.AffectedProjects
            .Select(p => scope.Includes(p.Path) ? p.Name : MF(TextKey.M186, $"{p.Name}（当前范围外，也会受影响）"))
            .ToList();
        var title = plan.Changes.Select(c => c.Action).Distinct().Count() == 1
            ? plan.Changes[0].Action switch
            {
                PackageAction.Upgrade => MF(TextKey.M187, $"确认升级 {plan.Changes.Select(c => c.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个包"),
                PackageAction.Install => M(TextKey.M188),
                _ => M(TextKey.M189),
            }
            : M(TextKey.M190);
        return new ConfirmationModel(title, changes, affected, plan.Files, plan.Warnings, plan.Blockers, plan.CanExecute);
    }

    private static void AddOccurrenceGroup(List<PackageGroupModel> groups, WorkspaceViewState state, PackageGroupKind kind, UiMessage title,
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

    private static UiMessage ProjectsText(IEnumerable<string> projectPaths, WorkspaceViewState state)
    {
        var distinct = projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 1)
        {
            return state.Workspace?.Projects.FirstOrDefault(p => PathUtil.AreEqual(p.Path, distinct[0]))?.Name ?? ProjectName(distinct[0]);
        }
        return MF(TextKey.M191, $"{distinct.Count} 个项目");
    }

    private static UiMessage VersionText(IReadOnlyList<PackageOccurrence> occurrences)
    {
        if (occurrences.Count == 0)
        {
            return M(TextKey.M192);
        }
        var showFramework = occurrences.Select(o => o.Framework).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || occurrences.Any(o => o.Kind != ReferenceKind.Direct);
        return UiMessage.Join("; ", occurrences.Select(o =>
        {
            var text = o.Kind switch
            {
                ReferenceKind.Direct => o.RequestedVersion is { } requested && requested != o.ResolvedVersion
                    ? MF(TextKey.M193, $"{requested}（解析为 {o.ResolvedVersion}）")
                    : o.ResolvedVersion,
                ReferenceKind.Transitive => MF(TextKey.M194, $"{o.ResolvedVersion}（隐式）"),
                _ => MF(TextKey.M195, $"{o.ResolvedVersion}（SDK 自动引用）"),
            };
            return showFramework ? UiMessage.Join("", [o.Framework, ": ", text]) : text;
        }));
    }

    private static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);
}
