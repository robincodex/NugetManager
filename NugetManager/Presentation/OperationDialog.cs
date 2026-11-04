using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using NugetManager.Domain;
using NugetManager.Operations;

namespace NugetManager.Presentation;

internal enum RecoveryChoice
{
    Later,
    Recover,
    Discard,
}

internal sealed class OperationDialog(UiLanguage language, System.Globalization.CultureInfo culture)
{
    private string F(UiMessage message) => AppLocalization.Format(message, language, culture);
    private string L(TextKey key, params string[] arguments) => F(new UiMessage(key, arguments));
    private string LF(TextKey key, FormattableString format) => F(UiMessage.FromFormat(key, format));
    internal static Color WarningColor => AppTheme.Current.Warning;
    internal static Color ErrorColor => AppTheme.Current.Error;
    internal static Color MutedColor => AppTheme.Current.Muted;

    /// <summary>Shows the preview of a planned change. Returns true only when the plan has no blockers and the user confirms.</summary>
    public async Task<bool> ConfirmAsync(Window owner, OperationPlan plan, WorkspaceScope? scope = null)
    {
        var model = PresentationModel.BuildConfirmation(plan, scope ?? WorkspaceScope.All);
        var confirmed = false;
        var dialog = new Window
        {
            Title = F(model.Title),
            StartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Resizable(620, 520, minWidth: 420, minHeight: 320);

        var body = new StackPanel().Vertical().Spacing(10);
        AddSection(body, L(TextKey.M134), model.Changes, null);
        AddSection(body, L(TextKey.M135), model.AffectedProjects, null);
        AddSection(body, L(TextKey.M136), model.Files.Select(f => (UiMessage)f).ToArray(), MutedColor);
        AddSection(body, L(TextKey.M137), model.Warnings, WarningColor);
        AddSection(body, L(TextKey.M138), model.Blockers, ErrorColor);
        if (!model.CanConfirm)
        {
            body.Add(new TextBlock()
                .Text(model.Blockers.Count > 0 ? L(TextKey.M139) : L(TextKey.M140))
                .Foreground(ErrorColor)
                .Bold());
        }

        var confirm = new Button()
            .Content(model.CanConfirm ? L(TextKey.M141) : L(TextKey.M142))
            .IsEnabled(model.CanConfirm)
            .OnClick(() =>
            {
                confirmed = model.CanConfirm;
                dialog.Close();
            });
        var cancel = new Button().Content(L(TextKey.Cancel)).OnClick(dialog.Close);

        dialog.Content = new DockPanel()
            .Margin(16)
            .Children(
                new StackPanel().Horizontal().Spacing(8).HorizontalAlignment(HorizontalAlignment.Right).Margin(0, 12, 0, 0).DockBottom()
                    .Children(cancel, confirm),
                new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(body));

        await dialog.ShowDialogAsync(owner);
        return confirmed;
    }

    /// <summary>Explains a failed write, including each file's recovery status and the backup location.</summary>
    public Task ShowFailureAsync(Window owner, OperationResult result)
    {
        var lines = result.Recovery.Select(r => $"{StatusText(r.Status)}: {r.Path}{(r.Message is { } m ? $" ({F(m)})" : "")}").ToList();
        if (result.RecoveryManifestPath is { } manifest)
        {
            lines.Add(LF(TextKey.M143, $"备份与恢复清单：{manifest}"));
        }
        var title = result.Status == OperationStatus.RecoveryRequired
            ? L(TextKey.M144)
            : L(TextKey.M145);
        var detail = string.Join(Environment.NewLine, new[] { result.Error is null ? null : F(result.Error) }.Concat(lines).Where(s => !string.IsNullOrWhiteSpace(s)));
        return MessageBox.NotifyAsync(title,
            result.Status == OperationStatus.RecoveryRequired ? PromptIconKind.Error : PromptIconKind.Warning,
            detail, owner);
    }

    internal string StatusText(RecoveryStatus status) => status switch
    {
        RecoveryStatus.Restored => L(TextKey.M146),
        RecoveryStatus.Conflict => L(TextKey.M147),
        _ => L(TextKey.M148),
    };

    /// <summary>Startup prompt for an unfinished operation. Never changes files by itself.</summary>
    public async Task<RecoveryChoice> ShowRecoveryAsync(Window owner, PendingRecovery pending)
    {
        var choice = RecoveryChoice.Later;
        var dialog = new Window
        {
            Title = L(TextKey.M149),
            StartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Resizable(680, 540, minWidth: 460, minHeight: 360);

        var body = new StackPanel().Vertical().Spacing(10);
        body.Add(new TextBlock()
            .Text(L(TextKey.M150))
            .TextWrapping(TextWrapping.Wrap));
        var summary = new List<UiMessage>();
        if (pending.Description is { } description)
        {
            summary.Add(L(TextKey.M151) + F(description));
        }
        if (pending.StartedAt is { } started)
        {
            summary.Add(LF(TextKey.M152, $"开始时间：{started.LocalDateTime:yyyy-MM-dd HH:mm:ss}"));
        }
        summary.Add(L(TextKey.M153) + pending.BackupDirectory);
        AddSection(body, L(TextKey.M154), summary, null);
        if (pending.Error is { } error)
        {
            AddSection(body, L(TextKey.M155), [error], ErrorColor);
        }
        foreach (var group in pending.Files.GroupBy(f => f.State).OrderBy(g => g.Key))
        {
            AddSection(body, RecoveryStateTitle(group.Key), group.Select(f => (UiMessage)$"{f.Path} ({F(f.Description)})").ToList(), group.Key switch
            {
                PendingFileState.Unchanged => MutedColor,
                PendingFileState.Conflict => WarningColor,
                PendingFileState.Unreadable => ErrorColor,
                _ => null,
            });
        }
        AddSection(body, L(TextKey.M156),
        [
            L(TextKey.M157),
            L(TextKey.M158),
            L(TextKey.M159),
        ], MutedColor);

        var recover = new Button()
            .Content(L(TextKey.M160))
            .IsEnabled(pending.CanRecover)
            .OnClick(() =>
            {
                choice = RecoveryChoice.Recover;
                dialog.Close();
            });
        var discard = new Button()
            .Content(L(TextKey.M161))
            .OnClick(() =>
            {
                choice = RecoveryChoice.Discard;
                dialog.Close();
            });
        var later = new Button().Content(L(TextKey.M162)).OnClick(dialog.Close);

        dialog.Content = new DockPanel()
            .Margin(16)
            .Children(
                new StackPanel().Horizontal().Spacing(8).HorizontalAlignment(HorizontalAlignment.Right).Margin(0, 12, 0, 0).DockBottom()
                    .Children(later, discard, recover),
                new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(body));

        await dialog.ShowDialogAsync(owner);
        return choice;
    }

    /// <summary>Reports the per-file outcome of a startup recovery.</summary>
    public Task ShowRecoveryResultAsync(Window owner, PendingRecovery pending, IReadOnlyList<FileRecovery> results)
    {
        var allRestored = results.All(r => r.Status == RecoveryStatus.Restored);
        var lines = results.Select(r => $"{StatusText(r.Status)}: {r.Path}{(r.Message is { } m ? $" ({F(m)})" : "")}").ToList();
        if (!allRestored)
        {
            lines.Add(L(TextKey.M163) + pending.BackupDirectory);
        }
        return MessageBox.NotifyAsync(
            allRestored ? L(TextKey.M164) : L(TextKey.M165),
            allRestored ? PromptIconKind.Info : PromptIconKind.Warning,
            string.Join(Environment.NewLine, lines), owner);
    }

    private string RecoveryStateTitle(PendingFileState state) => state switch
    {
        PendingFileState.OwnedChange => L(TextKey.M166),
        PendingFileState.Conflict => L(TextKey.M167),
        PendingFileState.Unreadable => L(TextKey.M168),
        _ => L(TextKey.M169),
    };

    private void AddSection(StackPanel body, string title, IReadOnlyList<UiMessage> lines, Color? color)
    {
        if (lines.Count == 0)
        {
            return;
        }
        body.Add(new TextBlock().Text(title).Bold());
        foreach (var line in lines)
        {
            var bullet = new TextBlock().Text("•").Margin(0, 0, 6, 0).Top();
            var text = new TextBlock().Text(F(line)).TextWrapping(TextWrapping.Wrap).Column(1);
            if (color is { } c)
            {
                bullet.Foreground(c);
                text.Foreground(c);
            }
            body.Add(new Grid().Columns("Auto,*").Margin(8, 0, 0, 0).Children(bullet, text));
        }
    }
}
