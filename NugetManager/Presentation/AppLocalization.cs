using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace NugetManager.Presentation;

internal sealed record UiArgument(string? Literal, UiMessage? Message);

internal sealed record UiMessage
{
    public TextKey Key { get; }
    public IReadOnlyList<UiArgument> Arguments { get; }

    [JsonConstructor]
    public UiMessage(TextKey key, IReadOnlyList<UiArgument> arguments)
    {
        if (!Enum.IsDefined(key) || arguments is null || arguments.Any(a => a is null || (a.Literal is null) == (a.Message is null))
            || (key == TextKey.Join ? arguments.Count == 0
                : arguments.Count < CompositeFormat.Parse(AppLocalization.Template(key, UiLanguage.English)).MinimumArgumentCount))
            throw UiErrors.Json(new UiMessage(TextKey.InvalidLocalizedMessage));
        Key = key;
        Arguments = Array.AsReadOnly(arguments.ToArray());
    }

    public UiMessage(TextKey key, params string[] arguments) : this(key,
        Array.AsReadOnly(arguments.Select(a => new UiArgument(a, null)).ToArray()))
    {
    }

    public static implicit operator UiMessage(string text) => new(TextKey.Literal, text);
    public override string ToString() => AppLocalization.Format(this, UiLanguage.SimplifiedChinese, CultureInfo.InvariantCulture);

    public static UiMessage FromException(Exception exception) => exception is IUiException localized ? localized.UiMessage
        : exception.Data["NugetManager.UiMessage"] is UiMessage message ? message : exception.Message;

    public static UiMessage FromFormat(TextKey key, FormattableString format)
    {
        var args = format.GetArguments().Select((argument, index) =>
        {
            if (argument is UiMessage message) return new UiArgument(null, message);
            var spec = Regex.Match(format.Format, @"\{" + index + @"(?:,[^}:]+)?(?::([^}]+))?\}").Groups[1].Value;
            return new UiArgument(argument is IFormattable value ? value.ToString(spec.Length == 0 ? null : spec, CultureInfo.InvariantCulture)
                : argument?.ToString() ?? "", null);
        }).ToArray();
        return new(key, Array.AsReadOnly(args));
    }

    public static UiMessage Join(string separator, IEnumerable<UiMessage> messages) =>
        new(TextKey.Join, Array.AsReadOnly(new[] { new UiArgument(separator, null) }
            .Concat(messages.Select(m => new UiArgument(null, m))).ToArray()));
    public static UiMessage operator +(UiMessage left, UiMessage right) => Join("", [left, right]);
}

internal interface IUiException
{
    UiMessage UiMessage { get; }
}

internal static class UiErrors
{
    private static T WithMessage<T>(T exception, UiMessage message) where T : Exception
    {
        exception.Data["NugetManager.UiMessage"] = message;
        return exception;
    }
    public static InvalidOperationException InvalidOperation(UiMessage message) => WithMessage(new InvalidOperationException(message.ToString()), message);
    public static ArgumentException Argument(UiMessage message, string? parameter = null) => WithMessage(new ArgumentException(message.ToString(), parameter), message);
    public static InvalidDataException InvalidData(UiMessage message) => WithMessage(new InvalidDataException(message.ToString()), message);
    public static IOException IO(UiMessage message) => WithMessage(new IOException(message.ToString()), message);
    public static JsonException Json(UiMessage message) => WithMessage(new JsonException(message.ToString()), message);
    public static OperationCanceledException OperationCanceled(UiMessage message, CancellationToken ct) =>
        WithMessage(new OperationCanceledException(message.ToString(), ct), message);
}

internal static class Messages
{
    public static UiMessage M(TextKey key) => new(key);
    public static UiMessage MF(TextKey key, FormattableString format) => UiMessage.FromFormat(key, format);
}

