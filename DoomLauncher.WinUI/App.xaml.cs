using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI;

public partial class App : Application
{
    private Window? _window;
    private SplashWindow? _splashWindow;
    private InstallationLease? _installationLease;

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
        ObsidianService = new ObsidianService(
            databaseLocator,
            NativeLibraryService);
        MigrationService = new LegacyInstallationMigrationService(databaseLocator);
        FirstSetupService = new FirstSetupService(
            databaseLocator,
            NativeLibraryService,
            Localization,
            ObsidianService);
        IdGamesService = new IdGamesService(databaseLocator);
        UserLibraryStateStore = new JsonUserLibraryStateStore();
    }

    internal ILibraryCatalog LibraryCatalog { get; }
    internal ILaunchOptionsCatalog LaunchOptionsCatalog { get; }
    internal ILaunchService LaunchService { get; }
    internal INativeLibraryService NativeLibraryService { get; }
    internal IObsidianService ObsidianService { get; }
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
            var databasePath = Environment.GetEnvironmentVariable(DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                try { databasePath = new DoomLauncherDatabaseLocator().FindDatabase(); }
                catch (FileNotFoundException)
                {
                    var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
                    databasePath = Path.Combine(baseDirectory.Name.Equals("WinUI", StringComparison.OrdinalIgnoreCase)
                        ? baseDirectory.Parent!.FullName : baseDirectory.FullName, DoomLauncherDatabaseLocator.DatabaseFileName);
                }
            }
            _installationLease = await Task.Run(() => InstallationLease.Acquire(databasePath));

            var stateLoad = UserLibraryStateStore.LoadAsync(
                CancellationToken.None);
            InitialUserState = await stateLoad;
            TileImageDefaults.EnsurePortableCopies();

            _window = new MainWindow();
            _window.Activate();
            _splashWindow.Close();
            _splashWindow = null;
        }
        catch (Exception exception)
        {
            _ = Task.Run(() => WriteDiagnostic(exception));
            _installationLease?.Dispose();
            _installationLease = null;
            _window = new Window { Title = "Doom Launcher 667" };
            var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 16, Padding = new Thickness(32) };
            panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap });
            var close = new Microsoft.UI.Xaml.Controls.Button { Content = "Close" };
            close.Click += (_, _) => _window.Close();
            panel.Children.Add(close);
            _window.Content = panel;
            _window.Activate();
            _splashWindow?.Close();
            _splashWindow = null;
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
