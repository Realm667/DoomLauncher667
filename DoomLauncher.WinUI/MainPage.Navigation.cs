using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private bool CanUseLibraryShortcuts => !_isImporting && !_isRescraping && !ViewModel.IsLoading && !HasOpenPopup();
    private readonly Stack<LibrarySection> _navigationHistory = new();
    private bool _navigatingBack;
    private bool _restoringViewControls;

    private ScrollViewer? SectionScrollViewer(LibrarySection section) => section switch
    {
        LibrarySection.Home => HomeScrollViewer,
        LibrarySection.Generate => GenerateScrollViewer,
        LibrarySection.Achievements => AchievementsScrollViewer,
        LibrarySection.Discover => FindDescendant<ScrollViewer>(DiscoverHost),
        LibrarySection.Collections => FindDescendant<ScrollViewer>(
            CollectionDetailHost.Visibility == Visibility.Visible
                ? (_showCollectionDetailList ? CollectionDetailGameList : CollectionDetailGameGrid)
                : (_showCollectionList ? CollectionListHost : CollectionOverviewHost)),
        _ => FindDescendant<ScrollViewer>(_showList ? GameList : GameGrid),
    };

    private void RestoreViewControls()
    {
        _restoringViewControls = true;
        try
        {
            SortOrderBox.SelectedItem = SortOrderBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == ViewModel.SortOrder.ToString());
            var category = ViewModel.CurrentCategoryFilter;
            var collection = ViewModel.CurrentCollectionFilter;
            AllFilterButton.IsChecked = collection is null && category == LibraryCategoryFilter.All;
            IwadFilterButton.IsChecked = collection is null && category == LibraryCategoryFilter.Iwads;
            ModsFilterButton.IsChecked = collection is null && category == LibraryCategoryFilter.Mods;
            UnplayedFilterButton.IsChecked = collection is null && category == LibraryCategoryFilter.Unplayed;
            foreach (var button in _collectionFilterButtons)
                button.IsChecked = string.Equals(button.Tag?.ToString(), collection, StringComparison.OrdinalIgnoreCase);
        }
        finally { _restoringViewControls = false; }
        UpdateSortDescription();
    }

    private void ConfigureKeyboardNavigation()
    {
        var import = new KeyboardAccelerator { Key = VirtualKey.I, Modifiers = VirtualKeyModifiers.Control };
        import.Invoked += (_, args) =>
        {
            if (!CanUseLibraryShortcuts) return;
            args.Handled = true;
            ImportButton_Click(this, new RoutedEventArgs());
        };
        KeyboardAccelerators.Add(import);
        var back = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
        back.Invoked += (_, args) => { if (CanUseLibraryShortcuts) args.Handled = GoBack(); };
        KeyboardAccelerators.Add(back);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += async (_, args) =>
        {
            if (!CanUseLibraryShortcuts) return;
            if (SearchBox.Text.Length > 0)
            {
                _searchCancellation?.Cancel(); SearchBox.Text = string.Empty;
                args.Handled = true;
                if (ViewModel.ActiveSection == LibrarySection.Discover)
                    await ViewModel.SearchDiscoverAsync(string.Empty, _loadCancellation.Token);
                else ViewModel.Filter(string.Empty);
            }
            else args.Handled = GoBack();
        };
        KeyboardAccelerators.Add(escape);
        foreach (var list in new Control[] { GameList, GameGrid })
        {
            list.UseSystemFocusVisuals = true;
            var play = new KeyboardAccelerator { Key = VirtualKey.Enter };
            play.Invoked += async (_, args) =>
            {
                if (!CanUseLibraryShortcuts) return;
                var focused = FocusManager.GetFocusedElement(XamlRoot);
                if (focused is not ListViewItem and not GridViewItem && !ReferenceEquals(focused, GameList) && !ReferenceEquals(focused, GameGrid)) return;
                args.Handled = true;
                await ViewModel.LaunchSelectedAsync(_loadCancellation.Token);
            };
            list.KeyboardAccelerators.Add(play);
        }
    }

    private bool HasOpenPopup() => XamlRoot is not null
        && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0;

    private void RememberNavigation(LibrarySection previous, LibrarySection next)
    {
        if (_navigatingBack) { _navigatingBack = false; return; }
        if (previous != next) _navigationHistory.Push(previous);
    }

    private bool GoBack()
    {
        if (!_navigationHistory.TryPop(out var previous)) return false;
        var item = ShellNavigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), previous.ToString(), StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        _navigatingBack = true;
        ShellNavigation.SelectedItem = item;
        return true;
    }
}
