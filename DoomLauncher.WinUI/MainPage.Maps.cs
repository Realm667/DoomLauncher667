using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private static ListViewItem CreateMapProgressItem(MapDocument map, bool completed, bool trackingAvailable = true) => new()
    {
        Content = new TextBlock { Text = (trackingAvailable ? (completed ? "✓ " : "○ ") : "") + map.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 }, Tag = map,
    };
    private async Task ShowMapsAsync()
    {
        var game = ViewModel.SelectedGame; if (game.IsPlaceholder) return;
        var trackingAvailable = ViewModel.CanTrackSelectedGame;
        var title = ViewModel.MapsActionText;
        var path = await _app.NativeLibraryService.ResolveManagedGameFileAsync(game.GameFileId, _loadCancellation.Token);
        if (path is null || !File.Exists(path)) throw new FileNotFoundException(Strings["ArchiveMissing"], path);
        var doomGame = new[] { "doom", "plutonia", "tnt" }.Any(word => game.Iwad.Contains(word, StringComparison.OrdinalIgnoreCase));
        var maps = await RunProgressDialogAsync(title, game.Title, async progress =>
        { var result = await MapBrowserService.ReadAsync(path, doomGame, _loadCancellation.Token); progress.Report(100); return result; });
        if (maps.Count == 0) { await ShowActionMessageAsync(title, Strings["NoMaps"], Strings["Close"]); return; }
        var personal = (await _app.LibraryFeatures.LoadAsync(_loadCancellation.Token)).GetValueOrDefault(game.GameFileId) ?? PersonalModData.Empty(game.GameFileId);
        var completed = personal.CompletedMaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var narrow = XamlRoot.Size.Width < 960;
        var list = new ListView { Height = narrow ? 160 : Math.Min(440, UiLayoutPolicy.DialogHeight(XamlRoot.Size.Height) - 100), SelectionMode = ListViewSelectionMode.Single };
        var preview = new Image { Stretch = Stretch.Uniform };
        var showPreview = new CheckBox { Content = Strings["ShowMapPreview"] };
        var stats = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var imageScroll = new ScrollViewer { Content = preview, Height = Math.Min(360, XamlRoot.Size.Height * .35), ZoomMode = ZoomMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinZoomFactor = .25f, MaxZoomFactor = 5, Visibility = Visibility.Collapsed };
        CancellationTokenSource? rendering = null; var temporaryFiles = new List<string>();
        foreach (var map in maps)
        {
            list.Items.Add(CreateMapProgressItem(map, completed.Contains(map.Name), trackingAvailable));
        }
        async Task ShowSelection()
        {
            rendering?.Cancel(); rendering?.Dispose(); rendering = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
            var token = rendering.Token;
            if (list.SelectedItem is not ListViewItem { Tag: MapDocument map }) return;
            stats.Text = $"{map.Label} · {map.Format}\n{map.ArchiveEntry}\n{Strings["MapGeometry"]}: {map.Vertices.Count:N0} / {map.Lines.Count:N0} / {map.Sectors:N0}\n{Strings["MapObjects"]}: {map.Things:N0} · {Strings["MapMonsters"]}: {map.StandardMonsters?.ToString() ?? "—"} · {Strings["MapItems"]}: {map.StandardItems?.ToString() ?? "—"} · {Strings["MapSecrets"]}: {map.Secrets?.ToString() ?? "—"}\n{Strings["MapCountsNotice"]}";
            imageScroll.Visibility = showPreview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (showPreview.IsChecked != true) return;
            try
            {
                var file = await MapBrowserService.RenderPreviewAsync(map, Path.Combine(GetPortableRoot(), "Data", "Temp", "MapPreviews"), token);
                temporaryFiles.Add(file); token.ThrowIfCancellationRequested(); preview.Source = new BitmapImage(new Uri(file));
                imageScroll.ChangeView(0, 0, .45f, true);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { status.Text = error.Message; }
        }
        list.SelectionChanged += async (_, _) => await ShowSelection(); showPreview.Click += async (_, _) => await ShowSelection();
        var right = new StackPanel { Spacing = 8, Children = { stats, showPreview, imageScroll } };
        var columns = new Grid { Width = UiLayoutPolicy.DialogWidth(XamlRoot.Size.Width, 1000), ColumnSpacing = 16, RowSpacing = 12 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(narrow ? 1 : .3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = narrow ? new GridLength(0) : new GridLength(.7, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, narrow ? 0 : 1); Grid.SetRow(right, narrow ? 1 : 0);
        columns.Children.Add(list); columns.Children.Add(right);
        var content = new StackPanel { Spacing = 10 };
        if (trackingAvailable) content.Children.Add(new TextBlock { Text = Strings["MapProgressNotice"], TextWrapping = TextWrapping.Wrap, MaxWidth = 950 });
        content.Children.Add(columns); content.Children.Add(status);
        var dialog = FeatureDialog(title, new ScrollViewer { Content = content, MaxHeight = 720 }, Strings["LaunchSelectedMap"]);
        if (trackingAvailable) { dialog.SecondaryButtonText = Strings["LaunchNextMap"]; dialog.IsSecondaryButtonEnabled = maps.Any(map => !completed.Contains(map.Name)); }
        list.SelectedIndex = 0; var result = await dialog.ShowAsync(); rendering?.Cancel();
        var selected = result == ContentDialogResult.Primary ? (list.SelectedItem as ListViewItem)?.Tag as MapDocument
            : result == ContentDialogResult.Secondary ? maps.FirstOrDefault(map => !completed.Contains(map.Name)) : null;
        if (selected is not null)
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(game.GameFileId, game.Title, Map: selected.Name), _loadCancellation.Token);
        foreach (var file in temporaryFiles) { try { File.Delete(file); } catch (IOException) { } }
    }
}
