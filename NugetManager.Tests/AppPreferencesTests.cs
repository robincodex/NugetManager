using System.Text.Json;
using NugetManager.Presentation;

namespace NugetManager.Tests;

public class AppPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NugetManager-preferences-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "preferences.json");

    [Theory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    [InlineData(UiLanguage.Russian)]
    public async Task PreferencesRoundTrip(object language)
    {
        var store = new AppPreferenceStore(FilePath);
        await store.SaveAsync(new(UiTheme.Dark, (UiLanguage)language), CancellationToken.None);
        Assert.Equal(new(UiTheme.Dark, (UiLanguage)language), await store.LoadAsync(CancellationToken.None));
        await store.SaveAsync(new(UiTheme.Light, UiLanguage.System), CancellationToken.None);
        Assert.Equal(new(UiTheme.Light, UiLanguage.System), await store.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MissingPreferencesUseSystemDefaults() =>
        Assert.Equal(new(UiTheme.System, UiLanguage.System), await new AppPreferenceStore(FilePath).LoadAsync(CancellationToken.None));

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"theme\":99,\"language\":0}")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task CorruptPreferencesAreReported(string contents)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, contents);
        await Assert.ThrowsAnyAsync<JsonException>(() => new AppPreferenceStore(FilePath).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedWritePreservesOriginalFile()
    {
        var store = new AppPreferenceStore(FilePath);
        await store.SaveAsync(new(UiTheme.Light, UiLanguage.English), CancellationToken.None);
        var before = await File.ReadAllBytesAsync(FilePath);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(new(UiTheme.Dark, UiLanguage.Korean), CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
