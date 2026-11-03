using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using NugetManager.Domain;
using NugetManager.Operations;

namespace NugetManager.Presentation;

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

    private static void AddSection(StackPanel body, string title, IReadOnlyList<string> lines, Color? color)
    {
        if (lines.Count == 0)
        {
            return;
        }
        body.Add(new TextBlock().Text(title).Bold());
        foreach (var line in lines)
        {
            var text = new TextBlock().Text("• " + line).TextWrapping(TextWrapping.Wrap).Margin(8, 0, 0, 0);
            if (color is { } c)
            {
                text.Foreground(c);
            }
            body.Add(text);
        }
    }
}
