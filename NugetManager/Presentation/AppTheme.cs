using Aprillz.MewUI;

namespace NugetManager.Presentation;

internal sealed record AppTheme(Color Pane, Color Row, Color Header, Color Border, Color Selected,
    Color Text, Color Muted, Color Accent, Color Warning, Color Error)
{
    public static AppTheme Current => From(Application.Current.Theme);

    public static AppTheme From(Theme theme)
    {
        var p = theme.Palette;
        var selected = Mix(p.ControlBackground, p.WindowText, .12);
        return new(p.WindowBackground, p.ControlBackground, p.ContainerBackground, p.ControlBorder, selected,
            p.WindowText, Mix(p.WindowBackground, p.WindowText, .75), p.Accent,
            theme.IsDark ? Color.FromRgb(255, 195, 91) : Color.FromRgb(133, 77, 0), p.Error);
    }

    private static Color Mix(Color background, Color foreground, double amount) => Color.FromRgb(
        (byte)Math.Round(background.R * (1 - amount) + foreground.R * amount),
        (byte)Math.Round(background.G * (1 - amount) + foreground.G * amount),
        (byte)Math.Round(background.B * (1 - amount) + foreground.B * amount));
}
