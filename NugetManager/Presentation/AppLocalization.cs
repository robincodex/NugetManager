using System.Globalization;

namespace NugetManager.Presentation;

internal enum TextKey
{
    Literal, LoadingWorkspace, ErrorDetail, AppTitle, System, Light, Dark, Installed, Browse,
    OpenDirectory, Refresh, Cancel, Prerelease, AllSources, SelectedSources, NoSources, SelectSources,
    OpenNuGet, OpenGitHub, CheckingUpdates, Ready, PreferencesReadFailed, PreferencesWriteFailed,
}

internal sealed record UiMessage
{
    public TextKey Key { get; }
    public IReadOnlyList<string> Arguments { get; }

    public UiMessage(TextKey key, params string[] arguments)
    {
        Key = key;
        Arguments = Array.AsReadOnly((string[])arguments.Clone());
    }
}

internal static class AppLocalization
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
        Resources[key][language switch
        {
            UiLanguage.SimplifiedChinese => 0, UiLanguage.English => 1,
            UiLanguage.Korean => 2, UiLanguage.Russian => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        }];

    public static string Format(UiMessage message, UiLanguage language, CultureInfo culture) =>
        string.Format(culture, Template(message.Key, ResolveLanguage(language, culture)), message.Arguments.Cast<object>().ToArray());
}
