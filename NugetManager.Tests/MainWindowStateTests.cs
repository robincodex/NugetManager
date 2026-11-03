using NugetManager.Domain;
using NugetManager.Operations;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class MainWindowStateTests
{
    private const string Root = @"C:\ws";
    private static readonly string ApiPath = Path.Combine(Root, "Api", "Api.csproj");
    private static readonly string CorePath = Path.Combine(Root, "Core", "Core.csproj");
    private static readonly ProjectDescriptor Api = new(ApiPath, "Api");
    private static readonly ProjectDescriptor Core = new(CorePath, "Core");

    private static PackageOccurrence Occ(string project, string id, string version, ReferenceKind kind, string framework = "net10.0") =>
        new(project, framework, id, kind == ReferenceKind.Direct ? version : null, version, kind, null);

    private static EvaluatedReference Ref(string project, string id) =>
        new(id, "net10.0", project, null, "1.0.0", null, true, null);

    private static WorkspaceViewState State(bool withUpgrades) => new()
    {
        Workspace = new Workspace(Root, null, [Api, Core]),
        VisibleProjects = [Api, Core],
        Status = WorkspaceStatus.Ready,
        UpdatesComplete = true,
        Upgrades = withUpgrades
            ? [new UpdateCandidate("Example.Json", [new PackageUpgrade(Ref(ApiPath, "Example.Json"), "1.0.0", "3.0.0")])]
            : [],
        Installed = [Occ(ApiPath, "Example.Json", "1.0.0", ReferenceKind.Direct), Occ(CorePath, "Example.Json", "1.0.0", ReferenceKind.Direct)],
        Implicit = [Occ(ApiPath, "Example.Core", "1.0.0", ReferenceKind.Transitive)],
        SdkImplicit = [Occ(CorePath, "Microsoft.NET.ILLink.Tasks", "10.0.0", ReferenceKind.SdkImplicit)],
    };

    [Fact]
    public void ExtractedPresentationStateUsesInstalledLabels()
    {
        var model = PresentationModel.BuildPackageList(State(withUpgrades: true));

        Assert.Equal(["可升级", "已安装", "隐式依赖", "SDK 自动引用"], model.Groups.Select(g => g.Title).ToArray());
        var installed = Assert.Single(model.Groups[1].Rows);
        Assert.Equal("Example.Json", installed.PackageId);
        Assert.Equal("2 个项目", installed.ProjectsText);
        Assert.Equal("1.0.0 → 3.0.0", Assert.Single(model.Groups[0].Rows).VersionText);
        Assert.Equal(PackageGroupKind.Implicit, model.Groups[2].Kind);
        Assert.False(Assert.Single(model.Groups[2].Rows).CanSelect);
    }

    [Fact]
    public void NoUpgradeMeansNoUpgradeHeader()
    {
        var model = PresentationModel.BuildPackageList(State(withUpgrades: false));

        var upgradeHeaderVisible = model.Groups.Any(g => g.Kind == PackageGroupKind.Upgrades);
        var upgradeButtonVisible = model.Groups.SelectMany(g => g.Actions).Any(a => a.Kind == GroupActionKind.UpgradeSelected);
        Assert.False(upgradeHeaderVisible);
        Assert.False(upgradeButtonVisible);
        Assert.Equal("已安装", model.Groups[0].Title);
    }

    [Fact]
    public void UpgradeButtonLivesInHeader()
    {
        var state = State(withUpgrades: true);
        var model = PresentationModel.BuildPackageList(state);
        var header = model.Groups[0];

        Assert.Equal(PackageGroupKind.Upgrades, header.Kind);
        Assert.Equal([GroupActionKind.SelectAll, GroupActionKind.UpgradeSelected], header.Actions.Select(a => a.Kind).ToArray());
        Assert.All(model.Groups.Skip(1), g => Assert.Empty(g.Actions));
        Assert.False(header.Actions[1].IsEnabled);
        Assert.Equal(false, header.Actions[0].IsChecked);

        var selected = PresentationModel.BuildPackageList(state with
        {
            SelectedPackageIds = new HashSet<string>(["Example.Json"], StringComparer.OrdinalIgnoreCase),
        });
        Assert.True(selected.Groups[0].Actions[1].IsEnabled);
        Assert.Equal(true, selected.Groups[0].Actions[0].IsChecked);
        Assert.True(selected.Groups[0].Rows[0].IsSelected);

        var busy = PresentationModel.BuildPackageList(state with
        {
            IsBusy = true,
            SelectedPackageIds = new HashSet<string>(["Example.Json"], StringComparer.OrdinalIgnoreCase),
        });
        Assert.False(busy.Groups[0].Actions[1].IsEnabled);
    }

    [Fact]
    public void DirectShowsUninstallTransitiveShowsInstall()
    {
        var state = State(withUpgrades: false) with
        {
            SelectedPackageId = "Example.Core",
            SelectedVersion = "1.0.0",
            ProjectStates =
            [
                new ProjectPackageState(Api, [Occ(ApiPath, "Example.Core", "1.0.0", ReferenceKind.Direct)], ProjectAction.Uninstall, null),
                new ProjectPackageState(Core, [Occ(CorePath, "Example.Core", "1.0.0", ReferenceKind.Transitive, "net8.0")], ProjectAction.Install, null),
            ],
        };
        var rows = PresentationModel.BuildProjectRows(state);

        var directProjectActionLabel = rows[0].ActionLabel;
        var transitiveProjectActionLabel = rows[1].ActionLabel;
        Assert.Equal("卸载", directProjectActionLabel);
        Assert.Equal("安装", transitiveProjectActionLabel);
        Assert.Contains("net8.0", rows[1].VersionText);
        Assert.Contains("隐式", rows[1].VersionText);
        Assert.All(rows, r => Assert.True(r.ActionEnabled));

        var blocked = PresentationModel.BuildProjectRows(state with
        {
            ProjectStates = [new ProjectPackageState(Api, [], ProjectAction.Uninstall, "条件引用")],
        });
        Assert.False(Assert.Single(blocked).ActionEnabled);
        Assert.Equal("条件引用", blocked[0].Note);
    }

    [Fact]
    public void ConfirmationListsOutsideScopeAffectedProjects()
    {
        var plan = new OperationPlan(
            [new PackageChange(PackageAction.Upgrade, ApiPath, "Example.Json", "3.0.0")],
            [Path.Combine(Root, "Directory.Packages.props")],
            [Api, Core],
            ["集中版本也会影响范围外项目 Core"],
            [])
        {
            Commands = [new PlannedCommand(PackageAction.Upgrade, ApiPath, "Example.Json", "3.0.0", "")],
        };
        var state = State(withUpgrades: true) with { Scope = new WorkspaceScope(ApiPath), VisibleProjects = [Api] };

        var model = PresentationModel.BuildConfirmation(plan, state.Scope);

        Assert.Contains(model.AffectedProjects, p => p.Contains("Core") && p.Contains("当前范围外"));
        Assert.DoesNotContain(model.AffectedProjects, p => p.Contains("Api") && p.Contains("当前范围外"));
        Assert.Contains(model.Changes, c => c.Contains("Example.Json") && c.Contains("3.0.0"));
        Assert.NotEmpty(model.Warnings);
        Assert.True(model.CanConfirm);
        Assert.Equal(ApiPath, Assert.Single(state.VisibleProjects).Path);

        var blocked = PresentationModel.BuildConfirmation(plan with { Blockers = ["引用受条件控制"] }, state.Scope);
        Assert.False(blocked.CanConfirm);
        Assert.Contains("引用受条件控制", blocked.Blockers);
    }
}
