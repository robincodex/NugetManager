using System.Reflection;
using System.Runtime.ExceptionServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using NugetManager.Domain;
using NugetManager.Presentation;

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
                var controller = new WorkspaceController(new FakeDiscovery(new("C:\\fixture", null, [])), new FakeSdk(),
                    new FakeCatalog(), metadata, new FakeOperations());
                Application.Create().UseWin32().UseDirect2D().BuildMainWindow(() =>
                {
                    var window = MainWindow.Create(controller);
                    return window.OnLoaded(async () =>
                    {
                        try { await CheckBindings(window, metadata); }
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
                Assert.Equal(AppLocalization.Template(TextKey.AppTitle, language), window.Title);
                var statusBar = Assert.IsType<Border>(Field<TextBlock>(window, "_statusText").Parent!.Parent);
                Assert.Equal(AppTheme.Current.Header, statusBar.Background);
                CheckActionColors(window, state);
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
        }
    }

    private static T Field<T>(Window window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Set(Window window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static object? Invoke(Window window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
}
