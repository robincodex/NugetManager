using System.Text.Json;
using System.Text.Json.Serialization;

namespace NugetManager.Presentation;

internal enum UiLanguage { System, SimplifiedChinese, English, Korean, Russian }
internal enum UiTheme { System, Light, Dark }

internal sealed record AppPreferences(UiTheme Theme = UiTheme.System, UiLanguage Language = UiLanguage.System, bool QuickMode = false);

internal sealed class AppPreferenceStore(string path)
{
    public async Task<AppPreferences> LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var dto = await JsonSerializer.DeserializeAsync(file, PreferenceJsonContext.Default.PreferenceDto, ct).ConfigureAwait(false);
            if (dto?.Theme is not { } theme || dto.Language is not { } language
                || !Enum.IsDefined(theme) || !Enum.IsDefined(language))
                throw new JsonException("Invalid preference values.");
            return new(theme, language, dto.QuickMode);
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken ct)
    {
        if (!Enum.IsDefined(preferences.Theme) || !Enum.IsDefined(preferences.Language))
            throw new ArgumentOutOfRangeException(nameof(preferences));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(file, new PreferenceDto { Theme = preferences.Theme, Language = preferences.Language, QuickMode = preferences.QuickMode },
                    PreferenceJsonContext.Default.PreferenceDto, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed class PreferenceDto
{
    public UiTheme? Theme { get; set; }
    public UiLanguage? Language { get; set; }
    public bool QuickMode { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PreferenceDto))]
internal partial class PreferenceJsonContext : JsonSerializerContext;
