using System.Reflection;
using System.Runtime.ExceptionServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using NugetManager.Domain;
using NugetManager.Presentation;
using NugetManager.Operations;
using NugetManager.Packages;

namespace NugetManager.Tests;

public class NativeWindowBindingTests
{
    [Fact]
    public void ThemeAndLanguageChangesPreserveWorkspaceSelectionAndPage()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native MewUI binding checks require Windows.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var metadata = new FakeMetadata();
                var operations = new FakeOperations();
                var controller = new WorkspaceController(new FakeDiscovery(new("C:\\fixture", null, [])), new FakeSdk(),
                    new FakeCatalog(), metadata, operations);
                Application.Create().UseWin32().UseDirect2D().BuildMainWindow(() =>
                {
                    var window = MainWindow.Create(controller);
                    return window.OnLoaded(async () =>
                    {
                        try
                        {
                            await CheckBindings(window, metadata);
                            await CheckConfigurationPage(window);
                            await CheckQuickMode(window, controller, operations);
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { window.Close(); }
                    });
                }).Run();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native binding check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task CheckBindings(Window window, FakeMetadata metadata)
    {
        Assert.NotNull(window.Icon);
        var state = new WorkspaceViewState
        {
            Workspace = new("C:\\fixture", null, []),
            Status = WorkspaceStatus.Ready,
            SearchQuery = "Example",
            Search = new([new("Example", "1.0", "local", null)], 20, 20, true, []),
            SelectedPackageId = "Example",
            SelectedPackageSourceName = "local",
            SelectedVersion = "1.0",
            SelectedPackageIds = new HashSet<string> { "Checked" },
            VersionsLoading = true,
        };
        Set(window, "_browseMode", true);
        Set(window, "_suppressEvents", true);
        Field<TextBox>(window, "_searchBox").Text = state.SearchQuery;
        Invoke(window, "Render", state);
        Assert.True(Field<ProgressRing>(window, "_versionsRing").IsActive);
        state = state with { VersionsLoading = false };
        Invoke(window, "Render", state);
        Assert.False(Field<ProgressRing>(window, "_versionsRing").IsActive);
        Assert.IsType<SegmentedControl>(Field<SegmentedControl>(window, "_tabs"));
        Assert.True(Field<Button>(window, "_sourcePicker").IsEnabled);
        foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English, UiLanguage.Korean, UiLanguage.Russian })
        {
            foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark, UiTheme.System })
            {
                await (Task)Invoke(window, "ChangePreferencesAsync", new AppPreferences(theme, language))!;
                Assert.Same(state, Field<WorkspaceViewState>(window, "_state"));
                Assert.Equal("Example", Field<TextBox>(window, "_searchBox").Text);
                Assert.Equal(1, Field<SegmentedControl>(window, "_tabs").SelectedIndex);
                Assert.Equal(40, state.Search!.NextSkip);
                Assert.Equal(["Checked"], state.SelectedPackageIds);
                Assert.Equal(AppLocalization.Template(TextKey.AppTitle, language) + " — fixture", window.Title);
                var sections = Field<TabControl>(window, "_sections");
                Assert.Equal(2, sections.Tabs.Count);
                Assert.Equal(AppLocalization.Template(TextKey.PackagesTab, language), sections.Tabs[0].HeaderText);
                Assert.Equal(AppLocalization.Template(TextKey.ConfigurationTab, language), sections.Tabs[1].HeaderText);
                sections.SelectedIndex = 1;
                Assert.Same(state, Field<WorkspaceViewState>(window, "_state"));
                sections.SelectedIndex = 0;
                var statusBar = Assert.IsType<Border>(Field<TextBlock>(window, "_statusText").Parent!.Parent);
                Assert.Equal(AppTheme.Current.Header, statusBar.Background);
                var quickMode = Field<CheckBox>(window, "_quickMode");
                var toolbar = Assert.IsType<Border>(quickMode.Parent!.Parent!.Parent);
                toolbar.Measure(new Size(980, 100));
                toolbar.Arrange(new Rect(0, 0, 980, toolbar.DesiredSize.Height));
                var languageBounds = Field<ComboBox>(window, "_themePicker").Bounds;
                Assert.True(quickMode.Bounds.X + quickMode.Bounds.Width <= languageBounds.X, "Quick mode must not overlap theme controls.");
                CheckActionColors(window, state);
                CheckOperationActivity(window, state, language);
                var tabs = Field<SegmentedControl>(window, "_tabs");
                tabs.Measure(new Size(650, 50));
                tabs.Arrange(new Rect(0, 0, 650, 50));
                Assert.InRange(tabs.Bounds.Width, 80, 340);
                Assert.True(tabs.Bounds.Width >= tabs.DesiredSize.Width, "Tab labels must not be clipped.");
                foreach (var showError in new[] { false, true })
                {
                    Invoke(window, "Render", state with
                    {
                        IsBusy = !showError,
                        Progress = showError ? null : new UiMessage(TextKey.M093),
                        Error = showError ? new UiMessage(TextKey.M093) : null,
                    });
                    statusBar.Measure(new Size(800, 44));
                    statusBar.Arrange(new Rect(0, 0, 800, 44));
                    var statusBounds = Field<TextBlock>(window, "_statusText").Bounds;
                    var detailBounds = Field<TextBlock>(window, showError ? "_errorText" : "_progressText").Bounds;
                    Assert.InRange(Math.Abs(statusBounds.Y + statusBounds.Height / 2
                        - detailBounds.Y - detailBounds.Height / 2), 0, 1);
                }
                Invoke(window, "Render", state);
            }
        }
        Set(window, "_browseMode", false);
        Invoke(window, "Render", state);
        Assert.True(Field<Button>(window, "_sourcePicker").IsEnabled);
        Set(window, "_browseMode", true);
        Invoke(window, "Render", state with { IsBusy = true });
        Assert.False(Field<Button>(window, "_sourcePicker").IsEnabled);
        Invoke(window, "Render", state);
        Invoke(window, "Render", state with { Workspace = new("C:\\fixture", "C:\\fixture\\Example.slnx", []) });
        Assert.EndsWith(" — Example.slnx", window.Title);
        var workspaceText = Field<TextBlock>(window, "_workspaceText");
        Assert.Equal("C:\\fixture\\Example.slnx", workspaceText.Text);
        Assert.Same(Field<TextBlock>(window, "_statusText").Parent, workspaceText.Parent);
        Assert.Equal(workspaceText.Text, Assert.IsType<TextBlock>(workspaceText.ToolTip).Text);
        Invoke(window, "Render", state with { Workspace = new("C:\\fixture\\", null, []) });
        Assert.EndsWith(" — fixture", window.Title);
        Invoke(window, "Render", state with { Workspace = null });
        Assert.Equal(AppLocalization.Template(TextKey.AppTitle, ((MainWindow)window).Language), window.Title);
        Invoke(window, "Render", state);
        metadata.Read = (id, version) => Task.FromResult(new PackageDetails(id, version, null, null, null, null, [], [], [])
        {
            SourceName = "clicked",
            SourceLocation = "https://api.nuget.org/v3/index.json",
            RepositoryUrl = new("https://github.com/clicked/repo"),
        });
        var rightClick = new MouseEventArgs(new(20, 20), new(20, 20), MouseButton.Right, false, true, false, 1, ModifierKeys.None);
        Invoke(window, "PackagePointer", rightClick, "Unselected", "2.0", "clicked");
        var menu = Field<ContextMenu>(window, "_openMenu");
        Assert.NotNull(menu.Parent);
        var entries = menu.Items.Cast<MenuItem>().ToArray();
        Assert.True(entries[0].IsEnabled);
        Assert.Equal("https://www.nuget.org/packages/Unselected/2.0", Assert.IsType<Uri>(entries[0].CommandData).AbsoluteUri);
        Assert.Equal("https://github.com/clicked/repo", Assert.IsType<Uri>(entries[1].CommandData).AbsoluteUri);
        Assert.Equal("Example", Field<WorkspaceViewState>(window, "_state").SelectedPackageId);
        Assert.Equal(["Checked"], Field<WorkspaceViewState>(window, "_state").SelectedPackageIds);

        var pending = new TaskCompletionSource<PackageDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        metadata.Read = (_, _) => pending.Task;
        Invoke(window, "PackagePointer", rightClick, "Delayed", "3.0", "clicked");
        menu = Field<ContextMenu>(window, "_openMenu");
        typeof(Window).GetMethod("CloseAllPopups", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Assert.Null(menu.Parent);
        pending.SetResult(new("Delayed", "3.0", null, null, null, null, [], [], [])
        {
            SourceName = "clicked", SourceLocation = "https://api.nuget.org/v3/index.json",
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Field<PackageLinkMenu>(window, "_links").State.Loading)
            await Task.Delay(5, timeout.Token);
        Assert.All(menu.Items.Cast<MenuItem>(), item => Assert.False(item.IsEnabled));
        Assert.Null(Field<PackageLinkMenu>(window, "_links").State.Identity);
    }

    private static void CheckOperationActivity(Window window, WorkspaceViewState state, UiLanguage language)
    {
        var expected = language switch
        {
            UiLanguage.SimplifiedChinese => "正在更新依赖，请耐心等待…",
            UiLanguage.Korean => "종속성을 업데이트하는 중입니다. 잠시 기다려 주세요…",
            UiLanguage.Russian => "Обновление зависимостей, пожалуйста, подождите…",
            _ => "Updating dependencies, please wait…",
        };
        foreach (var browse in new[] { false, true })
        {
            Set(window, "_browseMode", browse);
            foreach (var applying in new[] { false, true })
            {
                Set(window, "_applying", applying);
                var active = state with { IsBusy = !applying, Status = applying ? WorkspaceStatus.Ready : WorkspaceStatus.Writing };
                Invoke(window, "Render", active);
                var middle = Field<StackPanel>(window, "_middle");
                var line = Assert.IsType<StackPanel>(middle.Children[0]);
                Assert.True(Assert.IsType<ProgressRing>(line.Children[0]).IsActive);
                Assert.Equal(expected, Assert.IsType<TextBlock>(line.Children[1]).Text);
                Set(window, "_applying", false);
                foreach (var status in new[] { WorkspaceStatus.Ready, WorkspaceStatus.Failed, WorkspaceStatus.Cancelled })
                {
                    Invoke(window, "Render", state with { Status = status });
                    Assert.False(Field<ProgressRing>(window, "_installedRing").IsActive);
                    Assert.DoesNotContain(middle.Children.OfType<StackPanel>(),
                        panel => panel.Children.OfType<TextBlock>().Any(text => text.Text == expected));
                }
            }
        }
        Set(window, "_browseMode", true);
        Invoke(window, "Render", state);
    }

    private static void CheckActionColors(Window window, WorkspaceViewState state)
    {
        foreach (var action in new[] { ProjectAction.Install, ProjectAction.Uninstall })
        {
            foreach (var enabled in new[] { false, true })
            {
                var row = new ProjectRowModel("C:\\fixture\\App.csproj", "App", new UiMessage(TextKey.M093),
                    action, new UiMessage(TextKey.M093), enabled, null);
                var border = Assert.IsType<Border>(Invoke(window, "ProjectRow", row, "Example", state));
                var grid = Assert.IsType<Grid>(border.Child);
                var button = Assert.IsType<Button>(grid.Children[1]);
                Assert.IsType<GlyphElement>(button.Content);
                Assert.NotNull(button.ToolTip);
                var change = Assert.IsType<PackageChange>(button.CommandData);
                Assert.Equal(action == ProjectAction.Install ? PackageAction.Install : PackageAction.Uninstall, change.Action);
                Assert.Equal("Example", change.PackageId);
                Assert.Equal(row.ProjectPath, change.ProjectPath);
                Assert.Equal(enabled, button.IsEnabled);
                if (action == ProjectAction.Install)
                {
                    Assert.True(button.Background.G > button.Background.R, "Install must use a green background.");
                    Assert.True(button.Foreground.G > button.Foreground.R, "Install must use green text.");
                }
                else
                {
                    Assert.True(button.Background.R > button.Background.G, "Uninstall must use a red background.");
                    Assert.True(button.Foreground.R > button.Foreground.G, "Uninstall must use red text.");
                }
            }
            foreach (var (version, installed, expectedGlyph, expectedAction) in new[]
            {
                ("3.0.0", new[] { "1.0.0", "2.0.0" }, GlyphKind.ChevronUp, PackageAction.Upgrade),
                ("1.0.0", new[] { "2.0.0", "3.0.0" }, GlyphKind.ChevronDown, PackageAction.Downgrade),
                ("2.0.0", new[] { "1.0.0", "3.0.0" }, GlyphKind.ChevronRight, PackageAction.SwitchVersion),
            })
            {
                foreach (var busy in new[] { false, true })
                {
                    var selected = state with
                    {
                        SelectedVersion = version,
                        IsBusy = busy,
                        ProjectStates = [new(new("C:\\fixture\\App.csproj", "App"),
                            installed.Select((v, i) => new PackageOccurrence("C:\\fixture\\App.csproj", $"net{i + 8}.0",
                                "Example", v, v, ReferenceKind.Direct, null)).ToList(), ProjectAction.Uninstall, null)],
                    };
                    Invoke(window, "Render", selected);
                    var details = Field<StackPanel>(window, "_details");
                    var project = Assert.Single(details.Children.OfType<Border>(), b => b.Child is Grid g && g.Children.OfType<Button>().Any());
                    var buttons = Assert.IsType<Grid>(project.Child).Children.OfType<Button>().ToList();
                    Assert.Equal(2, buttons.Count);
                    var versionButton = Assert.Single(buttons, b => Assert.IsType<PackageChange>(b.CommandData).Action != PackageAction.Uninstall);
                    Assert.Equal(expectedGlyph, Assert.IsType<GlyphElement>(versionButton.Content).Kind);
                    Assert.Equal(!busy, versionButton.IsEnabled);
                    var command = Assert.IsType<PackageChange>(versionButton.CommandData);
                    Assert.Equal(expectedAction, command.Action);
                    Assert.Equal(version, command.Version);
                    Assert.Equal(selected.ProjectStates[0].Project.Path, command.ProjectPath);
                    Assert.Equal("Example", command.PackageId);
                    var tooltip = Assert.IsType<TextBlock>(versionButton.ToolTip);
                    Assert.Contains(version, tooltip.Text);
                    var key = expectedAction switch
                    {
                        PackageAction.Upgrade => TextKey.UpgradeToVersion,
                        PackageAction.Downgrade => TextKey.DowngradeToVersion,
                        _ => TextKey.SwitchToVersion,
                    };
                    Assert.Equal(AppLocalization.Format(new UiMessage(key, version), ((MainWindow)window).Language,
                        System.Globalization.CultureInfo.CurrentUICulture), tooltip.Text);
                }
            }
            Invoke(window, "Render", state);
        }
    }

    private static async Task CheckQuickMode(Window window, WorkspaceController controller, FakeOperations operations)
    {
        await controller.OpenAsync("C:\\fixture", null, CancellationToken.None);
        Assert.False(Field<CheckBox>(window, "_quickMode").IsChecked == true);
        await (Task)Invoke(window, "ChangePreferencesAsync", new AppPreferences(QuickMode: true))!;
        Assert.True(Field<CheckBox>(window, "_quickMode").IsChecked);
        var executions = 0;
        var pendingPreview = new TaskCompletionSource<OperationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<OperationPlan>> pending = _ => pendingPreview.Task;
        var applying = (Task)Invoke(window, "ApplyAsync", pending)!;
        Assert.False(Field<CheckBox>(window, "_quickMode").IsEnabled);
        Assert.True(Field<ProgressRing>(window, "_installedRing").IsActive);
        var duplicatePreviewCalls = 0;
        Func<CancellationToken, Task<OperationPlan>> duplicate = _ =>
        {
            duplicatePreviewCalls++;
            return pendingPreview.Task;
        };
        await (Task)Invoke(window, "ApplyAsync", duplicate)!;
        Assert.Equal(0, duplicatePreviewCalls);
        pendingPreview.SetException(new InvalidOperationException("preview failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => applying);
        Assert.True(Field<CheckBox>(window, "_quickMode").IsEnabled);
        Assert.False(Field<ProgressRing>(window, "_installedRing").IsActive);
        operations.Execute = (_, _) =>
        {
            executions++;
            Assert.False(Field<CheckBox>(window, "_quickMode").IsEnabled);
            return Task.FromResult(new OperationResult(OperationStatus.Succeeded, null, []));
        };
        var changes = new[] { PackageAction.Install, PackageAction.Uninstall, PackageAction.Upgrade,
            PackageAction.Downgrade, PackageAction.SwitchVersion }.Select(a =>
                new PackageChange(a, "C:\\fixture\\App.csproj", "Example", a == PackageAction.Uninstall ? null : "2.0.0")).ToList();
        foreach (var group in changes.Select(c => new[] { c }).Append(changes.ToArray()))
        {
            var plan = new OperationPlan(group, [], [], ["warning"], [])
            {
                Commands = group.Select(c => new PlannedCommand(c.Action, c.ProjectPath, c.PackageId, c.Version, "")).ToList(),
            };
            Func<CancellationToken, Task<OperationPlan>> preview = _ => Task.FromResult(plan);
            await ((Task)Invoke(window, "ApplyAsync", preview)!).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(6, executions);
        var blocked = new OperationPlan(changes, [], [], [], ["protected reference"]);
        Func<CancellationToken, Task<OperationPlan>> blockedPreview = _ => Task.FromResult(blocked);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => (Task)Invoke(window, "ApplyAsync", blockedPreview)!);
        Assert.Contains("protected reference", failure.Message);
        Assert.Equal(6, executions);
        Assert.True(Field<CheckBox>(window, "_quickMode").IsEnabled);
        await (Task)Invoke(window, "ChangePreferencesAsync", new AppPreferences())!;
        Assert.False(Field<CheckBox>(window, "_quickMode").IsChecked == true);
    }

    private static async Task CheckConfigurationPage(Window window)
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var parent = ws.Write("NuGet.Config", """<configuration><packageSources><add key="same" value="https://example.test/parent"/></packageSources></configuration>""");
        var local = ws.Write("project/NuGet.Config", """<configuration><packageSources><clear/><add key="same" value="https://example.test/local"/></packageSources></configuration>""");
        var editable = true;
        var saves = 0;
        var page = new ConfigurationPage(window, message => AppLocalization.Format(message, UiLanguage.English, System.Globalization.CultureInfo.InvariantCulture), () => editable,
            () => { saves++; return Task.CompletedTask; }, () => { });
        page.Update(Path.GetDirectoryName(local));
        var filter = Field<ComboBox>(page, "_files");
        Assert.Equal(0, filter.SelectedIndex);
        Assert.False(Field<Button>(page, "_open").IsEnabled);
        var snapshots = Field<List<ConfigSnapshot>>(page, "_snapshots");
        Assert.Equal(2, snapshots.Where(s => s.Path == parent || s.Path == local).SelectMany(s => s.Sources).Count(s => s.Name == "same"));
        var paths = Field<IReadOnlyList<string>>(page, "_paths");
        filter.SelectedIndex = paths.ToList().IndexOf(local) + 1;
        Assert.True(Field<Button>(page, "_open").IsEnabled);
        var rows = Field<StackPanel>(page, "_rows");
        var row = Assert.IsType<Grid>(Assert.IsType<Border>(rows.Children[1]).Child);
        Assert.Equal("same", Assert.IsType<TextBlock>(row.Children[0]).Text);
        Assert.Equal("https://example.test/local", Assert.IsType<TextBlock>(row.Children[1]).Text);
        Assert.Equal(2, rows.Children.Count);
        page.Content.Measure(new Size(1280, 720));
        page.Content.Arrange(new Rect(0, 0, 1280, 720));
        Assert.InRange(rows.DesiredSize.Width, 1035, 1256);
        editable = false;
        page.Update(Path.GetDirectoryName(local));
        row = Assert.IsType<Grid>(Assert.IsType<Border>(rows.Children[1]).Child);
        Assert.All(row.Children.OfType<Button>(), button => Assert.False(button.IsEnabled));
        Assert.All(row.Children.OfType<CheckBox>(), box => Assert.False(box.IsEnabled));
        editable = true;
        var snapshot = snapshots.Single(s => s.Path == local);
        await (Task)Invoke(page, "SaveAsync", snapshot, snapshot.Sources[0], snapshot.Sources[0] with { Enabled = false }, null!, false, false)!;
        Assert.Equal(1, saves);
        Assert.False(NuGetConfiguration.Read(local).Sources[0].Enabled);
        Assert.Equal(paths.ToList().IndexOf(local) + 1, filter.SelectedIndex);
        await File.AppendAllTextAsync(local, "\n<!--external-->");
        page.Update(Path.GetDirectoryName(local), reload: true);
        filter.SelectedIndex = 0;
        Assert.False(Field<Button>(page, "_open").IsEnabled);
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
}
