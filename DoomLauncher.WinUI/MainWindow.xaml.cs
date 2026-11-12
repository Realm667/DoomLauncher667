using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using DoomLauncher.WinUI.Services;

namespace DoomLauncher.WinUI;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1440;
    private const int DefaultWindowHeight = 900;
    private const int MinimumWindowWidth = 800;
    private const int MinimumWindowHeight = 600;
    private readonly App _app;
    private bool _allowClose;
    private ulong _lastDisplay;

    public MainWindow()
    {
        InitializeComponent();
        _app = (App)Application.Current;

        AppTitleBar.Subtitle = AppVersion.Current;
        Title = $"Doom Launcher 667 {AppVersion.Current}";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarTheme();

        // SetIcon requires an absolute file path, not a working-directory or
        // package-relative path. The portable app can start from any directory.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "DoomLauncher.ico"));
        RestoreWindowSize();
        _lastDisplay=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest)?.DisplayId.Value ?? 0;
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += (_, change) =>
        {
            if (!change.DidSizeChange && !change.DidPositionChange) return;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area is null) return;
            var size = AppWindow.Size;
            var minWidth = Math.Min(MinimumWindowWidth, area.WorkArea.Width);
            var minHeight = Math.Min(MinimumWindowHeight, area.WorkArea.Height);
            var changedMonitor=_lastDisplay!=area.DisplayId.Value;
            _lastDisplay=area.DisplayId.Value;
            if(changedMonitor && (size.Width>area.WorkArea.Width || size.Height>area.WorkArea.Height)
                && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                var width=Math.Clamp(size.Width,minWidth,area.WorkArea.Width);
                var height=Math.Clamp(size.Height,minHeight,area.WorkArea.Height);
                var x=Math.Clamp(AppWindow.Position.X,area.WorkArea.X,area.WorkArea.X+area.WorkArea.Width-width);
                var y=Math.Clamp(AppWindow.Position.Y,area.WorkArea.Y,area.WorkArea.Y+area.WorkArea.Height-height);
                AppWindow.MoveAndResize(new RectInt32(x,y,width,height));
                return;
            }
            if (size.Width < minWidth || size.Height < minHeight)
                AppWindow.Resize(new SizeInt32(Math.Max(size.Width,minWidth),Math.Max(size.Height,minHeight)));
        };
        RootFrame.Navigate(typeof(MainPage));
    }

    private void RestoreWindowSize()
    {
        var width = _app.IsDebugMode
            ? DefaultWindowWidth
            : _app.InitialUserState.WindowWidth ?? DefaultWindowWidth;
        var height = _app.IsDebugMode
            ? DefaultWindowHeight
            : _app.InitialUserState.WindowHeight ?? DefaultWindowHeight;

        var displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);
        if (displayArea is not null)
        {
            width = Math.Clamp(
                width,
                Math.Min(MinimumWindowWidth, displayArea.WorkArea.Width),
                displayArea.WorkArea.Width);
            height = Math.Clamp(
                height,
                Math.Min(MinimumWindowHeight, displayArea.WorkArea.Height),
                displayArea.WorkArea.Height);
        }

        AppWindow.Resize(new SizeInt32(width, height));
    }

    private async void AppWindow_Closing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_allowClose || _app.IsDebugMode)
            return;

        args.Cancel = true;
        try
        {
            var latestState = await _app.UserLibraryStateStore.LoadAsync(
                CancellationToken.None);
            await _app.UserLibraryStateStore.SaveAsync(
                latestState with
                {
                    WindowWidth = sender.Size.Width,
                    WindowHeight = sender.Size.Height,
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Window size could not be saved: {exception}");
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    internal void ApplyTitleBarTheme()
    {
        if (Application.Current.Resources["DoomSidebarBrush"]
            is not SolidColorBrush sidebarBrush)
        {
            return;
        }

        var background = sidebarBrush.Color;
        var foreground = UsesDarkForeground(background)
            ? Colors.Black
            : Colors.White;
        var hoverBackground = Blend(background, foreground, 0.12);
        var pressedBackground = Blend(background, foreground, 0.20);

        AppTitleBar.RequestedTheme = UsesDarkForeground(background)
            ? ElementTheme.Light
            : ElementTheme.Dark;
        AppTitleBar.Background = sidebarBrush;
        AppTitleBar.Foreground = new SolidColorBrush(foreground);

        var titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static bool UsesDarkForeground(Color color) =>
        (color.R * 299 + color.G * 587 + color.B * 114) / 1000 >= 160;

    private static Color Blend(Color background, Color foreground, double amount) =>
        Color.FromArgb(
            255,
            checked((byte)Math.Round(background.R + (foreground.R - background.R) * amount)),
            checked((byte)Math.Round(background.G + (foreground.G - background.G) * amount)),
            checked((byte)Math.Round(background.B + (foreground.B - background.B) * amount)));
}
