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

internal static class OperationDialog
{
    internal static readonly Color WarningColor = Color.FromRgb(0xB4, 0x6A, 0x00);
    internal static readonly Color ErrorColor = Color.FromRgb(0xC4, 0x2B, 0x1C);
    internal static readonly Color MutedColor = Color.FromRgb(0x6B, 0x6B, 0x6B);

    /// <summary>Shows the preview of a planned change. Returns true only when the plan has no blockers and the user confirms.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, OperationPlan plan, WorkspaceScope? scope = null)
    {
        var model = PresentationModel.BuildConfirmation(plan, scope ?? WorkspaceScope.All);
        var confirmed = false;
        var dialog = new Window
        {
            Title = model.Title,
            StartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Resizable(620, 520, minWidth: 420, minHeight: 320);

        var body = new StackPanel().Vertical().Spacing(10);
        AddSection(body, "将要执行", model.Changes, null);
        AddSection(body, "受影响的项目", model.AffectedProjects, null);
        AddSection(body, "将修改的文件（执行前已备份）", model.Files, MutedColor);
        AddSection(body, "风险提示", model.Warnings, WarningColor);
        AddSection(body, "无法执行", model.Blockers, ErrorColor);
        if (!model.CanConfirm)
        {
            body.Add(new TextBlock()
                .Text(model.Blockers.Count > 0 ? "存在阻塞项，不能执行此操作。" : "没有需要执行的命令。")
                .Foreground(ErrorColor)
                .Bold());
        }

        var confirm = new Button()
            .Content(model.CanConfirm ? "确认执行" : "不能执行")
            .IsEnabled(model.CanConfirm)
            .OnClick(() =>
            {
                confirmed = model.CanConfirm;
                dialog.Close();
            });
        var cancel = new Button().Content("取消").OnClick(dialog.Close);

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
    public static Task ShowFailureAsync(Window owner, OperationResult result)
    {
        var lines = result.Recovery.Select(r => $"{StatusText(r.Status)}：{r.Path}{(r.Message is { } m ? $"（{m}）" : "")}").ToList();
        if (result.RecoveryManifestPath is { } manifest)
        {
            lines.Add($"备份与恢复清单：{manifest}");
        }
        var title = result.Status == OperationStatus.RecoveryRequired
            ? "操作失败，部分文件未能自动恢复，需要处理。"
            : "操作失败，所有文件已恢复到操作前的状态。";
        var detail = string.Join(Environment.NewLine, new[] { result.Error }.Concat(lines).Where(s => !string.IsNullOrWhiteSpace(s)));
        return MessageBox.NotifyAsync(title,
            result.Status == OperationStatus.RecoveryRequired ? PromptIconKind.Error : PromptIconKind.Warning,
            detail, owner);
    }

    internal static string StatusText(RecoveryStatus status) => status switch
    {
        RecoveryStatus.Restored => "已恢复",
        RecoveryStatus.Conflict => "冲突（文件已被外部修改，未覆盖）",
        _ => "恢复失败",
    };

    /// <summary>Startup prompt for an unfinished operation. Never changes files by itself.</summary>
    public static async Task<RecoveryChoice> ShowRecoveryAsync(Window owner, PendingRecovery pending)
    {
        var choice = RecoveryChoice.Later;
        var dialog = new Window
        {
            Title = "发现未完成的包操作",
            StartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Resizable(680, 540, minWidth: 460, minHeight: 360);

        var body = new StackPanel().Vertical().Spacing(10);
        body.Add(new TextBlock()
            .Text("上次的包操作没有正常结束（可能是程序异常退出）。请选择如何处理受影响的文件。")
            .TextWrapping(TextWrapping.Wrap));
        var summary = new List<string>();
        if (pending.Description is { } description)
        {
            summary.Add("操作：" + description);
        }
        if (pending.StartedAt is { } started)
        {
            summary.Add($"开始时间：{started.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        }
        summary.Add("备份目录（可用于手动恢复）：" + pending.BackupDirectory);
        AddSection(body, "概况", summary, null);
        if (pending.Error is { } error)
        {
            AddSection(body, "无法自动处理", [error], ErrorColor);
        }
        foreach (var group in pending.Files.GroupBy(f => f.State).OrderBy(g => g.Key))
        {
            AddSection(body, RecoveryStateTitle(group.Key), group.Select(f => $"{f.Path}（{f.Description}）").ToList(), group.Key switch
            {
                PendingFileState.Unchanged => MutedColor,
                PendingFileState.Conflict => WarningColor,
                PendingFileState.Unreadable => ErrorColor,
                _ => null,
            });
        }
        AddSection(body, "选项说明",
        [
            "撤回修改：只把“可撤回”的文件恢复为操作前内容；冲突文件保持不变，记录会保留以便手动处理。",
            "保留当前文件：不修改任何文件，删除这条备份记录。",
            "稍后处理：本次不做任何更改，下次启动时会再次提示。",
        ], MutedColor);

        var recover = new Button()
            .Content("撤回修改")
            .IsEnabled(pending.CanRecover)
            .OnClick(() =>
            {
                choice = RecoveryChoice.Recover;
                dialog.Close();
            });
        var discard = new Button()
            .Content("保留当前文件")
            .OnClick(() =>
            {
                choice = RecoveryChoice.Discard;
                dialog.Close();
            });
        var later = new Button().Content("稍后处理").OnClick(dialog.Close);

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
    public static Task ShowRecoveryResultAsync(Window owner, PendingRecovery pending, IReadOnlyList<FileRecovery> results)
    {
        var allRestored = results.All(r => r.Status == RecoveryStatus.Restored);
        var lines = results.Select(r => $"{StatusText(r.Status)}：{r.Path}{(r.Message is { } m ? $"（{m}）" : "")}").ToList();
        if (!allRestored)
        {
            lines.Add("未恢复的文件保持原样，记录已保留：" + pending.BackupDirectory);
        }
        return MessageBox.NotifyAsync(
            allRestored ? "已撤回未完成操作的修改。" : "部分文件未能自动撤回，需要手动处理。",
            allRestored ? PromptIconKind.Info : PromptIconKind.Warning,
            string.Join(Environment.NewLine, lines), owner);
    }

    private static string RecoveryStateTitle(PendingFileState state) => state switch
    {
        PendingFileState.OwnedChange => "可撤回",
        PendingFileState.Conflict => "冲突（不会自动覆盖）",
        PendingFileState.Unreadable => "无法读取",
        _ => "无需恢复",
    };

    private static void AddSection(StackPanel body, string title, IReadOnlyList<string> lines, Color? color)
    {
        if (lines.Count == 0)
        {
            return;
        }
        body.Add(new TextBlock().Text(title).Bold());
        foreach (var line in lines)
        {
            var bullet = new TextBlock().Text("•").Margin(0, 0, 6, 0).Top();
            var text = new TextBlock().Text(line).TextWrapping(TextWrapping.Wrap).Column(1);
            if (color is { } c)
            {
                bullet.Foreground(c);
                text.Foreground(c);
            }
            body.Add(new Grid().Columns("Auto,*").Margin(8, 0, 0, 0).Children(bullet, text));
        }
    }
}