internal static partial class AppLocalization
{
    private static readonly IReadOnlyDictionary<TextKey, string[]> Resources = new Dictionary<TextKey, string[]>
    {
        [TextKey.Literal] = ["{0}", "{0}", "{0}", "{0}"],
        [TextKey.LoadingWorkspace] = ["正在加载工作区…", "Loading workspace…", "작업 영역 로드 중…", "Загрузка рабочей области…"],
        [TextKey.ErrorDetail] = ["错误：{0}", "Error: {0}", "오류: {0}", "Ошибка: {0}"],
        [TextKey.AppTitle] = ["NuGet 包管理器", "NuGet Package Manager", "NuGet 패키지 관리자", "Менеджер пакетов NuGet"],
        [TextKey.System] = ["跟随系统", "System", "시스템 설정", "Системная"],
        [TextKey.Light] = ["浅色", "Light", "밝게", "Светлая"],
        [TextKey.Dark] = ["深色", "Dark", "어둡게", "Тёмная"],
        [TextKey.Installed] = ["已安装", "Installed", "설치됨", "Установлено"],
        [TextKey.Browse] = ["浏览", "Browse", "찾아보기", "Обзор"],
        [TextKey.OpenDirectory] = ["打开目录…", "Open folder…", "폴더 열기…", "Открыть папку…"],
        [TextKey.Refresh] = ["刷新", "Refresh", "새로 고침", "Обновить"],
        [TextKey.Cancel] = ["取消", "Cancel", "취소", "Отмена"],
        [TextKey.Prerelease] = ["包含预发布版本", "Include prerelease", "시험판 포함", "Предварительные версии"],
        [TextKey.AllSources] = ["全部包源", "All sources", "모든 소스", "Все источники"],
        [TextKey.SelectedSources] = ["已选 {0} 个源", "{0} sources selected", "소스 {0}개 선택됨", "Выбрано источников: {0}"],
        [TextKey.NoSources] = ["未选包源", "No sources selected", "선택한 소스 없음", "Источники не выбраны"],
        [TextKey.SelectSources] = ["请选择包源后浏览。", "Select sources to browse.", "찾아볼 소스를 선택하세요.", "Выберите источники для обзора."],
        [TextKey.OpenNuGet] = ["打开 NuGet 页面", "Open NuGet page", "NuGet 페이지 열기", "Открыть страницу NuGet"],
        [TextKey.OpenGitHub] = ["打开 GitHub 仓库", "Open GitHub repository", "GitHub 저장소 열기", "Открыть репозиторий GitHub"],
        [TextKey.CheckingUpdates] = ["正在检测更新…", "Checking for updates…", "업데이트 확인 중…", "Проверка обновлений…"],
        [TextKey.Ready] = ["就绪", "Ready", "준비됨", "Готово"],
        [TextKey.PreferencesReadFailed] = ["无法读取偏好设置，已使用系统默认：{0}", "Cannot read preferences; using system defaults: {0}", "설정을 읽을 수 없어 시스템 기본값을 사용합니다: {0}", "Не удалось прочитать настройки; используются системные: {0}"],
        [TextKey.PreferencesWriteFailed] = ["偏好设置未保存：{0}", "Preferences were not saved: {0}", "설정이 저장되지 않았습니다: {0}", "Настройки не сохранены: {0}"],
        [TextKey.InvalidLocalizedMessage] = ["本地化消息记录无效。", "Invalid localized message record.", "현지화 메시지 기록이 올바르지 않습니다.", "Некорректная запись локализованного сообщения."],
        [TextKey.InvalidWebLink] = ["只能打开不含凭据的 HTTP(S) 绝对链接。", "Only absolute HTTP(S) links without credentials can be opened.", "자격 증명이 없는 HTTP(S) 절대 링크만 열 수 있습니다.", "Можно открывать только абсолютные HTTP(S)-ссылки без учётных данных."],
        [TextKey.BrowserDidNotStart] = ["系统浏览器未启动。", "The system browser did not start.", "시스템 브라우저가 시작되지 않았습니다.", "Системный браузер не запустился."],
        [TextKey.UnknownInstalledSources] = ["{0} 个包的还原来源未知或不在当前包源中，仅在全部包源中显示。", "{0} packages have an unknown or unconfigured restore source and appear only under All sources.", "패키지 {0}개의 복원 소스를 알 수 없거나 현재 소스에 없어 모든 소스에서만 표시됩니다.", "Источник восстановления {0} пакетов неизвестен или не настроен; они видны только при выборе всех источников."],
        [TextKey.SelectInstalledSources] = ["请选择包源以显示已安装的包。", "Select sources to show installed packages.", "설치된 패키지를 표시할 소스를 선택하세요.", "Выберите источники для отображения установленных пакетов."],
        [TextKey.UpgradeToVersion] = ["升级到 {0}", "Upgrade to {0}", "{0}(으)로 업그레이드", "Обновить до {0}"],
        [TextKey.DowngradeToVersion] = ["降级到 {0}", "Downgrade to {0}", "{0}(으)로 다운그레이드", "Понизить до {0}"],
        [TextKey.SwitchToVersion] = ["切换到版本 {0}", "Switch to version {0}", "버전 {0}(으)로 전환", "Переключить на версию {0}"],
        [TextKey.Downgrade] = ["降级", "Downgrade", "다운그레이드", "Понизить версию"],
        [TextKey.QuickMode] = ["快速模式", "Quick mode", "빠른 모드", "Быстрый режим"],
        [TextKey.QuickModeHint] = ["立即执行所有包操作，不显示确认（包括批量操作、降级和集中版本影响）；保留安全检查与回滚。此设置会保存。", "Execute all package actions without confirmation, including batch actions, downgrades and central version changes. Safety checks and rollback remain enabled. This setting is saved.", "일괄 작업, 다운그레이드 및 중앙 버전 변경을 포함한 모든 패키지 작업을 확인 없이 실행합니다. 안전 검사와 롤백은 유지되며 설정은 저장됩니다.", "Выполнять все действия без подтверждения, включая массовые операции, понижение и централизованные версии. Проверки безопасности и откат сохраняются. Настройка сохраняется."],
        [TextKey.OperationBlocked] = ["无法执行包操作", "Package operation cannot proceed", "패키지 작업을 실행할 수 없습니다", "Невозможно выполнить операцию с пакетом"],
        [TextKey.SwitchVersion] = ["切换版本", "Switch version", "버전 전환", "Сменить версию"],
        [TextKey.DowngradePackage] = ["降级 {0} 到 {1}（{2}）", "Downgrade {0} to {1} ({2})", "{0}을(를) {1}(으)로 다운그레이드 ({2})", "Понизить {0} до {1} ({2})"],
        [TextKey.SwitchPackageVersion] = ["切换 {0} 到版本 {1}（{2}）", "Switch {0} to version {1} ({2})", "{0}을(를) 버전 {1}(으)로 전환 ({2})", "Переключить {0} на версию {1} ({2})"],
        [TextKey.ConfirmDowngrade] = ["确认降级", "Confirm downgrade", "다운그레이드 확인", "Подтвердить понижение версии"],
        [TextKey.ConfirmSwitchVersion] = ["确认切换版本", "Confirm version switch", "버전 전환 확인", "Подтвердить смену версии"],
        [TextKey.MajorVersionChange] = ["{0} 将从 {1} 切换到 {2}，跨越主版本，可能包含不兼容变更。", "{0} will change from {1} to {2}, crossing a major version and potentially introducing incompatible changes.", "{0}이(가) {1}에서 {2}(으)로 변경됩니다. 주 버전 변경으로 호환되지 않는 변경 사항이 포함될 수 있습니다.", "{0} изменится с {1} на {2} с переходом между основными версиями; возможны несовместимые изменения."],
    };

    public static UiLanguage ResolveLanguage(UiLanguage language, CultureInfo culture) => language != UiLanguage.System ? language
        : culture.TwoLetterISOLanguageName switch
        {
            "zh" => UiLanguage.SimplifiedChinese,
            "ko" => UiLanguage.Korean,
            "ru" => UiLanguage.Russian,
            _ => UiLanguage.English,
        };

    public static string Template(TextKey key, UiLanguage language) =>
        (Resources.TryGetValue(key, out var resource) ? resource : ExtraResources[key])[language switch
        {
            UiLanguage.SimplifiedChinese => 0, UiLanguage.English => 1,
            UiLanguage.Korean => 2, UiLanguage.Russian => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        }];

    public static string Format(UiMessage message, UiLanguage language, CultureInfo culture)
    {
        string Argument(UiArgument a) => a.Message is { } nested ? Format(nested, language, culture) : a.Literal ?? "";
        return message.Key == TextKey.Join ? string.Join(message.Arguments[0].Literal, message.Arguments.Skip(1).Select(Argument))
            : string.Format(culture, Template(message.Key, ResolveLanguage(language, culture)), message.Arguments.Select(Argument).Cast<object>().ToArray());
    }
}
