using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace DoomLauncher.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarTheme();

        AppWindow.SetIcon("Assets/DoomLauncher.ico");
        AppWindow.Resize(new SizeInt32(1440, 900));
        RootFrame.Navigate(typeof(MainPage));
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
