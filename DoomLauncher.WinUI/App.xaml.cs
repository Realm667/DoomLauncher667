using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI;

public partial class App : Application
{
    private Window? _window;
    private SplashWindow? _splashWindow;

    public App()
    {
        IsDebugMode = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => argument.Equals(
                "--debug",
                StringComparison.OrdinalIgnoreCase));
        UnhandledException += App_UnhandledException;
        InitializeComponent();

        var databaseLocator = new DoomLauncherDatabaseLocator();
        Localization = new UiLocalization();
        LibraryCatalog = new SqliteLibraryCatalog(databaseLocator, Localization);
        LaunchOptionsCatalog = new SqliteLaunchOptionsCatalog(databaseLocator, Localization);
        LaunchService = new NativeGameLaunchService(
            databaseLocator,
            new SystemProcessStarter());
        NativeLibraryService = new SqliteNativeLibraryService(databaseLocator);
        MigrationService = new LegacyInstallationMigrationService(databaseLocator);
        FirstSetupService = new FirstSetupService(
            databaseLocator,
            NativeLibraryService,
            Localization);
        IdGamesService = new IdGamesService(databaseLocator);
        UserLibraryStateStore = new JsonUserLibraryStateStore();
    }

    internal ILibraryCatalog LibraryCatalog { get; }
    internal ILaunchOptionsCatalog LaunchOptionsCatalog { get; }
    internal ILaunchService LaunchService { get; }
    internal INativeLibraryService NativeLibraryService { get; }
    internal ILegacyInstallationMigrationService MigrationService { get; }
    internal IFirstSetupService FirstSetupService { get; }
    internal IIdGamesService IdGamesService { get; }
    internal IUserLibraryStateStore UserLibraryStateStore { get; }
    internal UiLocalization Localization { get; }
    internal bool IsDebugMode { get; }
    internal UserLibraryState InitialUserState { get; private set; } =
        UserLibraryState.Empty;
    internal Window? MainWindow => _window;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _splashWindow = new SplashWindow();
            _splashWindow.Activate();

            var minimumSplashTime = Task.Delay(1200);
            var stateLoad = UserLibraryStateStore.LoadAsync(
                CancellationToken.None);
            await Task.WhenAll(minimumSplashTime, stateLoad);
            InitialUserState = await stateLoad;

            _window = new MainWindow();
            _window.Activate();
            _splashWindow.Close();
            _splashWindow = null;
        }
        catch (Exception exception)
        {
            _splashWindow?.Close();
            _splashWindow = null;
            WriteDiagnostic(exception);
            throw;
        }
    }

    private static void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteDiagnostic(args.Exception);
    }

    private static void WriteDiagnostic(Exception exception)
    {
        try
        {
            var configuredPath = Environment.GetEnvironmentVariable(
                "DOOMLAUNCHER_DIAGNOSTIC_LOG");
            var logPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DoomLauncher.WinUI",
                    "Logs",
                    "crash.log")
                : Path.GetFullPath(configuredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Preserve the original exception when diagnostics cannot be written.
        }
    }

}
