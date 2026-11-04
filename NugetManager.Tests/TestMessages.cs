using System.Globalization;
using NugetManager.Presentation;

namespace NugetManager.Tests;

internal static class TestMessages
{
    public static string? Zh(UiMessage? message) => message is null ? null
        : AppLocalization.Format(message, UiLanguage.SimplifiedChinese, CultureInfo.InvariantCulture);
}
