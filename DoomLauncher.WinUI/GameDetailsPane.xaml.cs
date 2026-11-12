using DoomLauncher.WinUI.ViewModels;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoomLauncher.WinUI;

public sealed partial class GameDetailsPane : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MainViewModel),
            typeof(GameDetailsPane),
            new PropertyMetadata(null));

    public GameDetailsPane()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            ArtworkFrame.Height = Math.Clamp(ActualHeight * .25, 100, 200);
            LaunchOptionsContent.Width = Math.Max(220, Math.Min(300, (XamlRoot?.Size.Width ?? 420) - 100));
        };
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void Artwork_ImageFailed(object sender, ExceptionRoutedEventArgs args)
    {
        if (sender is not Image image) return;
        const string fallback = "ms-appx:///Assets/Library/grayscale/doom2.png";
        if (image.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap && bitmap.UriSource?.ToString() == fallback) return;
        image.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(fallback));
        ToolTipService.SetToolTip(image, DataContext is UiText text ? text["ArtworkUnavailable"] : "Artwork unavailable");
    }

    public event RoutedEventHandler? PlayRequested;
    public event RoutedEventHandler? LaunchOptionsRequested;
    public event RoutedEventHandler? LaunchWithOptionsRequested;
    public event RoutedEventHandler? FavoriteRequested;
    public event RoutedEventHandler? FinishedRequested;
    public event RoutedEventHandler? EditRequested;
    public event RoutedEventHandler? RescrapeRequested;
    public event RoutedEventHandler? ManageCollectionsRequested;
    public event RoutedEventHandler? ReadMoreRequested;
    public event RoutedEventHandler? QueueRequested;
    public event RoutedEventHandler? MapsRequested;
    public event RoutedEventHandler? SavesRequested;
    public event RoutedEventHandler? HistoryRequested;

    private async void PersonalRating_Changed(RatingControl sender, object args)
    {
        if (ViewModel is null || ViewModel.SelectedGame.IsPlaceholder) return;
        int? value = sender.Value < 1 ? null : Math.Clamp((int)sender.Value, 1, 5);
        if (ViewModel.SelectedPersonalData.Rating == value) return;
        try { await ViewModel.SetPersonalRatingAsync(ViewModel.SelectedGame.GameFileId, value); }
        catch (Exception error)
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Doom Launcher 667", Content = error.Message,
                CloseButtonText = DataContext is UiText text ? text["Close"] : "Close", RequestedTheme = ActualTheme };
            await dialog.ShowAsync();
        }
    }
    private void QueueButton_Click(object sender, RoutedEventArgs args) => QueueRequested?.Invoke(this, args);
    private void MapsButton_Click(object sender, RoutedEventArgs args) => MapsRequested?.Invoke(this, args);
    private void SavesButton_Click(object sender, RoutedEventArgs args) => SavesRequested?.Invoke(this, args);
    private void HistoryButton_Click(object sender, RoutedEventArgs args) => HistoryRequested?.Invoke(this, args);

    public void CloseLaunchOptions() => LaunchOptionsFlyout.Hide();

    private void ChooseLaunchDefinition_Click(object sender, RoutedEventArgs args)
    {
        LaunchOptionsRequested?.Invoke(this, args);
        LaunchOptionsFlyout.ShowAt((FrameworkElement)sender);
    }

    private async void SaveLaunchChoices_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel is null) return;
        var button = (Button)sender; button.IsEnabled = false;
        try { await ViewModel.SaveLaunchDefinitionsAsync(); LaunchOptionsFlyout.Hide(); }
        catch (Exception error)
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Doom Launcher 667", Content = error.Message,
                CloseButtonText = DataContext is UiText text ? text["Close"] : "Close", RequestedTheme = ActualTheme };
            await dialog.ShowAsync();
        }
        finally { button.IsEnabled = true; }
    }

    private void PreviousImageButton_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel?.SelectedGame.ShowPreviousImage();

    private void NextImageButton_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel?.SelectedGame.ShowNextImage();

    private void PlayButton_Click(object sender, RoutedEventArgs args) =>
        PlayRequested?.Invoke(this, args);

    private void LaunchOptionsButton_Click(
        object sender,
        RoutedEventArgs args) =>
        LaunchOptionsRequested?.Invoke(this, args);

    private void LaunchWithOptionsButton_Click(
        object sender,
        RoutedEventArgs args) =>
        LaunchWithOptionsRequested?.Invoke(this, args);

    private void FavoriteButton_Click(object sender, RoutedEventArgs args) =>
        FavoriteRequested?.Invoke(this, args);

    private void FinishedButton_Click(object sender, RoutedEventArgs args) =>
        FinishedRequested?.Invoke(this, args);

    private void EditButton_Click(object sender, RoutedEventArgs args) =>
        EditRequested?.Invoke(this, args);

    private void RescrapeIdGamesButton_Click(
        object sender,
        RoutedEventArgs args) =>
        RescrapeRequested?.Invoke(this, args);

    private void ManageCollectionsButton_Click(
        object sender,
        RoutedEventArgs args) =>
        ManageCollectionsRequested?.Invoke(this, args);

    private void ReadMoreButton_Click(object sender, RoutedEventArgs args) =>
        ReadMoreRequested?.Invoke(this, args);
}
