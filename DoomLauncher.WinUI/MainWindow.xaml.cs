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

    public MainWindow()
    {
        InitializeComponent();
        _app = (App)Application.Current;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarTheme();

        AppWindow.SetIcon("Assets/DoomLauncher.ico");
        RestoreWindowSize();
        AppWindow.Closing += AppWindow_Closing;
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
