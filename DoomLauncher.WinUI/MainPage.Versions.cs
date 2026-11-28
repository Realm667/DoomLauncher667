using DoomLauncher.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private bool _syncingVersionSelection;
    private bool _updatingVersionRows;

    private void VersionRow_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (!_syncingVersionSelection && !_updatingVersionRows && args.ClickedItem is LibraryItem row)
            ViewModel.SelectedGame = row;
    }

    private async void GroupVersions_Click(object sender, RoutedEventArgs args)
    {
        try { await ViewModel.SetVersionGroupingAsync(GroupVersionsButton.IsChecked == true); SyncSelection(); }
        catch (Exception error) { await ShowMessageAsync(_app.Localization.Get("NotAvailable"), error.Message); }
    }

    private void VersionToggle_PointerPressed(object sender, PointerRoutedEventArgs args) => args.Handled = true;

    private async void VersionToggle_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: LibraryItem row }) return;
        try
        {
            // Expanding can recycle the chevron's container while the selected
            // older child becomes visible. Keep focus in the list during that update.
            GameList.Focus(FocusState.Programmatic);
            await ViewModel.ToggleVersionGroupAsync(row); SyncSelection();
            // Retain keyboard focus on the same group chevron across reconciliation.
            if (sender is Control { IsLoaded: true } button) button.Focus(FocusState.Programmatic);
        }
        catch (Exception error) { await ShowMessageAsync(_app.Localization.Get("NotAvailable"), error.Message); }
    }

    private void ConfigureVersionKeyboard()
    {
        foreach (var key in new[] {VirtualKey.Left,VirtualKey.Right})
        {
            var accelerator = new KeyboardAccelerator {Key=key};
            accelerator.Invoked += async (_,args) =>
            {
                // Scoped to this list, including its chevrons. A tooltip must not
                // block arrow keys; dialogs and menu popups have a separate scope.
                if (_isImporting || _isRescraping || ViewModel.IsLoading || !ViewModel.VersionGroupingActive) return;
                var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                var insideList = false;
                for (var element = focused; element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
                    if (ReferenceEquals(element, GameList)) { insideList = true; break; }
                if (!insideList) return;
                var selected = GameList.SelectedItem as LibraryItem;
                var header = selected?.IsVersionGroup == true ? selected : ViewModel.ListRows.FirstOrDefault(row => row.VersionMembers.Contains(selected!));
                if (header is null) return;
                args.Handled = true;
                try { await ViewModel.ToggleVersionGroupAsync(header, key == VirtualKey.Right); SyncSelection(); }
                catch (Exception error) { await ShowMessageAsync(_app.Localization.Get("NotAvailable"), error.Message); }
            };
            GameList.KeyboardAccelerators.Add(accelerator);
        }
    }
}
