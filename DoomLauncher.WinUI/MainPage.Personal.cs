using System.Collections.ObjectModel;
using System.Text.Json;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private async Task FeatureActionAsync(Func<Task> action)
    {
        if (_isImporting || _isRescraping || ViewModel.IsLoading || ViewModel.IsGameRunning)
        { await ShowActionMessageAsync(Strings["LibraryTools"], Strings["CloseGamesFirst"], Strings["Close"]); return; }
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { await ShowErrorAsync(Strings["LibraryTools"], error.Message); }
    }
    private ContentDialog FeatureDialog(string title, object content, string? primary = null)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content,
            CloseButtonText = Strings["Close"], PrimaryButtonText = primary ?? string.Empty, DefaultButton = ContentDialogButton.Close };
        ApplyDialogTheme(dialog); dialog.Resources["ContentDialogMinWidth"] = 650d;
        dialog.Resources["ContentDialogMaxWidth"] = 1100d; return dialog;
    }
    private void LibraryTools_Opening(object sender, object args)
    { foreach (var item in ((MenuFlyout)sender).Items.OfType<MenuFlyoutItem>()) item.Text = Strings[item.Tag.ToString()!]; }
    private async void LibraryTool_Click(object sender, RoutedEventArgs args) => await FeatureActionAsync(() => ((FrameworkElement)sender).Tag?.ToString() switch
    { "RandomMod" => ShowRandomModAsync(), "BulkEdit" => ShowBulkEditAsync(), "PlayLater" => ShowQueueAsync(), _ => Task.CompletedTask });
    private async void PersonalQueue_Click(object sender, RoutedEventArgs args) => await FeatureActionAsync(() => ViewModel.ToggleQueueAsync(_loadCancellation.Token));
    private async void MetadataHistory_Click(object sender, RoutedEventArgs args) => await FeatureActionAsync(ShowMetadataHistoryAsync);
    private async void MapsProgress_Click(object sender, RoutedEventArgs args) => await FeatureActionAsync(ShowMapsAsync);
    private async void Saves_Click(object sender, RoutedEventArgs args) => await FeatureActionAsync(ShowSavesAsync);

    private void RevealLibraryGame(int id)
    {
        var navigation = ShellNavigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag?.ToString() == "Library");
        ShellNavigation.SelectedItem = navigation; ViewModel.SetSection(LibrarySection.Library);
        ViewModel.Filter(string.Empty); ViewModel.SetCategoryFilter(LibraryCategoryFilter.All); SearchBox.Text = string.Empty;
        ViewModel.SelectGame(id); SyncSelection();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        { GameList.ScrollIntoView(ViewModel.SelectedGame); GameGrid.ScrollIntoView(ViewModel.SelectedGame); });
    }

    private async Task ShowRandomModAsync()
    {
        var onlyUnplayed = new CheckBox { Content = Strings["Unplayed"] };
        var maxMaps = new NumberBox { Header = Strings["MaximumMaps"], Minimum = 0, Maximum = 99999, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var rating = new NumberBox { Header = Strings["MinimumRating"], Minimum = 0, Maximum = 5, Value = 0, SmallChange = 1 };
        ComboBox FilterBox(string header, IEnumerable<string> values) => new() { Header = header, ItemsSource = new[] { Strings["All"] }.Concat(values).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var iwads = FilterBox(Strings["IWAD"], ViewModel.CatalogItems.Where(item => item.Category == "Mod").Select(item => item.Iwad).Distinct().Order());
        var collections = FilterBox(Strings["Collections"], ViewModel.CatalogItems.SelectMany(item => item.Tags).Distinct().Order());
        var ports = FilterBox(Strings["SourcePort"], ViewModel.CatalogItems.Select(item => item.SourcePort).Distinct().Order());
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 20 };
        var pick = new Button { Content = Strings["RandomMod"] }; LibraryItem? chosen = null;
        pick.Click += (_, _) =>
        {
            var candidates = ViewModel.CatalogItems.Where(item => item.Category == "Mod"
                && (!onlyUnplayed.IsChecked.GetValueOrDefault() || item.MinutesPlayed == 0)
                && (maxMaps.Value <= 0 || item.MapCount > 0 && item.MapCount <= maxMaps.Value)
                && (rating.Value <= 0 || item.PersonalRating >= rating.Value)
                && (iwads.SelectedIndex == 0 || item.Iwad == iwads.SelectedItem?.ToString())
                && (ports.SelectedIndex == 0 || item.SourcePort == ports.SelectedItem?.ToString())
                && (collections.SelectedIndex == 0 || item.Tags.Contains(collections.SelectedItem?.ToString() ?? ""))).ToArray();
            var next = FeaturedModSelector.Choose([], candidates.Select(item => item.GameFileId).ToArray(), chosen?.GameFileId, true);
            chosen = candidates.FirstOrDefault(item => item.GameFileId == next);
            result.Text = chosen is null ? Strings["NoMatchingMods"] : $"{chosen.Title}\n{chosen.Author} · {chosen.Maps} maps";
        };
        var panel = new StackPanel { Spacing = 12, Width = 550 };
        foreach (var control in new UIElement[] { onlyUnplayed, maxMaps, rating, iwads, ports, collections, pick, result }) panel.Children.Add(control);
        var dialog = FeatureDialog(Strings["RandomMod"], panel, Strings["ShowInLibrary"]);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && chosen is not null) RevealLibraryGame(chosen.GameFileId);
    }

    private async Task ShowQueueAsync()
    {
        var data = await _app.LibraryFeatures.LoadAsync(_loadCancellation.Token);
        var items = new ObservableCollection<LibraryItem>(ViewModel.CatalogItems.Where(item => data.GetValueOrDefault(item.GameFileId)?.QueuePosition.HasValue == true)
            .OrderBy(item => data[item.GameFileId].QueuePosition));
        var list = new ListView { ItemsSource = items, DisplayMemberPath = nameof(LibraryItem.Title), Height = 340, SelectionMode = ListViewSelectionMode.Single };
        var auto = new CheckBox { Content = Strings["AutoDequeue"], IsEnabled = false };
        var overrides = data.ToDictionary(pair => pair.Key, pair => pair.Value.AutoDequeue);
        list.SelectionChanged += (_, _) => { auto.IsEnabled = list.SelectedItem is LibraryItem; auto.IsChecked = list.SelectedItem is LibraryItem item && overrides[item.GameFileId]; };
        var up = new Button { Content = Strings["MoveUp"] }; var down = new Button { Content = Strings["MoveDown"] };
        up.Click += (_, _) => { var index = list.SelectedIndex; if (index > 0) { items.Move(index, index - 1); list.SelectedIndex = index - 1; } };
        down.Click += (_, _) => { var index = list.SelectedIndex; if (index >= 0 && index < items.Count - 1) { items.Move(index, index + 1); list.SelectedIndex = index + 1; } };
        auto.Click += (_, _) => { if (list.SelectedItem is LibraryItem item) overrides[item.GameFileId] = auto.IsChecked == true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { up, down } };
        var panel = new StackPanel { Width = 580, Spacing = 12, Children = { list, buttons, auto } };
        var dialog = FeatureDialog(Strings["PlayLater"], panel, Strings["Save"]);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _app.LibraryFeatures.ReorderQueueAsync(items.Select(item => item.GameFileId).ToArray(), _loadCancellation.Token);
        foreach (var item in items) if (overrides[item.GameFileId] != data[item.GameFileId].AutoDequeue)
            await _app.LibraryFeatures.SetQueuedAsync(item.GameFileId, true, overrides[item.GameFileId], _loadCancellation.Token);
        await ViewModel.ReloadPersonalDataAsync(_loadCancellation.Token);
    }

    private async Task ShowBulkEditAsync()
    {
        var all = ViewModel.CatalogItems.Where(item => item.Category == "Mod").OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var selected = new HashSet<int>(); var filter = new TextBox { PlaceholderText = Strings["Filter"] };
        var list = new ListView { ItemsSource = all, DisplayMemberPath = nameof(LibraryItem.Title), Height = 340, SelectionMode = ListViewSelectionMode.Multiple };
        var replacing = false;
        list.SelectionChanged += (_, args) => { if (replacing) return; foreach (var item in args.AddedItems.OfType<LibraryItem>()) selected.Add(item.GameFileId); foreach (var item in args.RemovedItems.OfType<LibraryItem>()) selected.Remove(item.GameFileId); };
        filter.TextChanged += (_, _) =>
        {
            replacing = true; list.ItemsSource = all.Where(item => item.Title.Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            foreach (var item in list.Items.OfType<LibraryItem>().Where(item => selected.Contains(item.GameFileId))) list.SelectedItems.Add(item); replacing = false;
        };
        var selectAll = new Button { Content = Strings["SelectAllVisible"] }; selectAll.Click += (_, _) => list.SelectAll();
        var choose = FeatureDialog(Strings["BulkEdit"], new StackPanel { Width = 600, Spacing = 10, Children = { filter, selectAll, list } }, Strings["Next"]);
        if (await choose.ShowAsync() != ContentDialogResult.Primary || selected.Count == 0) return;
        var settings = await ViewModel.LoadNativeSettingsAsync(_loadCancellation.Token);
        var source = new ComboBox { Header = Strings["SourcePort"], ItemsSource = new[] { new NativeChoice(-1, Strings["KeepExisting"]), new(null, Strings["Automatic"]) }.Concat(settings.SourcePorts.Where(item => item.Id.HasValue)).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var iwad = new ComboBox { Header = Strings["IWAD"], ItemsSource = new[] { new NativeChoice(-1, Strings["KeepExisting"]), new(null, Strings["Automatic"]) }.Concat(settings.Iwads.Where(item => item.Id.HasValue)).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var finished = new ComboBox { Header = Strings["Finished"], ItemsSource = new[] { Strings["KeepExisting"], Strings["Finished"], Strings["NotFinished"] }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var tags = (await _app.NativeLibraryService.LoadGameCollectionsAsync(selected.First(), _loadCancellation.Token)).Collections;
        var add = new ListView { Header = Strings["AddCollections"], ItemsSource = tags, DisplayMemberPath = "Name", SelectionMode = ListViewSelectionMode.Multiple, Height = 140 };
        var remove = new ListView { Header = Strings["RemoveCollections"], ItemsSource = tags, DisplayMemberPath = "Name", SelectionMode = ListViewSelectionMode.Multiple, Height = 140 };
        var summary = new TextBlock { Text = string.Format(Strings["BulkEditCount"], selected.Count), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Width = 600, Spacing = 10, Children = { summary, source, iwad, finished, add, remove } };
        var options = FeatureDialog(Strings["BulkEdit"], new ScrollViewer { Content = panel, MaxHeight = 650 }, Strings["Next"]);
        if (await options.ShowAsync() != ContentDialogResult.Primary) return;
        var portChoice = (NativeChoice)source.SelectedItem; var iwadChoice = (NativeChoice)iwad.SelectedItem;
        var edit = new BulkLibraryEdit(portChoice.Id, portChoice.Id != -1, iwadChoice.Id, iwadChoice.Id != -1,
            finished.SelectedIndex == 0 ? null : finished.SelectedIndex == 1,
            add.SelectedItems.OfType<NativeTag>().Select(item => item.TagId).ToHashSet(), remove.SelectedItems.OfType<NativeTag>().Select(item => item.TagId).ToHashSet());
        var preview = $"{summary.Text}\n\n{Strings["SourcePort"]}: {portChoice.Name}\n{Strings["IWAD"]}: {iwadChoice.Name}\n{Strings["Finished"]}: {finished.SelectedItem}\n+ {string.Join(", ", add.SelectedItems.OfType<NativeTag>().Select(item => item.Name))}\n− {string.Join(", ", remove.SelectedItems.OfType<NativeTag>().Select(item => item.Name))}";
        if (await FeatureDialog(Strings["ConfirmChanges"], new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap }, Strings["Apply"]).ShowAsync() != ContentDialogResult.Primary) return;
        await _app.LibraryFeatures.ApplyBulkEditAsync(selected, edit, _loadCancellation.Token); await ViewModel.RefreshAsync(_loadCancellation.Token); SyncSelection();
    }

    private async Task ShowMetadataHistoryAsync()
    {
        if (ViewModel.SelectedGame.IsPlaceholder) return;
        var id = ViewModel.SelectedGame.GameFileId; var locks = await _app.LibraryFeatures.LoadLocksAsync(id, _loadCancellation.Token);
        var history = await _app.LibraryFeatures.LoadHistoryAsync(id, _loadCancellation.Token);
        var panel = new StackPanel { Width = 690, Spacing = 8 }; var checks = new Dictionary<string, CheckBox>();
        panel.Children.Add(new TextBlock { Text = Strings["LockField"], TextWrapping = TextWrapping.Wrap });
        foreach (var field in LibraryFeatureStore.MetadataFields)
        { var check = new CheckBox { Content = Strings[field], IsChecked = locks.Contains(field) }; checks[field] = check; panel.Children.Add(check); }
        var list = new ListView { ItemsSource = history.Select(change => new HistoryRow(change, $"{change.AtUtc} · {change.Source}" + (change.Undone ? " · ↶" : ""))).ToArray(), DisplayMemberPath = "Label", Height = 150 };
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 200 };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is HistoryRow row) details.Text = $"{Strings["Before"]}\n{JsonSerializer.Serialize(row.Change.Before, new JsonSerializerOptions { WriteIndented = true })}\n\n{Strings["After"]}\n{JsonSerializer.Serialize(row.Change.After, new JsonSerializerOptions { WriteIndented = true })}"; };
        panel.Children.Add(list); panel.Children.Add(details);
        var dialog = FeatureDialog(Strings["MetadataHistory"], new ScrollViewer { Content = panel, MaxHeight = 700 }, Strings["Save"]);
        dialog.SecondaryButtonText = Strings["UndoChange"];
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await _app.LibraryFeatures.SetLocksAsync(id, checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToHashSet(), _loadCancellation.Token);
        else if (result == ContentDialogResult.Secondary && list.SelectedItem is HistoryRow row)
        { await _app.LibraryFeatures.UndoMetadataAsync(row.Change.Id, _loadCancellation.Token); await ViewModel.RefreshAsync(_loadCancellation.Token); SyncSelection(); }
    }
    private sealed record HistoryRow(MetadataChange Change, string Label);
}
