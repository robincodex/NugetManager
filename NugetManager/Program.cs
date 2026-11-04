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
            return Fail("用法：NugetManager [工作目录]");
        }
        if (args.Length == 1)
        {
            directory = Path.GetFullPath(args[0]);
            if (!Directory.Exists(directory))
            {
                return Fail($"工作目录不存在：{directory}");
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

        try
        {
            Application.DispatcherUnhandledException += e =>
            {
                e.Handled = true;
                NativeMessageBox.Show("发生未处理的错误，当前操作可能未完成：" + Environment.NewLine + e.Exception.Message,
                    "NuGet 包管理器", NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Error);
            };
            var app = Application.Create().UseWin32().UseDirect2D();
            app.BuildMainWindow(() => MainWindow.Create(controller, directory, recoveryDirectory, preferences, preferenceStore)).Run();
            return 0;
        }
        catch (Exception ex)
        {
            return Fail("启动失败：" + ex.Message);
        }
    }

    private static int Fail(string message)
    {
        try
        {
            NativeMessageBox.Show(message, "NuGet 包管理器", NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Error);
        }
        catch (Exception)
        {
            Console.Error.WriteLine(message);
        }
        return 1;
    }
}
