using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private void CollectionsViewModeButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_selectedCollectionGroup is null)
            _showCollectionList = !_showCollectionList;
        else
            _showCollectionDetailList = !_showCollectionDetailList;

        UpdateCollectionsViewControls();
    }

    private async void CollectionAccordionHeader_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not Button button || button.Parent is not Grid container)
            return;

        var content = container.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1);
        if (content is null)
            return;

        var expanded = content.Visibility != Visibility.Visible;
        content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        var chevron = FindDescendant<FontIcon>(
            button,
            "CollectionAccordionChevron");
        if (chevron is not null)
            chevron.Glyph = expanded ? "\uE70E" : "\uE70D";

        var title = button.Tag as string ?? string.Empty;
        var collapsedNames = new HashSet<string>(
            _userState.CollapsedCollectionNames,
            StringComparer.OrdinalIgnoreCase);
        if (expanded)
            collapsedNames.Remove(title);
        else
            collapsedNames.Add(title);
        _userState = _userState with
        {
            CollapsedCollectionNames = collapsedNames,
        };
        await PersistCollectionUiStateAsync();
        UpdateCollapseAllCollectionsState();

        var action = _app.Localization.Get(
            expanded ? "CollapseCollection" : "ExpandCollection");
        AutomationProperties.SetName(button, $"{action}: {title}");
    }

    private async void CollapseAllCollectionsButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        _collectionsCollapsed = !_collectionsCollapsed;
        var visibility = _collectionsCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;
        var glyph = _collectionsCollapsed ? "\uE70D" : "\uE70E";
        foreach (var content in FindDescendants<FrameworkElement>(
                     this,
                     "CollectionAccordionContent"))
        {
            content.Visibility = visibility;
        }
        foreach (var chevron in FindDescendants<FontIcon>(
                     this,
                     "CollectionAccordionChevron"))
        {
            chevron.Glyph = glyph;
        }
        _userState = _userState with
        {
            CollapsedCollectionNames = _collectionsCollapsed
                ? ViewModel.CollectionGroups
                    .Select(group => group.Title)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        await PersistCollectionUiStateAsync();
        UpdateCollapseAllCollectionsState();
    }

    private void CollectionAccordion_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Border { Tag: string title } container)
            return;

        var collapsed = _userState.CollapsedCollectionNames.Contains(title);
        var content = FindDescendant<FrameworkElement>(
            container,
            "CollectionAccordionContent");
        if (content is not null)
            content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        var chevron = FindDescendant<FontIcon>(
            container,
            "CollectionAccordionChevron");
        if (chevron is not null)
            chevron.Glyph = collapsed ? "\uE70D" : "\uE70E";
    }

    private void CollectionGroupGrid_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LibraryGroup group)
            return;
        OpenCollectionGroup(group);
    }

    private void CollectionCardButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: LibraryGroup group })
            OpenCollectionGroup(group);
    }

    private void CollectionOverviewGame_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LibraryItem game)
            return;

        var group = (sender as FrameworkElement)?.DataContext as LibraryGroup
            ?? ViewModel.CollectionGroups.FirstOrDefault(candidate =>
                candidate.Items.Contains(game));
        if (group is null)
            return;

        ViewModel.SelectedGame = game;
        OpenCollectionGroup(group);
    }

    private void OpenCollectionGroup(LibraryGroup group)
    {
        _selectedCollectionGroup = group;
        CollectionDetailTitle.Text = group.Title;
        CollectionDetailProgress.Text = group.ProgressToolTip;
        CollectionDetailGameGrid.ItemsSource = group.Items;
        CollectionDetailGameList.ItemsSource = group.Items;
        if (!group.Items.Contains(ViewModel.SelectedGame))
            ViewModel.SelectedGame = group.Items.FirstOrDefault() ?? LibraryItem.Empty;
        CollectionDetailGameGrid.SelectedItem = ViewModel.SelectedGame;
        CollectionDetailGameList.SelectedItem = ViewModel.SelectedGame;
        UpdateCollectionsViewControls();
    }

    private void RefreshSelectedCollectionGroup()
    {
        if (_selectedCollectionGroup is null)
            return;

        var title = _selectedCollectionGroup.Title;
        _selectedCollectionGroup = ViewModel.CollectionGroups.FirstOrDefault(
            group => string.Equals(
                group.Title,
                title,
                StringComparison.OrdinalIgnoreCase));
        if (_selectedCollectionGroup is null)
        {
            CollectionDetailGameGrid.ItemsSource = null;
            CollectionDetailGameList.ItemsSource = null;
            UpdateCollectionsViewControls();
            return;
        }

        CollectionDetailProgress.Text = _selectedCollectionGroup.ProgressToolTip;
        CollectionDetailGameGrid.ItemsSource = _selectedCollectionGroup.Items;
        CollectionDetailGameList.ItemsSource = _selectedCollectionGroup.Items;
        if (!_selectedCollectionGroup.Items.Contains(ViewModel.SelectedGame))
        {
            ViewModel.SelectedGame =
                _selectedCollectionGroup.Items.FirstOrDefault()
                ?? LibraryItem.Empty;
        }
        CollectionDetailGameGrid.SelectedItem = ViewModel.SelectedGame;
        CollectionDetailGameList.SelectedItem = ViewModel.SelectedGame;
    }

    private void CollectionBackButton_Click(object sender, RoutedEventArgs args)
    {
        _selectedCollectionGroup = null;
        CollectionDetailGameGrid.ItemsSource = null;
        CollectionDetailGameList.ItemsSource = null;
        UpdateCollectionsViewControls();
    }

    private async void CollectionArtworkButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not Button { Tag: LibraryGroup group })
            return;
        await ChooseCollectionArtworkAsync(group);
    }

    private async void CollectionDetailArtworkButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_selectedCollectionGroup is not null)
            await ChooseCollectionArtworkAsync(_selectedCollectionGroup);
    }

    private async void CollectionDetailRemoveArtworkButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_selectedCollectionGroup is null)
            return;
        await RemoveCollectionArtworkAsync(_selectedCollectionGroup.Title);
    }

    private async void CollectionDetailDeleteButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_selectedCollectionGroup is not null)
            await ConfirmDeleteCollectionAsync(_selectedCollectionGroup);
    }

    private async Task ConfirmDeleteCollectionAsync(LibraryGroup group)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = Strings["DeleteCollection"],
            Content = new TextBlock
            {
                Text = _app.Localization.Format(
                    "DeleteCollectionWarning",
                    group.Title),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Strings["Delete"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyDialogTheme(confirm);
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var collections = await _app.NativeLibraryService.LoadGameCollectionsAsync(
                0,
                _loadCancellation.Token);
            var tag = collections.Collections.FirstOrDefault(item =>
                string.Equals(
                    item.Name,
                    group.Title,
                    StringComparison.OrdinalIgnoreCase));
            if (tag is null)
                return;

            await ViewModel.DeleteCollectionAsync(tag.TagId, _loadCancellation.Token);
            var state = await _app.UserLibraryStateStore.LoadAsync(
                _loadCancellation.Token);
            state.CollectionArtworkPaths.TryGetValue(group.Title, out var artwork);
            _userState = state with
            {
                LibraryFilterTags = state.LibraryFilterTags
                    .Where(name => !string.Equals(
                        name,
                        group.Title,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                CollapsedCollectionNames = state.CollapsedCollectionNames
                    .Where(name => !string.Equals(
                        name,
                        group.Title,
                        StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                CollectionArtworkPaths = state.CollectionArtworkPaths
                    .Where(item => !string.Equals(
                        item.Key,
                        group.Title,
                        StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        item => item.Key,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase),
            };
            await _app.UserLibraryStateStore.SaveAsync(
                _userState,
                _loadCancellation.Token);
            if (!string.IsNullOrWhiteSpace(artwork))
                DeleteManagedCollectionArtwork(artwork);
            ViewModel.SetCollectionArtworkPaths(_userState.CollectionArtworkPaths);
            if (_selectedCollectionGroup is not null
                && string.Equals(
                    _selectedCollectionGroup.Title,
                    group.Title,
                    StringComparison.OrdinalIgnoreCase))
            {
                _selectedCollectionGroup = null;
                CollectionDetailGameGrid.ItemsSource = null;
                CollectionDetailGameList.ItemsSource = null;
            }
            RebuildCollectionFilterButtons();
            UpdateCollectionsViewControls();
            UpdateCollapseAllCollectionsState();
            SyncSelection();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["CollectionsSaveFailed"], exception.Message);
        }
    }

    private async void CollectionDetailAddModsButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_selectedCollectionGroup is null)
            return;
        await ShowAddModsToCollectionDialogAsync(_selectedCollectionGroup);
    }

    private async Task ShowAddModsToCollectionDialogAsync(LibraryGroup group)
    {
        var collectionName = group.Title;
        var existingIds = group.Items
            .Select(item => item.GameFileId)
            .ToHashSet();
        var selectedIds = new HashSet<int>();
        var candidates = ViewModel.CatalogItems
            .Where(item => string.Equals(
                item.Category,
                "Mod",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var filter = new TextBox
        {
            PlaceholderText = Strings["FilterMods"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(filter, Strings["FilterMods"]);
        var entries = new StackPanel { Spacing = 2 };

        void RebuildEntries()
        {
            entries.Children.Clear();
            var query = filter.Text.Trim();
            foreach (var item in candidates.Where(item =>
                         query.Length == 0
                         || item.Title.Contains(
                             query,
                             StringComparison.CurrentCultureIgnoreCase)))
            {
                var alreadyAssigned = existingIds.Contains(item.GameFileId);
                var checkBox = new CheckBox
                {
                    Content = item.Title,
                    IsChecked = alreadyAssigned || selectedIds.Contains(item.GameFileId),
                    IsEnabled = !alreadyAssigned,
                    Tag = item.GameFileId,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                ApplyGreenCheckBox(checkBox);
                checkBox.Checked += (_, _) => selectedIds.Add(item.GameFileId);
                checkBox.Unchecked += (_, _) => selectedIds.Remove(item.GameFileId);
                entries.Children.Add(checkBox);
            }
            if (entries.Children.Count == 0)
            {
                entries.Children.Add(new TextBlock
                {
                    Text = Strings["NoMatchingMods"],
                    Margin = new Thickness(4, 10, 4, 10),
                });
            }
        }

        filter.TextChanged += (_, _) => RebuildEntries();
        RebuildEntries();
        var content = new StackPanel
        {
            Width = 520,
            Spacing = 12,
            Children =
            {
                filter,
                new ScrollViewer
                {
                    MaxHeight = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = entries,
                },
            },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = Strings["AddModsToCollection"],
            Content = content,
            PrimaryButtonText = Strings["AddSelectedMods"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || selectedIds.Count == 0)
        {
            return;
        }

        try
        {
            await ViewModel.AddGamesToCollectionAsync(
                collectionName,
                selectedIds,
                _loadCancellation.Token);
            RefreshSelectedCollectionGroup();
            SyncSelection();
            await ShowActionMessageAsync(
                Strings["CollectionUpdated"],
                _app.Localization.Format(
                    "ModsAddedToCollection",
                    selectedIds.Count,
                    collectionName),
                Strings["Close"]);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["CollectionUpdateFailed"], exception.Message);
        }
    }

    private void UpdateCollectionsViewControls()
    {
        var showingDetail = _selectedCollectionGroup is not null;
        CollectionListHost.Visibility = !showingDetail && _showCollectionList
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectionGridHost.Visibility = !showingDetail && _showCollectionList
            ? Visibility.Collapsed
            : Visibility.Visible;
        CollectionOverviewHost.Visibility = !showingDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectionDetailHost.Visibility = showingDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectionDetailRemoveArtworkButton.Visibility =
            showingDetail && _selectedCollectionGroup!.HasCustomArtwork
                ? Visibility.Visible
                : Visibility.Collapsed;
        CollectionDetailGridHost.Visibility =
            showingDetail && !_showCollectionDetailList
                ? Visibility.Visible
                : Visibility.Collapsed;
        CollectionDetailListHost.Visibility =
            showingDetail && _showCollectionDetailList
                ? Visibility.Visible
                : Visibility.Collapsed;

        var activeList = showingDetail
            ? _showCollectionDetailList
            : _showCollectionList;
        CollectionColumnsButton.Visibility = activeList
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollectionTileSizeSlider.IsEnabled = !activeList;
        CollapseAllCollectionsButton.Visibility =
            !showingDetail && _showCollectionList
                ? Visibility.Visible
                : Visibility.Collapsed;
        CollectionsViewModeIcon.Glyph = activeList ? "\uE8FD" : "\uE80A";
        var action = activeList
            ? _app.Localization.Get("GridActivation")
            : _app.Localization.Get("ListActivation");
        AutomationProperties.SetName(CollectionsViewModeButton, action);
        ToolTipService.SetToolTip(CollectionsViewModeButton, action);
        if (showingDetail)
            SyncSelection();
    }

    private void UpdateCollapseAllCollectionsState()
    {
        _collectionsCollapsed = ViewModel.CollectionGroups.Count > 0
            && ViewModel.CollectionGroups.All(group =>
                _userState.CollapsedCollectionNames.Contains(group.Title));
        var key = _collectionsCollapsed
            ? "ExpandAllCollections"
            : "CollapseAllCollections";
        CollapseAllCollectionsButton.Content = Strings[key];
        AutomationProperties.SetName(CollapseAllCollectionsButton, Strings[key]);
        ToolTipService.SetToolTip(CollapseAllCollectionsButton, Strings[key]);
    }

    private async Task PersistCollectionUiStateAsync()
    {
        await _collectionStateGate.WaitAsync();
        try
        {
            // Accordion state is a short, atomic user action and must outlive
            // page-load cancellation so it is still saved during navigation
            // or immediately before the window is closed.
            var current = await _app.UserLibraryStateStore.LoadAsync();
            _userState = current with
            {
                CollapsedCollectionNames = new HashSet<string>(
                    _userState.CollapsedCollectionNames,
                    StringComparer.OrdinalIgnoreCase),
            };
            await _app.UserLibraryStateStore.SaveAsync(_userState);
        }
        finally
        {
            _collectionStateGate.Release();
        }
    }

    private async Task ChooseCollectionArtworkAsync(LibraryGroup group)
    {
        try
        {
            var selectedFiles = await PickImageFilesAsync(multiple: false);
            if (selectedFiles.Count == 0)
                return;

            await SaveCollectionArtworkAsync(group.Title, selectedFiles[0]);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(
                Strings["CollectionsSaveFailed"],
                exception.Message);
        }
    }

    private async Task SaveCollectionArtworkAsync(
        string collectionName,
        string sourcePath)
    {
        var newReference = StoreCollectionArtwork(collectionName, sourcePath);
        string? oldReference = null;
        await _collectionStateGate.WaitAsync(_loadCancellation.Token);
        try
        {
            var current = await _app.UserLibraryStateStore.LoadAsync(
                _loadCancellation.Token);
            var artworkPaths = new Dictionary<string, string>(
                current.CollectionArtworkPaths,
                StringComparer.OrdinalIgnoreCase);
            artworkPaths.TryGetValue(collectionName, out oldReference);
            artworkPaths[collectionName] = newReference;
            _userState = current with
            {
                CollectionArtworkPaths = artworkPaths,
            };
            await _app.UserLibraryStateStore.SaveAsync(
                _userState,
                _loadCancellation.Token);
        }
        finally
        {
            _collectionStateGate.Release();
        }

        if (!string.IsNullOrWhiteSpace(oldReference)
            && !string.Equals(
                oldReference,
                newReference,
                StringComparison.OrdinalIgnoreCase))
        {
            DeleteManagedCollectionArtwork(oldReference);
        }

        ViewModel.SetCollectionArtworkPaths(_userState.CollectionArtworkPaths);
        RefreshSelectedCollectionGroup();
        UpdateCollectionsViewControls();
    }

    private async Task RemoveCollectionArtworkAsync(string collectionName)
    {
        string? oldReference = null;
        await _collectionStateGate.WaitAsync(_loadCancellation.Token);
        try
        {
            var current = await _app.UserLibraryStateStore.LoadAsync(
                _loadCancellation.Token);
            var artworkPaths = new Dictionary<string, string>(
                current.CollectionArtworkPaths,
                StringComparer.OrdinalIgnoreCase);
            artworkPaths.TryGetValue(collectionName, out oldReference);
            artworkPaths.Remove(collectionName);
            _userState = current with
            {
                CollectionArtworkPaths = artworkPaths,
            };
            await _app.UserLibraryStateStore.SaveAsync(
                _userState,
                _loadCancellation.Token);
        }
        finally
        {
            _collectionStateGate.Release();
        }

        if (!string.IsNullOrWhiteSpace(oldReference))
            DeleteManagedCollectionArtwork(oldReference);
        ViewModel.SetCollectionArtworkPaths(_userState.CollectionArtworkPaths);
        RefreshSelectedCollectionGroup();
        UpdateCollectionsViewControls();
    }

    private static string StoreCollectionArtwork(
        string collectionName,
        string sourcePath)
    {
        var root = GetPortableRoot();
        var artworkDirectory = Path.Combine(
            root,
            "Data",
            "CollectionArtworks");
        Directory.CreateDirectory(artworkDirectory);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp")
            throw new NotSupportedException("Unsupported collection artwork format.");

        var collectionHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(collectionName.ToUpperInvariant())))
            .ToLowerInvariant()[..20];
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var contentHash = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(sourceFullPath)))
            .ToLowerInvariant()[..12];
        var destination = Path.Combine(
            artworkDirectory,
            $"{collectionHash}-{contentHash}{extension}");
        if (!sourceFullPath.Equals(destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceFullPath, destination, overwrite: true);
        return Path.GetRelativePath(root, destination)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static void DeleteManagedCollectionArtwork(string reference)
    {
        try
        {
            var root = GetPortableRoot();
            var artworkDirectories = new[]
            {
                Path.GetFullPath(Path.Combine(
                    root,
                    "Data",
                    "CollectionArtworks")),
                Path.GetFullPath(Path.Combine(
                root,
                "UserData",
                "CollectionArtworks")),
            };
            var fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(reference)
                    ? reference
                    : Path.Combine(root, reference));
            if (artworkDirectories.Any(directory =>
                    IsInsideDirectory(fullPath, directory))
                && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            // A stale optional artwork must never block collection management.
        }
    }

}
