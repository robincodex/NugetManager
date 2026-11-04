using System.Globalization;
using System.Text;
using System.Text.Json;
using NugetManager.Sdk;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class AppLocalizationTests
{
    [Theory]
    [InlineData("""{"key":"ErrorDetail","arguments":[]}""")]
    [InlineData("""{"key":"Join","arguments":[]}""")]
    [InlineData("""{"key":"Ready","arguments":[null]}""")]
    public void CorruptPersistedMessagesAreRejectedAtTheReadBoundary(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SdkJsonContext.Default.UiMessage));

    [Fact]
    public async Task RecoveryMessagesKeepNestedIdentityAcrossRestart()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var file = ws.Write("before.txt", "original");
        var message = UiMessage.FromFormat(TextKey.M183, $"升级 {"Example.Json"} 到 {"3.0.0"}（{new UiMessage(TextKey.M109)}）");
        var transaction = await NugetManager.Operations.FileTransaction.BeginAsync([file], ws.Path_("recovery"), CancellationToken.None, description: message);
        var pending = Assert.Single(await NugetManager.Operations.RecoveryCoordinator.FindAsync(ws.Path_("recovery"), CancellationToken.None));
        Assert.Equal("Upgrade Example.Json to 3.0.0 (Projects)",
            AppLocalization.Format(pending.Description!, UiLanguage.English, CultureInfo.InvariantCulture));
        Assert.Contains("프로젝트", AppLocalization.Format(pending.Description!, UiLanguage.Korean, CultureInfo.InvariantCulture));
        Assert.Contains("Проекты", AppLocalization.Format(pending.Description!, UiLanguage.Russian, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ControllerRetainsMessageIdentityWhenLanguageChanges()
    {
        await using var ws = await TestWorkspace.CreateAsync();
        var controller = new WorkspaceController(new FakeDiscovery(new(ws.Root, null, [])), new FakeSdk(),
            new FakeCatalog(), new FakeMetadata(), new FakeOperations());
        await controller.OpenAsync(ws.Root, null, CancellationToken.None);
        var message = controller.State.StatusMessage!;
        Assert.Equal(TextKey.Ready, message.Key);
        Assert.Equal("준비됨", AppLocalization.Format(message, UiLanguage.Korean, CultureInfo.InvariantCulture));
        Assert.Equal("Готово", AppLocalization.Format(message, UiLanguage.Russian, CultureInfo.InvariantCulture));
    }

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
