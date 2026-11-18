using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private void ConfigureLibraryReconciliation()
    {
        if (ViewModel.Games is not ReconciledCollection<LibraryItem> items) return;
        double vertical = 0, horizontal = 0;
        bool restoreFocus = false;
        DependencyObject? focusBeforeUpdate = null;
        var generation = 0;
        static bool IsInside(DependencyObject? element, DependencyObject ancestor)
        {
            for (; element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, ancestor)) return true;
            return false;
        }
        items.Updating += () =>
        {
            var list = _showList ? (Control)GameList : GameGrid;
            var scroll = FindDescendant<ScrollViewer>(list);
            vertical = scroll?.VerticalOffset ?? 0;
            horizontal = scroll?.HorizontalOffset ?? 0;
            focusBeforeUpdate = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            restoreFocus = IsInside(focusBeforeUpdate, list);
        };
        items.Updated += () =>
        {
            var current = ++generation;
            var savedVertical = vertical; var savedHorizontal = horizontal; var savedFocus = restoreFocus;
            var savedFocusElement = focusBeforeUpdate;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (current != generation || _loadCancellation.IsCancellationRequested) return;
                // ApplyFilter has now restored the final selection. Navigation
                // and Latest enqueue their explicit target viewport afterward.
                SyncSelection();
                var list = _showList ? (ListViewBase)GameList : GameGrid;
                list.UpdateLayout();
                var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                var focusSurvived = ReferenceEquals(focused, savedFocusElement)
                    && focused is FrameworkElement { IsLoaded: true };
                var stillInList = IsInside(focused, list);
                if (savedFocus && !focusSurvived
                    && (focused is null or FrameworkElement { IsLoaded: false } || stillInList))
                {
                    // Leave a surviving focus alone. Refocusing a ListView can
                    // initiate another scroll; a new focus outside it belongs
                    // to the user's next action or an open dialog.
                    if (list.ContainerFromItem(ViewModel.SelectedGame) is Control selected)
                        selected.Focus(FocusState.Programmatic);
                    else if (!stillInList)
                        list.Focus(FocusState.Programmatic);
                }
                FindDescendant<ScrollViewer>(list)?.ChangeView(savedHorizontal, savedVertical, null, true);
            });
        };
    }
}
