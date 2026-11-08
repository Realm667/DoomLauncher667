using DoomLauncher.Modern.Core.Launch;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task ShowMapsAsync()
    {
        var game = ViewModel.SelectedGame; if (game.IsPlaceholder) return;
        var path = await _app.NativeLibraryService.ResolveManagedGameFileAsync(game.GameFileId, _loadCancellation.Token);
        if (path is null || !File.Exists(path)) throw new FileNotFoundException(Strings["ArchiveMissing"], path);
        var doomGame = new[] { "doom", "plutonia", "tnt" }.Any(word => game.Iwad.Contains(word, StringComparison.OrdinalIgnoreCase));
        var maps = await RunProgressDialogAsync(Strings["MapsAndProgress"], game.Title, async progress =>
        { var result = await MapBrowserService.ReadAsync(path, doomGame, _loadCancellation.Token); progress.Report(100); return result; });
        if (maps.Count == 0) { await ShowActionMessageAsync(Strings["MapsAndProgress"], Strings["NoMaps"], Strings["Close"]); return; }
        var personal = (await _app.LibraryFeatures.LoadAsync(_loadCancellation.Token)).GetValueOrDefault(game.GameFileId) ?? PersonalModData.Empty(game.GameFileId);
        var completed = personal.CompletedMaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new ListView { Width = 275, Height = 440, SelectionMode = ListViewSelectionMode.Single };
        var preview = new Image { Stretch = Stretch.Uniform, Width = 1400, Height = 1000 };
        var showPreview = new CheckBox { Content = Strings["ShowMapPreview"] };
        var stats = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var imageScroll = new ScrollViewer { Content = preview, Width = 670, Height = 400, ZoomMode = ZoomMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinZoomFactor = .25f, MaxZoomFactor = 5, Visibility = Visibility.Collapsed };
        CancellationTokenSource? rendering = null; var temporaryFiles = new List<string>(); var pending = new HashSet<Task>();
        async Task SaveProgress(string name, bool done, CheckBox check)
        {
            check.IsEnabled = false;
            try
            {
                await _app.LibraryFeatures.SetMapFinishedAsync(game.GameFileId, name, done, _loadCancellation.Token);
                if (done) completed.Add(name); else completed.Remove(name);
                status.Text = _app.Localization.Format("MapProgressSummary", completed.Count, maps.Select(map => map.Name).Distinct().Count());
                await ViewModel.ReloadPersonalDataAsync(_loadCancellation.Token);
            }
            catch (Exception error) { check.IsChecked = !done; status.Text = error.Message; }
            finally { check.IsEnabled = true; }
        }
        foreach (var map in maps)
        {
            var check = new CheckBox { IsChecked = completed.Contains(map.Name), Content = map.Label, MaxWidth = 250 };
            check.Click += (_, _) => { var task = SaveProgress(map.Name, check.IsChecked == true, check); pending.Add(task); _ = task.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => pending.Remove(task))); };
            list.Items.Add(new ListViewItem { Content = check, Tag = map });
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
        var right = new StackPanel { Width = 670, Spacing = 8, Children = { stats, showPreview, imageScroll } };
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { list, right } };
        var content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = Strings["MapProgressNotice"], TextWrapping = TextWrapping.Wrap, MaxWidth = 950 }, columns, status } };
        var dialog = FeatureDialog(Strings["MapsAndProgress"], new ScrollViewer { Content = content, MaxHeight = 720 }, Strings["LaunchSelectedMap"]);
        dialog.SecondaryButtonText = Strings["LaunchNextMap"];
        dialog.Closing += (_, args) => { if (pending.Count > 0) args.Cancel = true; };
        list.SelectedIndex = 0; var result = await dialog.ShowAsync(); rendering?.Cancel();
        var selected = result == ContentDialogResult.Primary ? (list.SelectedItem as ListViewItem)?.Tag as MapDocument
            : result == ContentDialogResult.Secondary ? maps.FirstOrDefault(map => !completed.Contains(map.Name)) : null;
        if (selected is not null)
            await ViewModel.LaunchFeatureRequestAsync(new GameLaunchRequest(game.GameFileId, game.Title, Map: selected.Name), _loadCancellation.Token);
        foreach (var file in temporaryFiles) { try { File.Delete(file); } catch (IOException) { } }
    }
}
