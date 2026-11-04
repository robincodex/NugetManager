using System.Globalization;
using System.Text;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class AppLocalizationTests
{
    [Theory]
    [InlineData("zh-TW", UiLanguage.SimplifiedChinese)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("ko-KR", UiLanguage.Korean)]
    [InlineData("ru-RU", UiLanguage.Russian)]
    [InlineData("de-DE", UiLanguage.English)]
    public void SystemLanguageResolvesAllSupportedLanguages(string culture, object expected) =>
        Assert.Equal(expected, AppLocalization.ResolveLanguage(UiLanguage.System, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void EveryKeyHasMatchingFormatArguments()
    {
        foreach (var key in Enum.GetValues<TextKey>())
        {
            var formats = Enum.GetValues<UiLanguage>().Where(l => l != UiLanguage.System)
                .Select(l => CompositeFormat.Parse(AppLocalization.Template(key, l))).ToArray();
            Assert.All(formats, f => Assert.Equal(formats[0].MinimumArgumentCount, f.MinimumArgumentCount));
        }
    }

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese, "正在加载工作区…")]
    [InlineData(UiLanguage.English, "Loading workspace…")]
    [InlineData(UiLanguage.Korean, "작업 영역 로드 중…")]
    [InlineData(UiLanguage.Russian, "Загрузка рабочей области…")]
    public void ExistingMessagesCanRenderInAllFourLanguages(object language, string expected) =>
        Assert.Equal(expected, AppLocalization.Format(new UiMessage(TextKey.LoadingWorkspace), (UiLanguage)language, CultureInfo.InvariantCulture));

    [Fact]
    public void FormattingDoesNotTranslateLiteralSdkOutput()
    {
        var message = new UiMessage(TextKey.ErrorDetail, "NU1301: Example.Package E:\\private\\project.csproj");
        foreach (var language in Enum.GetValues<UiLanguage>())
            Assert.EndsWith("NU1301: Example.Package E:\\private\\project.csproj",
                AppLocalization.Format(message, language, CultureInfo.GetCultureInfo("en-US")));
    }
}
