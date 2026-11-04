using static NugetManager.Presentation.Messages;
using Aprillz.MewUI;
using System.Globalization;
using System.Text.Json;
using NugetManager.Operations;
using NugetManager.Packages;
using NugetManager.Presentation;
using NugetManager.Sdk;
using NugetManager.Workspaces;

namespace NugetManager;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var preferenceStore = new AppPreferenceStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NugetManager", "preferences.json"));
        AppPreferences preferences;
        try { preferences = preferenceStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            preferences = new();
            NativeMessageBox.Show(AppLocalization.Format(new UiMessage(TextKey.PreferencesReadFailed, ex.Message),
                preferences.Language, CultureInfo.CurrentUICulture), "NuGet", NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Warning);
        }
        string? directory = null;
        if (args.Length > 1)
        {
            return Fail(M(TextKey.M217), preferences.Language);
        }
        if (args.Length == 1)
        {
            directory = Path.GetFullPath(args[0]);
            if (!Directory.Exists(directory))
            {
                return Fail(MF(TextKey.M218, $"工作目录不存在：{directory}"), preferences.Language);
            }
        }

        var recoveryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NugetManager", "recovery");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NugetManager/0.0.1");

        var sdk = new SdkClient(new ProcessRunner());
        var controller = new WorkspaceController(
            new WorkspaceDiscovery(),
            sdk,
            new PackageCatalog(sdk, new PopularPackageReader(http)),
            new PackageMetadataReader(http),
            new PackageOperations(sdk, recoveryDirectory));

        MainWindow? mainWindow = null;
        try
        {
            Application.DispatcherUnhandledException += e =>
            {
                e.Handled = true;
                var language = mainWindow?.Language ?? preferences.Language;
                NativeMessageBox.Show(AppLocalization.Format(M(TextKey.M219) + Environment.NewLine + UiMessage.FromException(e.Exception),
                    language, CultureInfo.CurrentUICulture),
                    AppLocalization.Format(M(TextKey.AppTitle), language, CultureInfo.CurrentUICulture),
                    NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Error);
            };
            var app = Application.Create().UseWin32().UseDirect2D();
            app.BuildMainWindow(() => mainWindow = MainWindow.Create(controller, directory, recoveryDirectory, preferences, preferenceStore)).Run();
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(M(TextKey.M220) + UiMessage.FromException(ex), mainWindow?.Language ?? preferences.Language);
        }
    }

    private static int Fail(UiMessage message, UiLanguage language)
    {
        try
        {
            NativeMessageBox.Show(AppLocalization.Format(message, language, CultureInfo.CurrentUICulture),
                AppLocalization.Format(M(TextKey.AppTitle), language, CultureInfo.CurrentUICulture), NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Error);
        }
        catch (Exception)
        {
            Console.Error.WriteLine(message);
        }
        return 1;
    }
}
