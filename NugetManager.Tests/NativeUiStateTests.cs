using Aprillz.MewUI;
using NugetManager.Domain;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class NativeUiStateTests
{
    [Fact]
    public void InstalledWorkspaceLoadingHasMiddleRing()
    {
        var activity = PresentationModel.BuildActivity(new() { Status = WorkspaceStatus.Loading, IsBusy = true }, false);
        Assert.True(activity.InstalledWorkspace);
        Assert.False(activity.InstalledCheck);
    }

    [Fact]
    public void CheckingWithExistingInventoryHasRing()
    {
        var state = new WorkspaceViewState { Workspace = new("C:\\ws", null, []), Status = WorkspaceStatus.Loading, IsBusy = true };
        Assert.True(PresentationModel.BuildActivity(state, false).InstalledCheck);
        Assert.False(PresentationModel.BuildActivity(state, true).InstalledCheck);
    }

    [Theory]
    [InlineData(WorkspaceStatus.Cancelled)]
    [InlineData(WorkspaceStatus.Failed)]
    [InlineData(WorkspaceStatus.Ready)]
    public void CancelledAndFailedChecksStopRing(object status)
    {
        var activity = PresentationModel.BuildActivity(new() { Workspace = new("C:\\ws", null, []), Status = (WorkspaceStatus)status }, false);
        Assert.False(activity.InstalledWorkspace);
        Assert.False(activity.InstalledCheck);
    }

    [Fact]
    public void DarkPaletteHasReadableSelectedWarningAndDetailText()
    {
        var theme = AppTheme.From(new Theme { Name = "Dark", Metrics = ThemeManager.DefaultMetrics,
            Palette = new Palette(ThemeManager.DefaultDarkSeed, Color.FromRgb(0, 120, 212)) });
        Assert.True(Contrast(theme.Text, theme.Row) >= 4.5);
        Assert.True(Contrast(theme.Text, theme.Selected) >= 4.5);
        Assert.True(Contrast(theme.Warning, theme.Pane) >= 4.5);
        Assert.True(Contrast(theme.Muted, theme.Pane) >= 4.5);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte v) { var x = v / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
