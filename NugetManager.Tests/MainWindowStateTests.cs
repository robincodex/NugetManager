using NugetManager.Domain;
using NugetManager.Operations;
using NugetManager.Presentation;
using NugetManager.Packages;
using Aprillz.MewUI.Controls;
using System.Reflection;

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
    public void RebuildingLoadingRowDoesNotLeaveRingInDetachedParent()
    {
        var build = typeof(MainWindow).GetMethod("LoadingLine", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ring = new ProgressRing();
        try
        {
            var first = (StackPanel)build.Invoke(null, [ring, "search"])!;
            ring.IsActive = false;
            var second = (StackPanel)build.Invoke(null, [ring, "search"])!;
            Assert.DoesNotContain(ring, first.Children);
            Assert.Same(second, ring.Parent);
            Assert.True(ring.IsActive);
        }
        finally { ring.IsActive = false; }
    }

    [Fact]
    public void LoadingSlotsHaveIndependentLifetimesAndRespectVisibleMode()
    {
        var state = State(false) with { IsBusy = true, SearchLoading = true, DetailsLoading = true, VersionsLoading = true, SelectedPackageId = "Example.Json" };
        var activity = PresentationModel.BuildActivity(state, true);
        Assert.True(activity.Workspace);
        Assert.True(activity.Search);
        Assert.True(activity.Details);
        Assert.True(activity.Versions);
        Assert.False(activity.SourceSelection);
        var ready = PresentationModel.BuildActivity(state with { IsBusy = false }, true);
        Assert.True(ready.SourceSelection);
        Assert.True(ready.Search);
        var installed = PresentationModel.BuildActivity(state with { IsBusy = false }, false);
        Assert.True(installed.SourceSelection);
        Assert.False(installed.Search);
        Assert.True(installed.Details);
    }

    [Fact]
    public void BrowseGroupTitlesDistinguishPopularityAndShowActualCounts()
    {
        var source = new NuGetSource("local", @"C:\feed", true, false);
        var page = new SearchPage([new SearchHit("A", "1.0.0", "local", null)], 0, 10, false, []);
        Assert.Equal("local · 普通浏览（1）", TestMessages.Zh(PresentationModel.BrowseGroupTitle(new(source, BrowseKind.Ordinary, page))));
        Assert.Equal("local · 热门（1）", TestMessages.Zh(PresentationModel.BrowseGroupTitle(new(source, BrowseKind.Popular, page))));
    }

    [Fact]
    public void ExtractedPresentationStateUsesInstalledLabels()
    {
        var model = PresentationModel.BuildPackageList(State(withUpgrades: true));

        Assert.Equal(["可升级", "已安装", "隐式依赖", "SDK 自动引用"], (model.Groups.Select(g => g.Title).ToArray()).Select(TestMessages.Zh));
        var installed = Assert.Single(model.Groups[1].Rows);
        Assert.Equal("Example.Json", installed.PackageId);
        Assert.Equal("2 个项目", TestMessages.Zh(installed.ProjectsText));
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
        Assert.Equal("已安装", TestMessages.Zh(model.Groups[0].Title));
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
        Assert.Equal("卸载", TestMessages.Zh(directProjectActionLabel));
        Assert.Equal("安装", TestMessages.Zh(transitiveProjectActionLabel));
        Assert.Contains("net8.0", TestMessages.Zh(rows[1].VersionText));
        Assert.Contains("隐式", TestMessages.Zh(rows[1].VersionText));
        Assert.All(rows, r => Assert.True(r.ActionEnabled));

        var blocked = PresentationModel.BuildProjectRows(state with
        {
            ProjectStates = [new ProjectPackageState(Api, [], ProjectAction.Uninstall, "条件引用")],
        });
        Assert.False(Assert.Single(blocked).ActionEnabled);
        Assert.Equal("条件引用", TestMessages.Zh(blocked[0].Note));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", "Upgrade")]
    [InlineData("2.0.0", "1.0.0", "Downgrade")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", "Upgrade")]
    [InlineData("1.0.0", "1.0.0-beta.10", "Downgrade")]
    [InlineData("1.0", "1.0.0+build", null)]
    [InlineData("1.0.0", "invalid", null)]
    [InlineData("unknown", "2.0.0", null)]
    [InlineData("1.0.0", null, null)]
    public void SelectedVersionDeterminesExtraProjectAction(string installed, string? selected, string? expected)
    {
        var state = State(false) with
        {
            SelectedVersion = selected,
            ProjectStates = [new(Api, [Occ(ApiPath, "Example.Json", installed, ReferenceKind.Direct)], ProjectAction.Uninstall, null)],
        };
        var row = Assert.Single(PresentationModel.BuildProjectRows(state));
        Assert.Equal(ProjectAction.Uninstall, row.Action);
        Assert.True(row.ActionEnabled);
        Assert.Equal(expected, row.VersionChange?.Action.ToString());
        if (expected is not null)
        {
            Assert.Equal(selected, row.VersionChange!.Version);
            Assert.True(row.VersionChange.IsEnabled);
        }
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", "Upgrade")]
    [InlineData("3.0.0", "2.0.0", "Downgrade")]
    [InlineData("3.0.0", "3.0.0", "Upgrade")]
    [InlineData("2.0.0", "2.0.0", null)]
    [InlineData("3.0.0", "1.0.0", "Downgrade")]
    [InlineData("3.0.0", "2.5.0", "SwitchVersion")]
    public void MultiFrameworkVersionActionDoesNotMislabelMixedDirections(string first, string selected, string? expected)
    {
        var state = State(false) with
        {
            SelectedVersion = selected,
            ProjectStates = [new(Api, [
                Occ(ApiPath, "Example.Json", first, ReferenceKind.Direct),
                Occ(ApiPath, "Example.Json", "2.0.0", ReferenceKind.Direct, "net8.0")], ProjectAction.Uninstall, null)],
        };
        Assert.Equal(expected, Assert.Single(PresentationModel.BuildProjectRows(state)).VersionChange?.Action.ToString());
    }

    [Fact]
    public void VersionActionsRespectBusyProtectedAndIndirectReferences()
    {
        var state = State(false) with
        {
            SelectedVersion = "2.0.0",
            ProjectStates = [
                new(Api, [Occ(ApiPath, "Example.Json", "1.0.0", ReferenceKind.Direct)], ProjectAction.Uninstall, "protected"),
                new(Core, [Occ(CorePath, "Example.Json", "1.0.0", ReferenceKind.Transitive)], ProjectAction.Install, null),
                new(Core, [Occ(CorePath, "Example.Json", "1.0.0", ReferenceKind.SdkImplicit)], ProjectAction.None, "SDK"),
            ],
        };
        var rows = PresentationModel.BuildProjectRows(state);
        Assert.False(rows[0].VersionChange!.IsEnabled);
        Assert.Null(rows[1].VersionChange);
        Assert.Null(rows[2].VersionChange);
        var busy = PresentationModel.BuildProjectRows(state with { IsBusy = true });
        Assert.All(busy, row => Assert.False(row.ActionEnabled));
        Assert.False(busy[0].VersionChange!.IsEnabled);
    }

    [Fact]
    public void QuickModeOnlySkipsConfirmationForExecutablePlans()
    {
        var plan = new OperationPlan([], [], [], ["warning"], [])
        {
            Commands = [new(PackageAction.Upgrade, ApiPath, "Example", "2.0.0", "")],
        };
        Assert.True(PresentationModel.RequiresConfirmation(plan, new()));
        Assert.False(PresentationModel.RequiresConfirmation(plan, new(QuickMode: true)));
        Assert.True(PresentationModel.RequiresConfirmation(plan with { Blockers = ["blocked"] }, new(QuickMode: true)));
        Assert.True(PresentationModel.RequiresConfirmation(plan with { Commands = [] }, new(QuickMode: true)));
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

        Assert.Contains(model.AffectedProjects, p => TestMessages.Zh(p)!.Contains("Core") && TestMessages.Zh(p)!.Contains("当前范围外"));
        Assert.DoesNotContain(model.AffectedProjects, p => TestMessages.Zh(p)!.Contains("Api") && TestMessages.Zh(p)!.Contains("当前范围外"));
        Assert.Contains(model.Changes, c => TestMessages.Zh(c)!.Contains("Example.Json") && TestMessages.Zh(c)!.Contains("3.0.0"));
        Assert.NotEmpty(model.Warnings);
        Assert.True(model.CanConfirm);
        Assert.Equal(ApiPath, Assert.Single(state.VisibleProjects).Path);

        var blocked = PresentationModel.BuildConfirmation(plan with { Blockers = ["引用受条件控制"] }, state.Scope);
        Assert.False(blocked.CanConfirm);
        Assert.Contains("引用受条件控制", (blocked.Blockers).Select(TestMessages.Zh));
    }
}
