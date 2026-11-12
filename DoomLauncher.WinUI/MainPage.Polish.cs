using DoomLauncher.WinUI.Services;
using DoomLauncher.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DoomLauncher.WinUI;

public sealed partial class MainPage
{
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _arrangingResponsiveLayout;
    internal UiLayoutProfile? CurrentLayoutProfile { get; private set; }
    private ContentDialog? _activeThemedDialog;

    private void ResponsiveLayout_SizeChanged(object sender, SizeChangedEventArgs args) => ApplyResponsiveLayout();

    private void EmptyStateAction_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.CatalogItems.Count == 0) { ImportButton_Click(sender,args); return; }
        SearchBox.Text = ""; ViewModel.Filter(""); ViewModel.SetCollectionFilter(null);
        ViewModel.SetCategoryFilter(LibraryCategoryFilter.All); RestoreViewControls();
    }

    private async void LibraryErrorDetails_Click(object sender, RoutedEventArgs args) =>
        await ShowErrorAsync(Strings["LibraryLoadFailed"], ViewModel.ErrorMessage);

    internal void ApplyResponsiveLayout(double? testWidth = null, double? testHeight = null)
    {
        if (_arrangingResponsiveLayout || ContentLayout is null || ActualWidth <= 0) return;
        _arrangingResponsiveLayout = true;
        try
        {
            var profile = UiLayoutPolicy.ForViewport(testWidth ?? ActualWidth, testHeight ?? ActualHeight);
            if(CurrentLayoutProfile?.CompactNavigation!=profile.CompactNavigation)
            {
                ShellNavigation.PaneDisplayMode = profile.CompactNavigation ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
                ShellNavigation.IsPaneOpen = !profile.CompactNavigation;
            }
            CurrentLayoutProfile = profile;
            ((DoomLauncher.WinUI.Models.AccordionDensityLayout)Resources["AccordionDensityLayout"]).FitViewport(testHeight ?? ActualHeight);
            NavigationBrand.Visibility = ShellNavigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            SidebarStatistics.Visibility = NavigationBrand.Visibility;
            ShellNavigation.IsPaneToggleButtonVisible = profile.CompactNavigation;
            ContentLayout.Padding = new Thickness(profile.Padding, 14, profile.Padding, 16);
            Grid.SetRow(PageHeading, 0); Grid.SetColumnSpan(PageHeading, profile.StackedHeader ? 3 : 1);
            Grid.SetRow(SearchBox, profile.StackedHeader ? 1 : 0);
            Grid.SetColumn(SearchBox, profile.StackedHeader ? 0 : 1);
            Grid.SetColumnSpan(SearchBox, profile.StackedHeader ? 2 : 1);
            SearchColumn.Width = new GridLength(profile.StackedHeader ? 0 : 300);
            Grid.SetRow(PageTools, profile.StackedHeader ? 1 : 0);
            Grid.SetRow(LibraryViewTools, profile.StackedTools ? 1 : 0);
            Grid.SetColumn(LibraryViewTools, profile.StackedTools ? 0 : 1);
            Grid.SetColumnSpan(LibraryViewTools, profile.StackedTools ? 2 : 1);
            LibraryViewTools.Margin = new Thickness(0, profile.StackedTools ? 8 : 0, 0, 0);
            LibraryDetailsColumn.Width = new GridLength(profile.InlineDetails ? profile.DetailsWidth : 0);
            CollectionGridDetailsColumn.Width = LibraryDetailsColumn.Width;
            CollectionListDetailsColumn.Width = LibraryDetailsColumn.Width;
            foreach (var pane in VisualDescendants<GameDetailsPane>(this))
                if (pane.Parent is Grid grid && Grid.GetColumn(pane) == 1)
                    pane.Visibility = profile.InlineDetails ? Visibility.Visible : Visibility.Collapsed;
            CompactDetailsButton.Visibility = profile.InlineDetails ? Visibility.Collapsed : Visibility.Visible;
            CompactDetailsButton.IsEnabled = !ViewModel.SelectedGame.IsPlaceholder;
            CompactCollectionDetailsButton.Visibility = CompactDetailsButton.Visibility;
            CompactCollectionDetailsButton.IsEnabled = CompactDetailsButton.IsEnabled;
            Grid.SetRow(CollectionActionToolbar, profile.StackedHeader ? 1 : 0);
            Grid.SetColumn(CollectionActionToolbar, profile.StackedHeader ? 0 : 2);
            Grid.SetColumnSpan(CollectionActionToolbar, profile.StackedHeader ? 3 : 1);
            CollectionActionToolbar.MaxWidth = Math.Max(240, ContentLayout.ActualWidth - 60);
            CollectionsViewTools.MaxWidth = Math.Max(240, ContentLayout.ActualWidth - 60);
            HomeShowcase.Height = profile.HeroHeight;
            ((DoomLauncher.WinUI.Models.ShowcaseLayout)Resources["ShowcaseLayout"]).SetHeight(profile.HeroHeight);
            ColumnLayout.SetAvailableWidth(Math.Max(280, LibraryBody.ActualWidth - (profile.InlineDetails ? profile.DetailsWidth + 20 : 0)));
            var collectionWidth=CollectionDetailHost.ActualWidth>0?CollectionDetailHost.ActualWidth:ContentLayout.ActualWidth-profile.Padding*2;
            CollectionColumnLayout.SetAvailableWidth(Math.Max(280, collectionWidth - (profile.InlineDetails ? profile.DetailsWidth + 20 : 0)));
        }
        finally { _arrangingResponsiveLayout = false; }
    }

    private async void CompactDetails_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedGame.IsPlaceholder || HasOpenPopup()) return;
        var pane = new GameDetailsPane { ViewModel = ViewModel, DataContext = Strings,
            Width = UiLayoutPolicy.DialogWidth(XamlRoot.Size.Width, 440), Height = UiLayoutPolicy.DialogHeight(XamlRoot.Size.Height) - 40 };
        var flyout = new Flyout { Content = pane };
        pane.PlayRequested += (_,e) => { flyout.Hide(); PlayButton_Click(this,e); }; pane.LaunchOptionsRequested += LaunchOptionsButton_Click;
        pane.LaunchWithOptionsRequested += LaunchWithOptionsButton_Click;
        pane.FavoriteRequested += FavoriteButton_Click; pane.FinishedRequested += FinishedButton_Click;
        pane.QueueRequested += PersonalQueue_Click;
        // Other workflows use ContentDialogs. Close the details flyout first to
        // avoid nesting dialogs and preserve the invoking control's focus.
        void OpenAfterClose(RoutedEventHandler action, object? source, RoutedEventArgs e) { flyout.Hide(); action(source ?? this, e); }
        pane.EditRequested += (s,e) => OpenAfterClose(EditButton_Click,s,e);
        pane.RescrapeRequested += (s,e) => OpenAfterClose(RescrapeIdGamesButton_Click,s,e);
        pane.ManageCollectionsRequested += (s,e) => OpenAfterClose(ManageCollectionsButton_Click,s,e);
        pane.ReadMoreRequested += (s,e) => OpenAfterClose(ReadMoreButton_Click,s,e);
        pane.MapsRequested += (s,e) => OpenAfterClose(MapsProgress_Click,s,e);
        pane.SavesRequested += (s,e) => OpenAfterClose(Saves_Click,s,e);
        pane.HistoryRequested += (s,e) => OpenAfterClose(MetadataHistory_Click,s,e);
        flyout.ShowAt((FrameworkElement)sender);
        await Task.CompletedTask;
    }

    private void ShowFeedback(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _feedbackTimer.Stop(); FeedbackBar.Title = title; FeedbackBar.Severity = severity;
        FeedbackBar.Message = message.Length > 350 ? message[..350] + "…" : message;
        FeedbackBar.ActionButton = null;
        if (message.Length > 350)
        {
            var details = new Button { Content = Strings["Details"] };
            details.Click += async (_,_) => await ShowMessageAsync(title, message);
            FeedbackBar.ActionButton = details;
        }
        FeedbackBar.IsOpen = true;
        if (message.Length <= 350 && severity != InfoBarSeverity.Error) _feedbackTimer.Start();
    }

    private void ConfigurePolish()
    {
        Loaded += (_,_) => ApplyResponsiveLayout();
        void RefreshNavigationChrome() { NavigationBrand.Visibility=ShellNavigation.IsPaneOpen?Visibility.Visible:Visibility.Collapsed;SidebarStatistics.Visibility=NavigationBrand.Visibility; }
        ShellNavigation.PaneOpened += (_,_) => RefreshNavigationChrome();
        ShellNavigation.PaneClosed += (_,_) => RefreshNavigationChrome();
        _feedbackTimer.Tick += (_,_) => { _feedbackTimer.Stop(); FeedbackBar.IsOpen = false; };
        Unloaded += (_,_) => _feedbackTimer.Stop();
        var details = new KeyboardAccelerator { Key = VirtualKey.D, Modifiers = VirtualKeyModifiers.Control };
        details.Invoked += (_,e) =>
        {
            var target=ViewModel.ActiveSection==LibrarySection.Collections?CompactCollectionDetailsButton:CompactDetailsButton;
            var visibleSection=ViewModel.LibraryVisibility==Visibility.Visible
                || ViewModel.ActiveSection==LibrarySection.Collections && CollectionDetailHost.Visibility==Visibility.Visible;
            if(CanUseLibraryShortcuts && visibleSection && target.Visibility==Visibility.Visible)
            { e.Handled=true;CompactDetails_Click(target,new RoutedEventArgs()); }
        };
        KeyboardAccelerators.Add(details);
        foreach(var list in new Control[] { GameList,GameGrid,CollectionDetailGameList,CollectionDetailGameGrid })
        {
            list.UseSystemFocusVisuals=true;
            var menu=new KeyboardAccelerator { Key=VirtualKey.F10, Modifiers=VirtualKeyModifiers.Shift };
            menu.Invoked+=(_,e)=> { if(CanUseLibraryShortcuts && !ViewModel.SelectedGame.IsPlaceholder) { e.Handled=true;((MenuFlyout)Resources["LibraryItemContextMenu"]).ShowAt(list); } };
            list.KeyboardAccelerators.Add(menu);
        }
        foreach (var nav in ShellNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            nav.SetBinding(AutomationProperties.NameProperty,new Microsoft.UI.Xaml.Data.Binding { Source=nav,Path=new PropertyPath("Content") });
            nav.SetBinding(ToolTipService.ToolTipProperty,new Microsoft.UI.Xaml.Data.Binding { Source=nav,Path=new PropertyPath("Content") });
        }
    }

    private void UpdateSortDescription()
    {
        var restoring=_restoringViewControls;
        _restoringViewControls=true;
        try
        {
        foreach(var selected in new[] { (SortOrderBox,ViewModel.SortOrder), (CollectionSortOrderBox,ViewModel.CollectionSortOrder) })
            if(!selected.Item1.Items.OfType<ComboBoxItem>().Any(item=>item.Tag?.ToString()==selected.Item2.ToString()))
                selected.Item1.Items.Add(new ComboBoxItem { Tag=selected.Item2.ToString(),Content=Strings[selected.Item2.ToString()] });
        foreach (var pair in new[] { (SortOrderBox, ViewModel.SortOrder, ViewModel.SortDescending),
            (CollectionSortOrderBox, ViewModel.CollectionSortOrder, ViewModel.CollectionSortDescending) })
            foreach (var item in pair.Item1.Items.OfType<ComboBoxItem>())
                if (item.Tag is string key)
                {
                    var active = key.Equals(pair.Item2.ToString(), StringComparison.OrdinalIgnoreCase);
                    var direction = key is "Title" or "Author" ? (pair.Item3 ? "Z–A" : "A–Z") : (pair.Item3 ? "↓" : "↑");
                    item.Content = Strings[key] + (active ? " · " + direction : "");
                    if (active) { ToolTipService.SetToolTip(pair.Item1, item.Content); AutomationProperties.SetHelpText(pair.Item1, item.Content.ToString()); }
                }
            // WinUI copies the selected item's content into SelectionBoxItem.
            // Refresh that copy after localization/direction changes, without
            // interpreting a visual update as a new sorting command.
            foreach(var selected in new[] { (SortOrderBox,ViewModel.SortOrder), (CollectionSortOrderBox,ViewModel.CollectionSortOrder) })
            {
                selected.Item1.SelectedItem=null;
                selected.Item1.SelectedItem=selected.Item1.Items.OfType<ComboBoxItem>().First(item=>item.Tag?.ToString()==selected.Item2.ToString());
            }
        }
        finally { _restoringViewControls=restoring; }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }

    private void FitDialogToViewport(ContentDialog dialog)
    {
        if (XamlRoot is null) return;
        var width = UiLayoutPolicy.DialogWidth(XamlRoot.Size.Width, 1080);
        var height = UiLayoutPolicy.DialogHeight(XamlRoot.Size.Height);
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(420d, width);
        dialog.Resources["ContentDialogMaxWidth"] = width + 48;
        dialog.MaxWidth = XamlRoot.Size.Width - 16;
        dialog.MaxHeight = XamlRoot.Size.Height - 16;
        if (dialog.Content is FrameworkElement content)
        {
            content.MaxWidth = Math.Min(content.MaxWidth, width);
            content.MaxHeight = Math.Min(content.MaxHeight, height);
            if (!double.IsNaN(content.Width)) content.Width = Math.Min(content.Width, width);
            content.MinWidth = Math.Min(content.MinWidth, width);
            foreach (var element in VisualDescendants<FrameworkElement>(content))
            {
                element.MinWidth = Math.Min(element.MinWidth, width);
                if (!double.IsNaN(element.Width) && element.Width > width) element.Width = width;
                if (element is ScrollViewer) element.MaxHeight = Math.Min(element.MaxHeight, height);
            }
        }
    }
}
