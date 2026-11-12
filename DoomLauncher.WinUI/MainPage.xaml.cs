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

public sealed partial class MainPage : Page
{
    private readonly CancellationTokenSource _loadCancellation = new();
    private CancellationTokenSource? _searchCancellation;
    private readonly App _app;
    private UserLibraryState _userState = UserLibraryState.Empty;
    private ScrollViewer? _listScrollViewer;
    private bool _synchronizingHorizontalScroll;
    private bool _showList = true;
    private bool _showCollectionList = true;
    private bool _showCollectionDetailList;
    private bool _collectionsCollapsed;
    private bool _isRescraping;
    private LibraryGroup? _selectedCollectionGroup;
    private readonly List<ToggleButton> _collectionFilterButtons = [];
    private readonly DispatcherTimer _statusDismissTimer = new();
    private readonly DispatcherTimer _newBadgeTimer = new();
    private readonly DispatcherTimer _tileImageRefreshTimer = new();
    private readonly List<FileSystemWatcher> _tileImageWatchers = [];
    private readonly SemaphoreSlim _collectionStateGate = new(1, 1);
    private CancellationTokenSource? _obsidianGenerationCancellation;
    private ObsidianInstallation? _obsidianInstallation;
    private readonly Dictionary<LibrarySection, double> _sectionScrollOffsets = [];

    public MainPage()
    {
        _app = (App)Application.Current;
        ViewModel = new MainViewModel(
            _app.LibraryCatalog,
            _app.LaunchOptionsCatalog,
            _app.LaunchService,
            _app.NativeLibraryService,
            _app.IdGamesService,
            _app.UserLibraryStateStore,
            _app.Localization,
            _app.LibraryFeatures);
        ViewModel.AchievementUnlocked += ViewModel_AchievementUnlocked;
        InitializeComponent();
        ConfigureKeyboardNavigation();
        _statusDismissTimer.Interval = TimeSpan.FromSeconds(5);
        _statusDismissTimer.Tick += StatusDismissTimer_Tick;
        _tileImageRefreshTimer.Interval = TimeSpan.FromMilliseconds(650);
        _tileImageRefreshTimer.Tick += TileImageRefreshTimer_Tick;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ImportsRevealed += RevealLatestImports;
        _newBadgeTimer.Interval = TimeSpan.FromMinutes(1);
        _newBadgeTimer.Tick += (_, _) =>
        {
            foreach (var game in ViewModel.CatalogItems)
                game.RefreshNewBadge();
        };
        Loaded += (_, _) => _newBadgeTimer.Start();
        DebugNavigationItem.Visibility = _app.IsDebugMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        DataContext = Strings;
        ConfigurePolish();
        ApplyGeneratorLocalization();

        Loaded += MainPage_Loaded;
        Unloaded += (_, _) =>
        {
            _searchCancellation?.Cancel();
            _loadCancellation.Cancel();
            _statusDismissTimer.Stop();
            _newBadgeTimer.Stop();
            _tileImageRefreshTimer.Stop();
            _obsidianGenerationCancellation?.Cancel();
            _multiplayerCancellation?.Cancel();
            DisposeTileImageWatchers();
        };
    }

    public MainViewModel ViewModel { get; }
    private readonly Dictionary<ComboBoxItem, string> _generatorChoiceKeys = [];

    private void ApplyGeneratorLocalization()
    {
        // ComboBoxItems do not reliably inherit DataContext from the page.
        // Keep their invariant option tags and localize only display content.
        var boxes = new[] { ObsidianLengthBox, ObsidianThemeBox,
                ObsidianMapSizeBox, ObsidianOutdoorsBox, ObsidianCavesBox,
                ObsidianLiquidsBox, ObsidianHallwaysBox, ObsidianSteepnessBox,
                ObsidianMonstersBox, ObsidianStrengthBox, ObsidianBossesBox,
                ObsidianTrapsBox, ObsidianCagesBox, ObsidianHealthBox,
                ObsidianAmmoBox, ObsidianWeaponsBox };
        if (_generatorChoiceKeys.Count == 0)
        {
            foreach (var box in boxes)
                foreach (var item in box.Items.OfType<ComboBoxItem>())
                    _generatorChoiceKeys[item] = "GeneratorChoice" + item.Content.ToString()!.Replace(" ", "");
        }
        foreach (var (item, key) in _generatorChoiceKeys)
            item.Content = Strings[key];
        foreach (var box in boxes)
        {
            var selected = box.SelectedIndex;
            box.SelectedIndex = -1;
            box.SelectedIndex = selected;
        }
    }
    public UiText Strings => _app.Localization.Text;
    private ElementTheme EffectiveDialogTheme =>
        RequestedTheme == ElementTheme.Default ? ActualTheme : RequestedTheme;
    private ListColumnLayout ColumnLayout =>
        (ListColumnLayout)Resources[nameof(ListColumnLayout)];
    private ListColumnLayout CollectionColumnLayout =>
        (ListColumnLayout)Resources["CollectionListColumnLayout"];
    private ListDensityLayout ListDensity =>
        (ListDensityLayout)Resources[nameof(ListDensityLayout)];
    private AccordionDensityLayout AccordionDensity =>
        (AccordionDensityLayout)Resources[nameof(AccordionDensityLayout)];

    private async void MainPage_Loaded(object sender, RoutedEventArgs args)
    {
        _userState = _app.InitialUserState;
        var testedThemes = _userState.TestedThemes.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var stateChanged = testedThemes.Add(_userState.Theme);
        var filtersWithoutFinished = _userState.LibraryFilterTags
            .Where(tag => !string.Equals(
                tag,
                "Finished",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (filtersWithoutFinished.Length != _userState.LibraryFilterTags.Count)
            stateChanged = true;
        if (stateChanged)
        {
            _userState = _userState with
            {
                TestedThemes = testedThemes,
                LibraryFilterTags = filtersWithoutFinished,
            };
            await _app.UserLibraryStateStore.SaveAsync(
                _userState,
                _loadCancellation.Token);
        }
        _app.Localization.SetLanguage(_userState.Language);
        ViewModel.NotifyLocalizationChanged();
        DataContext = null;
        DataContext = Strings;
        ApplyGeneratorLocalization();
        ThemeManager.Apply(this, _userState.Theme);
        (_app.MainWindow as MainWindow)?.ApplyTitleBarTheme();
        ListDensity.Apply(_userState.ListDensity);
        AccordionDensity.Apply(_userState.AccordionDensity);
        ColumnLayout.Apply(_userState.VisibleColumns);
        CollectionColumnLayout.Apply(_userState.CollectionVisibleColumns);
        RebuildCollectionFilterButtons();
        SyncColumnMenu();
        SyncCollectionColumnMenu();
        if (!_app.MigrationService.DatabaseExists())
            await ShowMigrationDialogAsync(firstStart: true);
        if (!_app.MigrationService.DatabaseExists())
            await _app.FirstSetupService.EnsureDatabaseAsync(_loadCancellation.Token);
        if (_app.MigrationService.DatabaseExists())
        {
            try
            {
                await _app.FirstSetupService.EnsureManagedLayoutAsync(
                    _loadCancellation.Token);
                if (await _app.FirstSetupService.ShouldRunWizardAsync(
                        _loadCancellation.Token))
                {
                    await ShowFirstSetupWizardAsync();
                }
                await _app.NativeLibraryService.CleanupDerivedThumbnailsAsync(
                    _loadCancellation.Token);
                await _app.NativeLibraryService.BackfillMapMetadataAsync(
                    _loadCancellation.Token);
            }
            catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Artwork cleanup is optional and must never block library startup.
            }
        }
        await ViewModel.LoadAsync(_loadCancellation.Token);
        ConfigureTileImageWatcher();
        UpdateCollectionsViewControls();
        UpdateCollapseAllCollectionsState();
        SyncSelection();
        UpdateSortHeaders();
        InitializeListScrollSynchronization();
        if (_app.UserLibraryStateStore is JsonUserLibraryStateStore { RecoveryNoticeKey: { } notice })
            await ShowActionMessageAsync(Strings["Settings"], Strings[notice], Strings["Close"]);
        await RunNativeUiChecksAsync();
        if (_app.IsDebugMode)
        {
            DebugEnvironmentText.Text = string.Join(
                Environment.NewLine,
                $"Version: {typeof(MainPage).Assembly.GetName().Version}",
                $"Base: {AppContext.BaseDirectory}",
                $"Database: {Environment.GetEnvironmentVariable(DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable)}",
                $"User state: {Environment.GetEnvironmentVariable(JsonUserLibraryStateStore.StateEnvironmentVariable)}",
                "Command line: --debug");
        }
    }

    private void ViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.SelectedGame))
        {
            CompactDetailsButton.IsEnabled = !ViewModel.SelectedGame.IsPlaceholder;
            CompactCollectionDetailsButton.IsEnabled = CompactDetailsButton.IsEnabled;
        }
        if (args.PropertyName == nameof(MainViewModel.SectionTitle))
        {
            UpdateSortDescription();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>ApplyResponsiveLayout());
        }
        if (args.PropertyName != nameof(MainViewModel.LaunchStatus))
            return;

        _statusDismissTimer.Stop();
        if (ViewModel.HasLaunchStatus)
            _statusDismissTimer.Start();
    }

    private void StatusDismissTimer_Tick(object? sender, object args)
    {
        _statusDismissTimer.Stop();
        ViewModel.DismissLaunchStatus();
    }

    private void ConfigureTileImageWatcher()
    {
        DisposeTileImageWatchers();
        var tileImagesRoot = Path.Combine(
            GetPortableRoot(),
            "Data",
            "TileImages");
        if (!Directory.Exists(tileImagesRoot))
            return;

        var watcher = new FileSystemWatcher(tileImagesRoot, "*.png")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.CreationTime
                | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += TileImageWatcher_Changed;
        watcher.Created += TileImageWatcher_Changed;
        watcher.Deleted += TileImageWatcher_Changed;
        watcher.Renamed += TileImageWatcher_Changed;
        _tileImageWatchers.Add(watcher);
    }

    private void DisposeTileImageWatchers()
    {
        foreach (var watcher in _tileImageWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= TileImageWatcher_Changed;
            watcher.Created -= TileImageWatcher_Changed;
            watcher.Deleted -= TileImageWatcher_Changed;
            watcher.Renamed -= TileImageWatcher_Changed;
            watcher.Dispose();
        }
        _tileImageWatchers.Clear();
    }

    private void TileImageWatcher_Changed(
        object sender,
        FileSystemEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_loadCancellation.IsCancellationRequested)
                return;
            _tileImageRefreshTimer.Stop();
            _tileImageRefreshTimer.Start();
        });
    }

    private async void TileImageRefreshTimer_Tick(object? sender, object args)
    {
        LibraryItem.ClearArtworkCache();
        foreach (var item in ViewModel.CatalogItems) item.InvalidateArtwork();
        _tileImageRefreshTimer.Stop();
        if (_loadCancellation.IsCancellationRequested)
            return;
        try
        {
            await ViewModel.RefreshAsync(_loadCancellation.Token);
            RefreshSelectedCollectionGroup();
            SyncSelection();
        }
        catch (OperationCanceledException)
            when (_loadCancellation.IsCancellationRequested)
        {
        }
    }

    private async void DebugRefresh_Click(object sender, RoutedEventArgs args)
    {
        if (!_app.IsDebugMode)
            return;
        DebugOutputText.Text = Strings["Updating"];
        await ViewModel.RefreshAsync(_loadCancellation.Token);
        DebugOutputText.Text = Strings["DebugRefreshComplete"];
    }

    private async void DebugDatabase_Click(object sender, RoutedEventArgs args)
    {
        if (!_app.IsDebugMode)
            return;
        var report = await ViewModel.CheckDatabaseHealthAsync(
            repair: false,
            _loadCancellation.Token);
        DebugOutputText.Text = _app.Localization.Format(
            "DebugDatabaseResult",
            report.IntegrityResult,
            report.OrphanedFileRows,
            report.OrphanedTagMappings,
            report.MissingManagedFiles);
    }

    private async void DebugRepairDatabase_Click(object sender, RoutedEventArgs args)
    {
        if (!_app.IsDebugMode)
            return;
        var report = await ViewModel.CheckDatabaseHealthAsync(
            repair: true,
            _loadCancellation.Token);
        DebugOutputText.Text = _app.Localization.Format(
            "DebugDatabaseRepairResult",
            report.IntegrityResult,
            report.OrphanedFileRows,
            report.OrphanedTagMappings,
            report.MissingManagedFiles,
            report.BackupPath);
    }

    private async void DebugSourcePorts_Click(object sender, RoutedEventArgs args)
    {
        if (!_app.IsDebugMode)
            return;
        var definitions = await _app.NativeLibraryService
            .LoadLauncherDefinitionsAsync(_loadCancellation.Token);
        DebugOutputText.Text = string.Join(
            Environment.NewLine,
            definitions.SourcePorts.Select(port => _app.Localization.Format(
                "DebugSourcePortLine",
                port.DisplayLabel,
                port.ScreenshotSupport,
                port.StatisticsAdapter)));
    }

    private void DebugAchievement_Click(object sender, RoutedEventArgs args)
    {
        if (_app.IsDebugMode)
            ViewModel.TriggerDebugAchievementNotification();
    }

    private async void DebugIdGamesRefresh_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (!_app.IsDebugMode || _isRescraping)
            return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = Strings["DebugRefreshIdGamesMetadata"],
            Content = new TextBlock
            {
                MaxWidth = 560,
                Text = Strings["DebugRefreshIdGamesConfirm"],
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Strings["Continue"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyDialogTheme(confirm);
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        _isRescraping = true;
        DebugIdGamesRefreshButton.IsEnabled = false;
        try
        {
            var progress = new Progress<IdGamesBulkRefreshProgress>(value =>
            {
                DebugOutputText.Text = _app.Localization.Format(
                    "DebugRefreshIdGamesProgress",
                    Math.Min(value.Completed + 1, value.Total),
                    value.Total,
                    value.Title);
            });
            var result = await ViewModel.RefreshLinkedIdGamesMetadataAsync(
                progress,
                _loadCancellation.Token);
            DebugOutputText.Text = _app.Localization.Format(
                "DebugRefreshIdGamesResult",
                result.Total,
                result.Updated,
                result.ArtworkImported,
                result.Failed);
            SyncSelection();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DebugOutputText.Text = _app.Localization.Format(
                "DebugRefreshIdGamesFailed",
                exception.Message);
        }
        finally
        {
            _isRescraping = false;
            DebugIdGamesRefreshButton.IsEnabled = true;
        }
    }

    private void ViewModel_AchievementUnlocked(AchievementItem achievement)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AchievementNotificationBar.Title =
                Strings["AchievementUnlockedNotification"];
            AchievementNotificationBar.Message =
                $"{achievement.Title} — {achievement.Description}";
            AchievementNotificationBar.IsOpen = true;
            ElementSoundPlayer.Play(ElementSoundKind.Show);
        });
    }

    private void InitializeListScrollSynchronization()
    {
        _listScrollViewer ??= FindDescendant<ScrollViewer>(GameList);
        if (_listScrollViewer is null)
            return;

        _listScrollViewer.ViewChanged -= ListScrollViewer_ViewChanged;
        _listScrollViewer.ViewChanged += ListScrollViewer_ViewChanged;
        _listScrollViewer.ChangeView(
            HeaderScrollViewer.HorizontalOffset,
            null,
            null,
            disableAnimation: true);
    }

    private void HeaderScrollViewer_ViewChanged(
        object sender,
        ScrollViewerViewChangedEventArgs args)
    {
        if (_synchronizingHorizontalScroll)
            return;
        InitializeListScrollSynchronization();
        if (_listScrollViewer is null)
            return;

        _synchronizingHorizontalScroll = true;
        _listScrollViewer.ChangeView(
            HeaderScrollViewer.HorizontalOffset,
            null,
            null,
            disableAnimation: true);
        _synchronizingHorizontalScroll = false;
    }

    private void ListScrollViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs args)
    {
        if (_synchronizingHorizontalScroll || _listScrollViewer is null)
            return;

        _synchronizingHorizontalScroll = true;
        HeaderScrollViewer.ChangeView(
            _listScrollViewer.HorizontalOffset,
            null,
            null,
            disableAnimation: true);
        _synchronizingHorizontalScroll = false;
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }

    private async void SearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation.Token);
        var cancellationToken = _searchCancellation.Token;
        try
        {
            await Task.Delay(350, cancellationToken);
            var searchText = sender.Text;
            if (ViewModel.ActiveSection == LibrarySection.Discover)
                await ViewModel.SearchDiscoverAsync(searchText, cancellationToken);
            else
                QueueViewUpdate(() => ViewModel.Filter(searchText));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SearchAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void GameGrid_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (GameGrid.SelectedItem is LibraryItem game)
            ViewModel.SelectedGame = game;
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (GameList.SelectedItem is LibraryItem game)
            ViewModel.SelectedGame = game;
    }

    private void CollectionDetailGameList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (CollectionDetailGameList.SelectedItem is LibraryItem game)
            ViewModel.SelectedGame = game;
    }

    private void CollectionDetailGameGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (CollectionDetailGameGrid.SelectedItem is LibraryItem game)
            ViewModel.SelectedGame = game;
    }

    private void CollectionDetailGameGrid_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LibraryItem game)
            return;
        ViewModel.SelectedGame = game;
        CollectionDetailGameGrid.SelectedItem = game;
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.LaunchSelectedAsync(_loadCancellation.Token);
    }

    private async void LaunchOptionsButton_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.LoadLaunchOptionsAsync(_loadCancellation.Token);
    }

    private async void LaunchWithOptionsButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is GameDetailsPane detailsPane)
            detailsPane.CloseLaunchOptions();
        else
            LibraryDetailsPane.CloseLaunchOptions();
        await ViewModel.LaunchSelectedWithOptionsAsync(_loadCancellation.Token);
    }

    private async void ShellNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            await ShowSettingsDialogAsync();
            RestoreActiveNavigationSelection();
            return;
        }

        if (args.SelectedItemContainer?.Tag as string == "Settings")
        {
            await ShowSettingsDialogAsync();
            RestoreActiveNavigationSelection();
            return;
        }

        if (args.SelectedItemContainer?.Tag as string == "LauncherDefinitions")
        {
            await ShowLauncherDefinitionsDialogAsync();
            RestoreActiveNavigationSelection();
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag
            || !Enum.TryParse<LibrarySection>(tag, ignoreCase: true, out var section))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            var previousSection = ViewModel.ActiveSection;
            RememberNavigation(previousSection, section);
            if (previousSection != section)
                _sectionScrollOffsets[previousSection] = SectionScrollViewer(previousSection)?.VerticalOffset ?? 0;
            ViewModel.SetSection(section);
            SearchBox.Text = ViewModel.NavigationSearchText;
            RestoreViewControls();
            UpdateSortHeaders();
            SyncSelection();
            if (previousSection != section)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    GameList.UpdateLayout(); GameGrid.UpdateLayout();
                    SectionScrollViewer(section)?.ChangeView(null,
                        _sectionScrollOffsets.GetValueOrDefault(section), null, true);
                });
            if (section == LibrarySection.Discover)
                await ViewModel.EnsureDiscoverLoadedAsync(_loadCancellation.Token);
            else if (section == LibrarySection.Generate)
                await RefreshObsidianDetectionAsync();
            else if (section == LibrarySection.Multiplayer)
                await InitializeMultiplayerAsync();
            if (section != LibrarySection.Multiplayer) _multiplayerCancellation?.Cancel();
        });
    }

    private void CategoryFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton selected
            || selected.Tag is not string tag
            || !Enum.TryParse<LibraryCategoryFilter>(
                tag,
                ignoreCase: true,
                out var filter))
        {
            return;
        }

        foreach (var button in new[]
                 {
                     AllFilterButton,
                     IwadFilterButton,
                     ModsFilterButton,
                     UnplayedFilterButton,
                 })
        {
            button.IsChecked = ReferenceEquals(button, selected);
        }
        foreach (var button in _collectionFilterButtons)
            button.IsChecked = false;

        QueueViewUpdate(() =>
        {
            ViewModel.SetCollectionFilter(null);
            ViewModel.SetCategoryFilter(filter);
        });
    }

    private void CollectionFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton selected
            || selected.Tag is not string tag)
        {
            return;
        }

        foreach (var button in new[]
                 {
                     AllFilterButton,
                     IwadFilterButton,
                     ModsFilterButton,
                     UnplayedFilterButton,
                 })
        {
            button.IsChecked = false;
        }
        foreach (var button in _collectionFilterButtons)
            button.IsChecked = ReferenceEquals(button, selected);

        QueueViewUpdate(() => ViewModel.SetCollectionFilter(tag));
    }

    private void RebuildCollectionFilterButtons()
    {
        foreach (var button in _collectionFilterButtons)
            CategoryFilterPanel.Children.Remove(button);
        _collectionFilterButtons.Clear();

        foreach (var tag in _userState.LibraryFilterTags)
        {
            var button = new ToggleButton
            {
                Content = string.Equals(tag, "Finished", StringComparison.OrdinalIgnoreCase)
                    ? Strings["Finished"]
                    : tag,
                Tag = tag,
                Style = (Style)Application.Current.Resources["PillToggleStyle"],
            };
            button.Click += CollectionFilter_Click;
            _collectionFilterButtons.Add(button);
            CategoryFilterPanel.Children.Add(button);
        }
    }

    private void LibraryItemContextMenu_Opening(
        object sender,
        object args)
    {
        if (sender is not MenuFlyout flyout)
            return;
        var target = flyout.Target as FrameworkElement;
        var item = target?.DataContext as LibraryItem
            ?? target?.Tag as LibraryItem
            ?? (!ViewModel.SelectedGame.IsPlaceholder
                ? ViewModel.SelectedGame
                : null);
        if (item is null || item.IsPlaceholder)
            return;

        var actions = flyout.Items.OfType<MenuFlyoutItem>().ToArray();
        if (actions.Length < 5)
            return;
        actions[0].Text = Strings[item.IsFavorite
            ? "RemoveFavorite"
            : "AddFavorite"];
        actions[1].Text = Strings[item.IsFinished
            ? "MarkUnfinished"
            : "MarkFinished"];
        actions[2].Text = Strings["ManageCollections"];
        actions[3].Text = Strings["EditEntry"];
        actions[4].Text = Strings["DeleteMod"];
        actions[4].Visibility = item.Category.Equals(
            "IWAD",
            StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
        foreach (var action in actions)
        {
            action.Visibility = Visibility.Visible;
            action.Tag = item;
        }
        actions[4].Visibility = item.Category.Equals(
            "IWAD",
            StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CollectionContextMenu_Opening(object sender, object args)
    {
        if (sender is not MenuFlyout flyout)
            return;
        var target = flyout.Target as FrameworkElement;
        var group = target?.DataContext as LibraryGroup
            ?? target?.Tag as LibraryGroup
            ?? _selectedCollectionGroup;
        var actions = flyout.Items.OfType<MenuFlyoutItem>().ToArray();
        if (group is null || actions.Length < 4)
        {
            foreach (var action in actions)
                action.Visibility = Visibility.Collapsed;
            return;
        }

        actions[0].Text = Strings["AddModsToCollection"];
        actions[1].Text = Strings["ChooseCollectionArtwork"];
        actions[2].Text = Strings["RemoveCollectionArtwork"];
        actions[2].Visibility = group.HasCustomArtwork
            ? Visibility.Visible
            : Visibility.Collapsed;
        actions[3].Text = Strings["DeleteCollection"];
        foreach (var action in actions)
            action.Tag = group;
    }

    private static LibraryGroup? CollectionFromMenu(object sender) =>
        (sender as MenuFlyoutItem)?.Tag as LibraryGroup;

    private async void CollectionContextAddMods_Click(object sender, RoutedEventArgs args)
    {
        if (CollectionFromMenu(sender) is { } group)
            await ShowAddModsToCollectionDialogAsync(group);
    }

    private async void CollectionContextChooseArtwork_Click(object sender, RoutedEventArgs args)
    {
        if (CollectionFromMenu(sender) is { } group)
            await ChooseCollectionArtworkAsync(group);
    }

    private async void CollectionContextRemoveArtwork_Click(object sender, RoutedEventArgs args)
    {
        if (CollectionFromMenu(sender) is { } group)
            await RemoveCollectionArtworkAsync(group.Title);
    }

    private async void CollectionContextDelete_Click(object sender, RoutedEventArgs args)
    {
        if (CollectionFromMenu(sender) is { } group)
            await ConfirmDeleteCollectionAsync(group);
    }

    private void SelectContextItem(object sender)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryItem item })
            return;
        ViewModel.SelectedGame = item;
        SyncSelection();
    }

    private async void ContextFavorite_Click(object sender, RoutedEventArgs args)
    {
        SelectContextItem(sender);
        await ViewModel.ToggleSelectedFavoriteAsync(_loadCancellation.Token);
        SyncSelection();
    }

    private async void ContextFinished_Click(object sender, RoutedEventArgs args)
    {
        SelectContextItem(sender);
        await ViewModel.ToggleSelectedFinishedAsync(_loadCancellation.Token);
        SyncSelection();
    }

    private async void ContextCollections_Click(object sender, RoutedEventArgs args)
    {
        SelectContextItem(sender);
        await ShowManageCollectionsDialogAsync();
    }

    private async void ContextEdit_Click(object sender, RoutedEventArgs args)
    {
        SelectContextItem(sender);
        await ShowEditDialogAsync();
    }

    private async void ContextDelete_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: LibraryItem item })
            return;
        SelectContextItem(sender);
        await ShowDeleteGameDialogAsync(item);
    }

    private async Task ShowDeleteGameDialogAsync(LibraryItem item)
    {
        var deleteFiles = new CheckBox
        {
            Content = Strings["DeleteModFiles"],
        };
        var content = new StackPanel
        {
            Spacing = 14,
        };
        content.Children.Add(new TextBlock
        {
            Text = _app.Localization.Format("DeleteModWarning", item.Title),
            MaxWidth = 520,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(deleteFiles);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings["DeleteMod"],
            Content = content,
            PrimaryButtonText = Strings["Delete"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyDialogTheme(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await _app.NativeLibraryService.DeleteGameAsync(
                item.GameFileId,
                deleteFiles.IsChecked == true,
                _loadCancellation.Token);
            await ViewModel.RefreshAsync(_loadCancellation.Token);
            SyncSelection();
            await ShowMessageAsync(
                Strings["DefinitionDeleted"],
                _app.Localization.Format("ModDeletedMessage", item.Title));
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["DeleteFailed"], exception.Message);
        }
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.ToggleSelectedFavoriteAsync(_loadCancellation.Token);
        SyncSelection();
    }

    private async void EditButton_Click(object sender, RoutedEventArgs args)
    {
        await ShowEditDialogAsync();
    }

    private async void RescrapeIdGamesButton_Click(
        object sender,
        RoutedEventArgs args) =>
        await ShowIdGamesMetadataDialogAsync();

    private async Task ShowIdGamesMetadataDialogAsync()
    {
        if (_isRescraping || ViewModel.SelectedGame.IsPlaceholder)
            return;
        if (ViewModel.SelectedGame.Category == "IWAD")
        {
            await ShowActionMessageAsync(Strings["IdGamesMetadataTitle"], Strings["IwadMetadataProtected"], Strings["Close"]);
            return;
        }

        _isRescraping = true;
        var isBlocking = false;
        try
        {
            var matches = await ViewModel.FindSelectedIdGamesMatchesAsync(
                _loadCancellation.Token);
            if (matches.Count == 0)
            {
                await ShowErrorAsync(
                    Strings["IdGamesMetadataTitle"],
                    Strings["IdGamesMetadataNoMatches"]);
                return;
            }

            var matchBox = new ComboBox
            {
                Header = Strings["IdGamesMetadataMatch"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(IdGamesItem.MatchLabel),
                ItemsSource = matches,
                SelectedIndex = 0,
            };
            AutomationProperties.SetName(
                matchBox,
                Strings["IdGamesMetadataMatch"]);
            var preview = new TextBlock
            {
                MaxWidth = 560,
                MaxHeight = 190,
                TextWrapping = TextWrapping.Wrap,
            };
            var gameId = ViewModel.SelectedGame.GameFileId;
            MetadataSnapshot currentMetadata;
            await using (var db = await _app.LibraryFeatures.OpenAsync(_loadCancellation.Token))
                currentMetadata = await LibraryFeatureStore.CaptureMetadataAsync(db, null, gameId, _loadCancellation.Token);
            var lockedFields = await _app.LibraryFeatures.LoadLocksAsync(gameId, _loadCancellation.Token);
            var fieldChecks = new Dictionary<string, CheckBox>(); var lockChecks = new Dictionary<string, CheckBox>();
            var fieldPanel = new StackPanel { Spacing = 8 };
            foreach (var field in LibraryFeatureStore.MetadataFields)
            {
                var apply = new CheckBox { IsChecked = !lockedFields.Contains(field), Tag = field };
                var protect = new CheckBox { Content = Strings["LockField"], IsChecked = lockedFields.Contains(field) };
                protect.Checked += (_, _) => { apply.IsChecked = false; apply.IsEnabled = false; };
                protect.Unchecked += (_, _) => apply.IsEnabled = true;
                apply.IsEnabled = !lockedFields.Contains(field); fieldChecks[field] = apply; lockChecks[field] = protect;
                fieldPanel.Children.Add(new StackPanel { Spacing = 2, Children = { apply, protect } });
            }
            string ShortValue(object? value)
            { var text = Convert.ToString(value) ?? "—"; return text.Length > 160 ? text[..160] + "…" : text; }
            void UpdatePreview()
            {
                if (matchBox.SelectedItem is not IdGamesItem item)
                    return;
                preview.Text =
                    $"{item.Title}\n{item.Author}\n{item.ReleaseDateText} · " +
                    $"{item.RatingText}\n\n{item.Description}";
                var before = new Dictionary<string, object?> { ["Title"] = currentMetadata.Title, ["Author"] = currentMetadata.Author,
                    ["Description"] = currentMetadata.Description, ["ReleaseDate"] = currentMetadata.ReleaseDate, ["Rating"] = currentMetadata.Rating, ["Maps"] = currentMetadata.MapCount };
                var after = new Dictionary<string, object?> { ["Title"] = item.Title, ["Author"] = item.Author, ["Description"] = item.Description,
                    ["ReleaseDate"] = item.ReleaseDate?.ToString("d"), ["Rating"] = item.Rating, ["Maps"] = Strings["RecountMaps"] };
                foreach (var field in LibraryFeatureStore.MetadataFields)
                    fieldChecks[field].Content = new TextBlock { Text = $"{Strings[field]}: {ShortValue(before[field])}\n→ {ShortValue(after[field])}", TextWrapping = TextWrapping.Wrap, MaxWidth = 510 };
            }
            matchBox.SelectionChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            var content = new StackPanel { Width = 560, Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = Strings["IdGamesMetadataFields"],
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(matchBox);
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 210,
                Content = preview,
            });
            content.Children.Add(fieldPanel);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = Strings["IdGamesMetadataTitle"],
                Content = new ScrollViewer { Content = content, MaxHeight = 620 },
                PrimaryButtonText = Strings["Apply"],
                CloseButtonText = Strings["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || matchBox.SelectedItem is not IdGamesItem selected)
            {
                return;
            }

            SetBlockingMetadataRefresh(true, 0);
            isBlocking = true;
            var progress = new Progress<double>(value =>
                SetBlockingMetadataRefresh(true, value));
            await _app.LibraryFeatures.SetLocksAsync(gameId, lockChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToHashSet(), _loadCancellation.Token);
            await ViewModel.ApplySelectedIdGamesMetadataAsync(
                selected,
                progress,
                _loadCancellation.Token,
                fieldChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToHashSet());
            SyncSelection();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (isBlocking)
            {
                SetBlockingMetadataRefresh(false);
                isBlocking = false;
            }
            await ShowErrorAsync(
                Strings["IdGamesMetadataFailed"],
                exception.Message);
        }
        finally
        {
            if (isBlocking)
                SetBlockingMetadataRefresh(false);
            _isRescraping = false;
        }
    }

    private void SetBlockingMetadataRefresh(bool isVisible, double progress = 0)
    {
        ShellNavigation.IsEnabled = !isVisible;
        BlockingOperationOverlay.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        BlockingOperationProgress.Value = Math.Clamp(progress, 0, 1) * 100;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs args)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var extension in ModImportBatch.SupportedExtensions) picker.FileTypeFilter.Add(extension);

        var app = (App)Application.Current;
        if (app.MainWindow is null)
            return;

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        var files = await picker.PickMultipleFilesAsync();
        await ImportModPathsAsync(files.Select(file => file.Path).ToArray());
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var decisions = await ResolveIwadsInModsAsync();
            var result = await RunProgressDialogAsync(
                Strings["RefreshingLibrary"],
                Strings["RefreshModsProgress"],
                progress => _app.FirstSetupService.ScanModsAsync(
                    _loadCancellation.Token,
                    progress,
                    decisions));
            await ViewModel.RefreshAsync(_loadCancellation.Token);
            if (result.Imported > 0)
                ViewModel.ShowLatestImports();
            SyncSelection();
            await ShowActionMessageAsync(
                Strings["LibraryRefreshComplete"],
                FormatSetupScanResult(result),
                Strings["Close"]);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(
                Strings["LibraryRefreshFailedTitle"],
                exception.Message);
        }
    }

    private void Page_DragOver(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        args.DragUIOverride.Caption = _app.Localization.Get("DragImport");
        args.DragUIOverride.IsCaptionVisible = true;
    }

    private async void Page_Drop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await args.DataView.GetStorageItemsAsync();
        await ImportModPathsAsync(items.OfType<Windows.Storage.StorageFile>()
            .Where(file => ModImportBatch.SupportedExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase))
            .Select(file => file.Path).ToArray());
    }

    private void SortOrderBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringViewControls) return;
        if (SortOrderBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag
            || !Enum.TryParse<LibrarySortOrder>(tag, ignoreCase: true, out var sortOrder))
        {
            return;
        }

        QueueViewUpdate(() =>
        {
            ViewModel.SetSortOrder(sortOrder);
            UpdateSortHeaders();
        });
    }

    private void CollectionSortOrderBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if(_restoringViewControls) return;
        if (CollectionSortOrderBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag
            || !Enum.TryParse<LibrarySortOrder>(
                tag,
                ignoreCase: true,
                out var sortOrder))
        {
            return;
        }

        QueueViewUpdate(() =>
        {
            ViewModel.SetCollectionSortOrder(sortOrder);
            UpdateSortDescription();
            RefreshSelectedCollectionGroup();
        });
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button
            || button.Tag is not string tag
            || !Enum.TryParse<LibrarySortOrder>(tag, true, out var sortOrder))
        {
            return;
        }

        QueueViewUpdate(() =>
        {
            ViewModel.SetSortOrder(sortOrder, toggleDirection: true);
            UpdateSortHeaders();
        });
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs args)
    {
        _showList = !_showList;
        GameGrid.Visibility = _showList ? Visibility.Collapsed : Visibility.Visible;
        ListViewHost.Visibility = _showList ? Visibility.Visible : Visibility.Collapsed;
        LibraryTileSizeSlider.IsEnabled = !_showList;
        ViewModeIcon.Glyph = _showList ? "\uE8FD" : "\uE80A";
        AutomationProperties.SetName(
            ViewModeButton,
            _showList
                ? _app.Localization.Get("GridActivation")
                : _app.Localization.Get("ListActivation"));
        SyncSelection();
    }

    private async void ImportBundleButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_app.MainWindow is null)
            return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".dl667pack");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        PortableBundleInspection inspection;
        try
        {
            inspection = await ViewModel.InspectPortableBundleAsync(
                file.Path,
                _loadCancellation.Token);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["BundleImportFailed"], exception.Message);
            return;
        }
        var generalMetadataCheck = new CheckBox
        {
            Content = Strings["ImportGeneralMetadata"],
            IsChecked = inspection.ContainsGeneralMetadata,
            IsEnabled = inspection.ContainsGeneralMetadata,
        };
        var personalMetadataCheck = new CheckBox
        {
            Content = Strings["ImportPersonalMetadata"],
            IsChecked = inspection.ContainsPersonalMetadata,
            IsEnabled = inspection.ContainsPersonalMetadata,
        };
        var screenshotsCheck = new CheckBox
        {
            Content = Strings["ImportScreenshots"],
            IsChecked = inspection.ContainsScreenshots,
            IsEnabled = inspection.ContainsScreenshots,
        };
        var titleArtworkCheck = new CheckBox
        {
            Content = Strings["ImportTitleArtwork"],
            IsChecked = inspection.ContainsTitleArtwork,
            IsEnabled = inspection.ContainsTitleArtwork,
        };
        var collectionsCheck = new CheckBox
        {
            Content = Strings["ImportCollections"],
            IsChecked = inspection.ContainsCollections,
            IsEnabled = inspection.ContainsCollections,
        };
        foreach (var checkBox in new[]
                 {
                     generalMetadataCheck,
                     personalMetadataCheck,
                     screenshotsCheck,
                     titleArtworkCheck,
                     collectionsCheck,
                 })
        {
            ApplyGreenCheckBox(checkBox);
        }
        var optionsDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings["ImportPackageOptions"],
            Content = new StackPanel
            {
                Width = 560,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings["ImportPackageOptionsDescription"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                    generalMetadataCheck,
                    personalMetadataCheck,
                    screenshotsCheck,
                    titleArtworkCheck,
                    collectionsCheck,
                },
            },
            PrimaryButtonText = Strings["ImportAction"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(optionsDialog);
        if (await optionsDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var conflictResolutions =
            new Dictionary<string, ImportFileConflictResolution>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inspection.Entries.Where(item => item.Conflict is not null))
        {
            conflictResolutions[entry.FileName] =
                await AskImportConflictResolutionAsync(entry.FileName);
        }
        try
        {
            var result = await ViewModel.ImportPortableBundleAsync(
                file.Path,
                new PortableBundleImportOptions(
                    generalMetadataCheck.IsChecked == true,
                    personalMetadataCheck.IsChecked == true,
                    screenshotsCheck.IsChecked == true,
                    titleArtworkCheck.IsChecked == true,
                    collectionsCheck.IsChecked == true,
                    conflictResolutions),
                _loadCancellation.Token);
            SyncSelection();
            await ShowMessageAsync(
                Strings["BundleImportComplete"],
                _app.Localization.Format(
                    "BundleImportSummary",
                    result.ImportedEntries,
                    result.ImportedMediaFiles,
                    result.Collections.Count));
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["BundleImportFailed"], exception.Message);
        }
    }

    private async void ExportBundleButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_app.MainWindow is null)
            return;
        var collectionNames = ViewModel.CatalogItems
            .SelectMany(item => item.Tags)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var exportType = new RadioButtons
        {
            Header = Strings["ExportSelectionType"],
            ItemsSource = new[]
            {
                Strings["ExportSingleMod"],
                Strings["ExportMultipleMods"],
                Strings["ExportCollectionType"],
            },
            SelectedIndex = ViewModel.SelectedGame.IsPlaceholder ? 1 : 0,
        };
        var singleModBox = new ComboBox
        {
            Header = Strings["SelectSingleMod"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(LibraryItem.Title),
            ItemsSource = ViewModel.CatalogItems
                .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            SelectedItem = ViewModel.SelectedGame.IsPlaceholder
                ? ViewModel.CatalogItems.FirstOrDefault()
                : ViewModel.SelectedGame,
        };
        var collectionBox = new ComboBox
        {
            Header = Strings["SelectCollection"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = collectionNames,
            SelectedIndex = collectionNames.Length > 0 ? 0 : -1,
        };
        var checks = ViewModel.CatalogItems
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item =>
            {
                var checkBox = new CheckBox
                {
                    Content = item.Title,
                    Tag = item,
                    IsChecked = ReferenceEquals(item, ViewModel.SelectedGame),
                };
                ApplyGreenCheckBox(checkBox);
                return checkBox;
            })
            .ToArray();
        var list = new StackPanel { Spacing = 3 };
        foreach (var check in checks)
            list.Children.Add(check);
        var multipleModsHost = new ScrollViewer
        {
            MaxHeight = 380,
            Padding = new Thickness(0, 0, 16, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        void UpdateExportSelectionVisibility()
        {
            singleModBox.Visibility = exportType.SelectedIndex == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            multipleModsHost.Visibility = exportType.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            collectionBox.Visibility = exportType.SelectedIndex == 2
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        exportType.SelectionChanged += (_, _) =>
            UpdateExportSelectionVisibility();
        UpdateExportSelectionVisibility();
        var content = new StackPanel
        {
            Width = 620,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            Text = Strings["BundleExportDescription"],
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(exportType);
        content.Children.Add(singleModBox);
        content.Children.Add(collectionBox);
        content.Children.Add(multipleModsHost);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings["ExportPackageSelection"],
            Content = content,
            PrimaryButtonText = Strings["Continue"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(dialog);
        dialog.Resources["ContentDialogMinWidth"] = 680d;
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        string? selectedCollection = null;
        var selectedSingleMod = singleModBox.SelectedItem as LibraryItem;
        int[] selectedIds;
        if (exportType.SelectedIndex == 0)
        {
            selectedIds = selectedSingleMod is not null
                ? [selectedSingleMod.GameFileId]
                : [];
        }
        else if (exportType.SelectedIndex == 1)
        {
            selectedIds = checks
                .Where(check => check.IsChecked == true)
                .Select(check => ((LibraryItem)check.Tag).GameFileId)
                .ToArray();
        }
        else
        {
            selectedCollection = collectionBox.SelectedItem as string;
            selectedIds = string.IsNullOrWhiteSpace(selectedCollection)
                ? []
                : ViewModel.CatalogItems
                    .Where(item => item.Tags.Contains(
                        selectedCollection,
                        StringComparer.CurrentCultureIgnoreCase))
                    .Select(item => item.GameFileId)
                    .ToArray();
        }
        if (selectedIds.Length == 0)
        {
            await ShowErrorAsync(
                Strings["ExportBundle"],
                Strings["SelectBundleEntries"]);
            return;
        }

        var generalMetadataCheck = new CheckBox
        {
            Content = Strings["ExportGeneralMetadata"],
            IsChecked = true,
        };
        var personalMetadataCheck = new CheckBox
        {
            Content = Strings["ExportPersonalMetadata"],
            IsChecked = true,
        };
        var screenshotsCheck = new CheckBox
        {
            Content = Strings["ExportScreenshots"],
            IsChecked = true,
        };
        var titleArtworkCheck = new CheckBox
        {
            Content = Strings["ExportTitleArtwork"],
            IsChecked = true,
        };
        var collectionsCheck = new CheckBox
        {
            Content = Strings["ExportCollections"],
            IsChecked = true,
        };
        foreach (var checkBox in new[]
                 {
                     generalMetadataCheck,
                     personalMetadataCheck,
                     screenshotsCheck,
                     titleArtworkCheck,
                     collectionsCheck,
                 })
        {
            ApplyGreenCheckBox(checkBox);
        }
        var dataModeDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Strings["ExportPackageContents"],
            Content = new StackPanel
            {
                Width = 560,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings["ExportPackageOptionsDescription"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                    generalMetadataCheck,
                    personalMetadataCheck,
                    screenshotsCheck,
                    titleArtworkCheck,
                    collectionsCheck,
                },
            },
            PrimaryButtonText = Strings["Continue"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(dataModeDialog);
        if (await dataModeDialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = !string.IsNullOrWhiteSpace(selectedCollection)
                ? $"{selectedCollection}-DoomLauncher667"
                : selectedIds.Length == 1
                    ? $"{selectedSingleMod?.Title ?? "DoomLauncher667"}-DoomLauncher667"
                    : "DoomLauncher667-Collection",
        };
        picker.FileTypeChoices.Add(
            "Doom Launcher 667",
            new List<string> { ".dl667pack" });
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        try
        {
            await ViewModel.ExportPortableBundleAsync(
                selectedIds,
                file.Path,
                generalMetadataCheck.IsChecked == true,
                personalMetadataCheck.IsChecked == true,
                screenshotsCheck.IsChecked == true,
                titleArtworkCheck.IsChecked == true,
                collectionsCheck.IsChecked == true,
                _loadCancellation.Token);
            await ShowMessageAsync(
                Strings["BundleExportComplete"],
                _app.Localization.Format("BundleExportSummary", selectedIds.Length));
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["BundleExportFailed"], exception.Message);
        }
    }

    private void RestoreActiveNavigationSelection()
    {
        var activeTag = ViewModel.ActiveSection.ToString();
        var activeItem = ShellNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                activeTag,
                StringComparison.OrdinalIgnoreCase));
        if (activeItem is not null)
            ShellNavigation.SelectedItem = activeItem;
    }

    private async void ColumnToggle_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleMenuFlyoutItem item || item.Tag is not string column)
            return;

        if (!ColumnLayout.Toggle(column, item.IsChecked))
        {
            item.IsChecked = true;
            return;
        }

        RefreshColumnGrids(ListViewHost, ColumnLayout, "LibraryColumnGrid");
        var currentState = await _app.UserLibraryStateStore.LoadAsync(_loadCancellation.Token);
        _userState = currentState with
        {
            VisibleColumns = ColumnLayout.VisibleColumns.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        await _app.UserLibraryStateStore.SaveAsync(_userState, _loadCancellation.Token);
    }

    private async void CollectionColumnToggle_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not ToggleMenuFlyoutItem item
            || item.Tag is not string column)
        {
            return;
        }

        if (!CollectionColumnLayout.Toggle(column, item.IsChecked))
        {
            item.IsChecked = true;
            return;
        }

        RefreshColumnGrids(
            CollectionListHost,
            CollectionColumnLayout,
            "CollectionColumnGrid");
        RefreshColumnGrids(
            CollectionDetailListHost,
            CollectionColumnLayout,
            "CollectionColumnGrid");
        var currentState =
            await _app.UserLibraryStateStore.LoadAsync(_loadCancellation.Token);
        _userState = currentState with
        {
            CollectionVisibleColumns = CollectionColumnLayout.VisibleColumns
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
        await _app.UserLibraryStateStore.SaveAsync(
            _userState,
            _loadCancellation.Token);
    }

    private void SyncColumnMenu()
    {
        foreach (var item in new[]
                 {
                     ArtworkColumnItem, TitleColumnItem, AuthorColumnItem, ReleaseDateColumnItem,
                     MapsColumnItem, RatingColumnItem, DownloadedColumnItem, SourcePortColumnItem,
                     PlaytimeColumnItem, FinishedColumnItem, FavoritesColumnItem,
                 })
        {
            item.IsChecked = item.Tag is string column
                && ColumnLayout.VisibleColumns.Contains(column);
        }
    }

    private void SyncCollectionColumnMenu()
    {
        foreach (var item in new[]
                 {
                     CollectionArtworkColumnItem, CollectionTitleColumnItem,
                     CollectionAuthorColumnItem, CollectionReleaseDateColumnItem,
                     CollectionMapsColumnItem, CollectionRatingColumnItem,
                     CollectionDownloadedColumnItem, CollectionSourcePortColumnItem,
                     CollectionPlaytimeColumnItem, CollectionFinishedColumnItem,
                     CollectionFavoritesColumnItem,
                 })
        {
            item.IsChecked = item.Tag is string column
                && CollectionColumnLayout.VisibleColumns.Contains(column);
        }
    }

    private void LibraryColumnGrid_Loaded(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is Grid grid)
            ApplyColumnVisibility(grid, ColumnLayout);
    }

    private void CollectionColumnGrid_Loaded(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is Grid grid)
            ApplyColumnVisibility(grid, CollectionColumnLayout);
    }

    private static void RefreshColumnGrids(
        DependencyObject root,
        ListColumnLayout layout,
        string gridTag)
    {
        if (root is Grid grid
            && string.Equals(
                grid.Tag as string,
                gridTag,
                StringComparison.Ordinal))
        {
            ApplyColumnVisibility(grid, layout);
        }

        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            RefreshColumnGrids(
                VisualTreeHelper.GetChild(root, index),
                layout,
                gridTag);
        }
    }

    private static void ApplyColumnVisibility(
        Grid grid,
        ListColumnLayout layout)
    {
        foreach (var child in grid.Children.OfType<FrameworkElement>())
        {
            var column = Grid.GetColumn(child);
            var key = column switch
            {
                0 => "Artwork",
                1 => "Title",
                2 => "Author",
                3 => "ReleaseDate",
                4 => "Maps",
                5 => "Rating",
                6 => "Downloaded",
                7 => "SourcePort",
                8 => "Playtime",
                9 => "Finished",
                10 => "Favorites",
                _ => null,
            };
            if (key is not null)
            {
                child.Visibility = layout.VisibleColumns.Contains(key)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private static T? FindDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && element.Name == name)
                return element;
            var nested = FindDescendant<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && element.Name == name)
                yield return element;
            foreach (var nested in FindDescendants<T>(child, name))
                yield return nested;
        }
    }

    private void SyncSelection()
    {
        if (GameGrid is not null)
            GameGrid.SelectedItem = ViewModel.SelectedGame;
        if (GameList is not null)
            GameList.SelectedItem = ViewModel.SelectedGame;
        if (CollectionDetailGameList is not null
            && _selectedCollectionGroup?.Items.Contains(ViewModel.SelectedGame) == true)
        {
            CollectionDetailGameGrid.SelectedItem = ViewModel.SelectedGame;
            CollectionDetailGameList.SelectedItem = ViewModel.SelectedGame;
        }
    }

    private void QueueViewUpdate(Action action)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_loadCancellation.IsCancellationRequested)
                return;
            action();
            SyncSelection();
        });
    }

    private void UpdateSortHeaders()
    {
        UpdateSortDescription();
        foreach (var button in new[]
                 {
                     TitleHeader, AuthorHeader, ReleaseDateHeader, MapsHeader,
                     RatingHeader, DownloadedHeader, SourcePortHeader, PlaytimeHeader,
                     FinishedHeader,
                 })
        {
            if (button.Tag is not string key)
                continue;
            var marker = Enum.TryParse<LibrarySortOrder>(key, true, out var order)
                         && order == ViewModel.SortOrder
                ? ViewModel.SortDescending ? " ▼" : " ▲"
                : string.Empty;
            button.Content = Strings[key] + marker;
        }
    }

    private async void FinishedCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox checkBox
            || checkBox.Tag is not int gameFileId)
        {
            return;
        }

        try
        {
            await ViewModel.SetFinishedAsync(
                gameFileId,
                checkBox.IsChecked == true,
                _loadCancellation.Token);
            _userState = await _app.UserLibraryStateStore.LoadAsync(_loadCancellation.Token);
            SyncSelection();
        }
        catch (Exception exception)
        {
            checkBox.IsChecked = !(checkBox.IsChecked == true);
            await ShowErrorAsync(Strings["FinishedSaveFailed"], exception.Message);
        }
    }

    private async void FinishedButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ViewModel.ToggleSelectedFinishedAsync(_loadCancellation.Token);
            SyncSelection();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["FinishedSaveFailed"], exception.Message);
        }
    }

    private void OverviewGrid_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LibraryItem game)
            return;
        OpenOverviewGame(game);
    }

    private void OverviewButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: LibraryItem game } || game.IsPlaceholder)
            return;
        OpenOverviewGame(game);
    }

    private void OpenOverviewGame(LibraryItem game)
    {
        SearchBox.Text = string.Empty;
        ViewModel.SetSection(LibrarySection.Library);
        ViewModel.Filter(string.Empty);
        ViewModel.SetCategoryFilter(LibraryCategoryFilter.All);
        ViewModel.SelectGame(game.GameFileId);
        AllFilterButton.IsChecked = true;
        IwadFilterButton.IsChecked = false;
        ModsFilterButton.IsChecked = false;
        UnplayedFilterButton.IsChecked = false;
        foreach (var button in _collectionFilterButtons)
            button.IsChecked = false;
        var libraryItem = ShellNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                LibrarySection.Library.ToString(),
                StringComparison.OrdinalIgnoreCase));
        if (libraryItem is not null)
            ShellNavigation.SelectedItem = libraryItem;
        DispatcherQueue.TryEnqueue(() =>
        {
            SyncSelection();
            if (GameGrid.Visibility == Visibility.Visible)
                GameGrid.ScrollIntoView(ViewModel.SelectedGame);
            if (GameList.Visibility == Visibility.Visible)
            {
                GameList.ScrollIntoView(
                    ViewModel.SelectedGame,
                    ScrollIntoViewAlignment.Leading);
            }
        });
    }

    private void RevealLatestImports()
    {
        _searchCancellation?.Cancel();
        SearchBox.Text = string.Empty;
        AllFilterButton.IsChecked = true;
        IwadFilterButton.IsChecked = false;
        ModsFilterButton.IsChecked = false;
        UnplayedFilterButton.IsChecked = false;
        foreach (var button in _collectionFilterButtons)
            button.IsChecked = false;
        RestoreActiveNavigationSelection();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SyncSelection();
            GameList.UpdateLayout();
            GameGrid.UpdateLayout();
            FindDescendant<ScrollViewer>(GameList)?.ChangeView(0, 0, null, true);
            FindDescendant<ScrollViewer>(GameGrid)?.ChangeView(0, 0, null, true);
        });
    }

    private async void IdGamesDownloadButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not Button { Tag: IdGamesItem item })
            return;
        if (item.IsDownloaded && item.LibraryGameFileId.HasValue)
        {
            var libraryItem = ViewModel.CatalogItems.FirstOrDefault(game =>
                game.GameFileId == item.LibraryGameFileId.Value);
            if (libraryItem is not null)
            {
                OpenOverviewGame(libraryItem);
                return;
            }
        }
        var originalFileName = await ViewModel.ResolveIdGamesArchiveFileNameAsync(
            item,
            _loadCancellation.Token);
        var conflict = string.IsNullOrWhiteSpace(originalFileName)
            ? null
            : await ViewModel.FindImportConflictAsync(
                originalFileName,
                _loadCancellation.Token);
        var resolution = conflict is null
            ? ImportFileConflictResolution.Fail
            : await AskImportConflictResolutionAsync(conflict.OriginalFileName);
        await ViewModel.DownloadIdGamesItemAsync(
            item,
            resolution,
            _loadCancellation.Token);
        SyncSelection();
    }

    private async void LoadMoreDiscoverButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        await ViewModel.LoadMoreDiscoverAsync(_loadCancellation.Token);
    }

    private void TileSizeSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        if (Resources[nameof(TileLayout)] is TileLayout layout)
            layout.SetWidth(args.NewValue);
    }


    private async void ManageCollectionsButton_Click(object sender, RoutedEventArgs args) =>
        await ShowManageCollectionsDialogAsync();

    private async void ReadMoreButton_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedGame.IsPlaceholder)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = ViewModel.SelectedGame.Title,
            Content = new ScrollViewer
            {
                MaxWidth = 680,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = ViewModel.SelectedGame.Description,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                },
            },
            CloseButtonText = Strings["Close"],
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
    }

    private async void FavoriteCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox checkBox
            || checkBox.Tag is not int gameFileId)
        {
            return;
        }

        var item = ViewModel.CatalogItems.FirstOrDefault(
            candidate => candidate.GameFileId == gameFileId);
        if (item is null || item.IsFavorite == (checkBox.IsChecked == true))
            return;
        ViewModel.SelectedGame = item;
        await ViewModel.ToggleSelectedFavoriteAsync(_loadCancellation.Token);
        _userState = await _app.UserLibraryStateStore.LoadAsync(
            _loadCancellation.Token);
        SyncSelection();
    }

    private async void NewCollectionButton_Click(
        object sender,
        RoutedEventArgs args) =>
        await ShowNewCollectionDialogAsync();

    private async Task ShowNewCollectionDialogAsync()
    {
        try
        {
            var editor = CreateNewCollectionEditor();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = Strings["NewCollection"],
                Content = editor.Content,
                PrimaryButtonText = Strings["Save"],
                CloseButtonText = Strings["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            editor.Name.TextChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled =
                    DatabaseTextSanitizer.SingleLine(editor.Name.Text).Length > 0;
            };
            ApplyDialogTheme(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var collectionName =
                DatabaseTextSanitizer.SingleLine(editor.Name.Text);
            await ViewModel.CreateCollectionAsync(
                collectionName,
                _loadCancellation.Token);
            if (editor.ShowAsFilter.IsChecked == true)
            {
                var state = await _app.UserLibraryStateStore.LoadAsync(
                    _loadCancellation.Token);
                _userState = state with
                {
                    LibraryFilterTags = state.LibraryFilterTags
                        .Append(collectionName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(
                            name => name,
                            StringComparer.CurrentCultureIgnoreCase)
                        .ToArray(),
                };
                await _app.UserLibraryStateStore.SaveAsync(
                    _userState,
                    _loadCancellation.Token);
                RebuildCollectionFilterButtons();
            }
            if (!string.IsNullOrWhiteSpace(editor.ArtworkPath))
            {
                await SaveCollectionArtworkAsync(
                    collectionName,
                    editor.ArtworkPath);
            }
            UpdateCollapseAllCollectionsState();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(
                Strings["CollectionsSaveFailed"],
                exception.Message);
        }
    }

    private NewCollectionEditor CreateNewCollectionEditor()
    {
        var name = new TextBox
        {
            Header = Strings["NewCollection"],
            PlaceholderText = Strings["NewCollectionPlaceholder"],
        };
        var showAsFilter = new CheckBox
        {
            Content = Strings["ShowNewCollectionAsFilter"],
        };
        ApplyGreenCheckBox(showAsFilter);
        var artworkSelection = new TextBlock
        {
            MaxWidth = 330,
            Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var chooseArtwork = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE91B", FontSize = 15 },
                    new TextBlock
                    {
                        Text = Strings["ChooseCollectionArtwork"],
                    },
                },
            },
        };
        AutomationProperties.SetName(
            chooseArtwork,
            Strings["ChooseCollectionArtwork"]);
        var content = new StackPanel
        {
            Width = 440,
            Spacing = 12,
            Children =
            {
                name,
                showAsFilter,
                chooseArtwork,
                artworkSelection,
            },
        };
        var editor = new NewCollectionEditor(
            content,
            name,
            showAsFilter);
        chooseArtwork.Click += async (_, _) =>
        {
            var selectedFiles = await PickImageFilesAsync(multiple: false);
            if (selectedFiles.Count == 0)
                return;
            editor.ArtworkPath = selectedFiles[0];
            artworkSelection.Text = Path.GetFileName(selectedFiles[0]);
        };
        return editor;
    }

    private async Task ShowManageCollectionsDialogAsync()
    {
        try
        {
            var data = await ViewModel.LoadSelectedCollectionsAsync(_loadCancellation.Token);
            var filter = new TextBox
            {
                PlaceholderText = Strings["FilterCollections"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(filter, Strings["FilterCollections"]);
            var entries = new StackPanel { Spacing = 2 };
            var selected = data.SelectedTagIds.ToHashSet();

            void RebuildEntries()
            {
                entries.Children.Clear();
                var query = filter.Text.Trim();
                foreach (var tag in data.Collections
                             .Where(tag => query.Length == 0
                                 || tag.Name.Contains(
                                     query,
                                     StringComparison.CurrentCultureIgnoreCase))
                             .OrderBy(
                                 tag => tag.Name,
                                 StringComparer.CurrentCultureIgnoreCase))
                {
                    var membership = new CheckBox
                    {
                        Content = tag.Name,
                        IsChecked = selected.Contains(tag.TagId),
                        Tag = tag.TagId,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    ApplyGreenCheckBox(membership);
                    membership.Checked += (_, _) => selected.Add(tag.TagId);
                    membership.Unchecked += (_, _) => selected.Remove(tag.TagId);
                    entries.Children.Add(membership);
                }
                if (entries.Children.Count == 0)
                {
                    entries.Children.Add(new TextBlock
                    {
                        Text = Strings["NoMatchingCollections"],
                        Margin = new Thickness(4, 10, 4, 10),
                    });
                }
            }

            filter.TextChanged += (_, _) => RebuildEntries();
            RebuildEntries();
            var content = new StackPanel
            {
                Width = 440,
                Spacing = 12,
                Children =
                {
                    filter,
                    new ScrollViewer
                    {
                        MaxHeight = 460,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = entries,
                    },
                },
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = Strings["ManageCollections"],
                Content = content,
                PrimaryButtonText = Strings["Save"],
                CloseButtonText = Strings["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            await ViewModel.SaveSelectedCollectionsAsync(
                selected,
                string.Empty,
                _loadCancellation.Token);
            RefreshSelectedCollectionGroup();
            SyncSelection();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["CollectionsSaveFailed"], exception.Message);
        }
    }

    private static void ApplyGreenCheckBox(CheckBox checkBox)
    {
        foreach (var key in new[]
                 {
                     "CheckBoxCheckBackgroundFillChecked",
                 })
        {
            checkBox.Resources[key] = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 108, 203, 95));
        }
        foreach (var key in new[]
                 {
                     "CheckBoxCheckBackgroundFillCheckedPointerOver",
                 })
        {
            checkBox.Resources[key] = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 108, 203, 95));
        }
        foreach (var key in new[]
                 {
                     "CheckBoxCheckBackgroundFillCheckedPressed",
                 })
        {
            checkBox.Resources[key] = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 108, 203, 95));
        }
    }

    private async Task ShowEditDialogAsync()
    {
        try
        {
            var game = await ViewModel.LoadSelectedGameForEditAsync(_loadCancellation.Token);
            var collections =
                await ViewModel.LoadSelectedCollectionsAsync(_loadCancellation.Token);
            var titleBox = new TextBox
            {
                Header = Strings.Title,
                Text = game.Title,
            };
            var authorBox = new TextBox
            {
                Header = Strings.Author,
                Text = game.Author,
            };
            var descriptionBox = new TextBox
            {
                Header = Strings["Description"],
                Text = game.Description,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120,
                MaxHeight = 240,
            };
            AutomationProperties.SetName(titleBox, Strings.Title);
            AutomationProperties.SetName(authorBox, Strings.Author);
            AutomationProperties.SetName(descriptionBox, Strings["Description"]);
            var sourcePortBox = CreateChoiceBox(Strings.SourcePort, game.SourcePorts, game.SourcePortId);
            var iwadBox = CreateChoiceBox(Strings.Iwad, game.Iwads, game.IwadId);
            var tagChecks = new List<(NativeTag Tag, CheckBox Box)>();
            var tagsPanel = new StackPanel { Spacing = 6 };
            foreach (var tag in collections.Collections)
            {
                var box = new CheckBox
                {
                    Content = tag.Name,
                    IsChecked = collections.SelectedTagIds.Contains(tag.TagId),
                };
                ApplyGreenCheckBox(box);
                tagChecks.Add((tag, box));
                tagsPanel.Children.Add(box);
            }
            var newTagBox = new TextBox
            {
                Header = Strings["NewCollection"],
                PlaceholderText = Strings["NewCollectionPlaceholder"],
                Margin = new Thickness(0, 8, 0, 0),
            };
            tagsPanel.Children.Add(newTagBox);
            var mediaChanged = false;
            var mediaPanel = new StackPanel { Spacing = 10 };
            var mediaStatus = new InfoBar
            {
                IsClosable = true,
                IsOpen = false,
            };
            async Task RunMediaActionAsync(Func<Task> action)
            {
                try
                {
                    await action();
                    mediaStatus.IsOpen = false;
                }
                catch (Exception exception)
                {
                    mediaStatus.Title = Strings["MediaSaveFailed"];
                    mediaStatus.Message = exception.Message;
                    mediaStatus.Severity = InfoBarSeverity.Error;
                    mediaStatus.IsOpen = true;
                }
            }
            async Task RefreshMediaAsync()
            {
                var media = await _app.NativeLibraryService.LoadGameMediaAsync(
                    game.GameFileId,
                    _loadCancellation.Token);
                mediaPanel.Children.Clear();
                mediaPanel.Children.Add(mediaStatus);
                mediaPanel.Children.Add(new TextBlock
                {
                    Text = Strings["TitleArtwork"],
                    FontFamily = (FontFamily)Application.Current.Resources[
                        "DoomHeadlineFontFamily"],
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                if (media.TitleArtwork is not null)
                {
                    mediaPanel.Children.Add(new Border
                    {
                        Width = 240,
                        Height = 180,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        CornerRadius = new CornerRadius(8),
                        Background = new ImageBrush
                        {
                            ImageSource = new BitmapImage(
                                new Uri(media.TitleArtwork.FullPath)),
                            Stretch = Stretch.UniformToFill,
                            AlignmentX = AlignmentX.Center,
                            AlignmentY = AlignmentY.Center,
                        },
                    });
                }
                else
                {
                    mediaPanel.Children.Add(new TextBlock
                    {
                        Text = Strings["NoTitleArtwork"],
                        Opacity = 0.68,
                    });
                }
                var mediaActions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                };
                var chooseTitle = new Button { Content = Strings["ChooseTitleArtwork"] };
                var removeTitle = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 7,
                        Children =
                        {
                            new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                            new TextBlock { Text = Strings["RemoveTitleArtwork"] },
                        },
                    },
                    IsEnabled = media.TitleArtwork is not null,
                };
                var addScreenshots = new Button { Content = Strings["AddScreenshots"] };
                chooseTitle.Click += async (_, _) =>
                {
                    try
                    {
                        var paths = await PickImageFilesAsync(multiple: false);
                        if (paths.Count == 0)
                            return;
                        await _app.NativeLibraryService.SetTitleArtworkAsync(
                            game.GameFileId,
                            paths[0],
                            _loadCancellation.Token);
                        mediaChanged = true;
                        mediaStatus.IsOpen = false;
                        await RefreshMediaAsync();
                    }
                    catch (Exception exception)
                    {
                        mediaStatus.Title = Strings["MediaSaveFailed"];
                        mediaStatus.Message = exception.Message;
                        mediaStatus.Severity = InfoBarSeverity.Error;
                        mediaStatus.IsOpen = true;
                    }
                };
                addScreenshots.Click += async (_, _) =>
                {
                    try
                    {
                        var paths = await PickImageFilesAsync(multiple: true);
                        if (paths.Count == 0)
                            return;
                        await _app.NativeLibraryService.AddScreenshotsAsync(
                            game.GameFileId,
                            paths,
                            _loadCancellation.Token);
                        mediaChanged = true;
                        mediaStatus.IsOpen = false;
                        await RefreshMediaAsync();
                    }
                    catch (Exception exception)
                    {
                        mediaStatus.Title = Strings["MediaSaveFailed"];
                        mediaStatus.Message = exception.Message;
                        mediaStatus.Severity = InfoBarSeverity.Error;
                        mediaStatus.IsOpen = true;
                    }
                };
                removeTitle.Click += async (_, _) => await RunMediaActionAsync(async () =>
                {
                    await _app.NativeLibraryService.RemoveTitleArtworkAsync(
                        game.GameFileId,
                        _loadCancellation.Token);
                    mediaChanged = true;
                    await RefreshMediaAsync();
                });
                mediaActions.Children.Add(chooseTitle);
                mediaActions.Children.Add(removeTitle);
                mediaActions.Children.Add(addScreenshots);
                mediaPanel.Children.Add(mediaActions);
                mediaPanel.Children.Add(new TextBlock
                {
                    Margin = new Thickness(0, 8, 0, 0),
                    Text = Strings["Screenshots"],
                    FontFamily = (FontFamily)Application.Current.Resources[
                        "DoomHeadlineFontFamily"],
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                if (media.Screenshots.Count == 0)
                {
                    mediaPanel.Children.Add(new TextBlock
                    {
                        Text = Strings["NoScreenshots"],
                        Opacity = 0.68,
                    });
                }
                for (var index = 0; index < media.Screenshots.Count; index++)
                {
                    var screenshot = media.Screenshots[index];
                    var screenshotIndex = index;
                    var row = new Grid
                    {
                        Padding = new Thickness(8),
                        ColumnSpacing = 10,
                        Background = (Brush)Application.Current.Resources[
                            "DoomSurfaceElevatedBrush"],
                        CornerRadius = new CornerRadius(8),
                    };
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(96),
                    });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto,
                    });
                    row.Children.Add(new Border
                    {
                        Width = 96,
                        Height = 72,
                        CornerRadius = new CornerRadius(6),
                        Background = new ImageBrush
                        {
                            ImageSource = new BitmapImage(new Uri(screenshot.FullPath)),
                            Stretch = Stretch.UniformToFill,
                            AlignmentX = AlignmentX.Center,
                            AlignmentY = AlignmentY.Center,
                        },
                    });
                    var name = new TextBlock
                    {
                        Text = screenshot.FileName,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Grid.SetColumn(name, 1);
                    row.Children.Add(name);
                    var controls = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var up = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE74A" },
                        IsEnabled = screenshotIndex > 0,
                    };
                    var down = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE74B" },
                        IsEnabled = screenshotIndex < media.Screenshots.Count - 1,
                    };
                    var asTitle = new Button
                    {
                        Content = Strings["UseAsTitleArtwork"],
                    };
                    var deleteScreenshot = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE74D" },
                    };
                    up.Click += async (_, _) => await RunMediaActionAsync(async () =>
                    {
                        var order = media.Screenshots.Select(item => item.FileId).ToList();
                        (order[screenshotIndex - 1], order[screenshotIndex]) =
                            (order[screenshotIndex], order[screenshotIndex - 1]);
                        await _app.NativeLibraryService.SetScreenshotOrderAsync(
                            game.GameFileId,
                            order,
                            _loadCancellation.Token);
                        mediaChanged = true;
                        await RefreshMediaAsync();
                    });
                    down.Click += async (_, _) => await RunMediaActionAsync(async () =>
                    {
                        var order = media.Screenshots.Select(item => item.FileId).ToList();
                        (order[screenshotIndex + 1], order[screenshotIndex]) =
                            (order[screenshotIndex], order[screenshotIndex + 1]);
                        await _app.NativeLibraryService.SetScreenshotOrderAsync(
                            game.GameFileId,
                            order,
                            _loadCancellation.Token);
                        mediaChanged = true;
                        await RefreshMediaAsync();
                    });
                    asTitle.Click += async (_, _) => await RunMediaActionAsync(async () =>
                    {
                        await _app.NativeLibraryService.SetScreenshotAsTitleArtworkAsync(
                            game.GameFileId,
                            screenshot.FileId,
                            _loadCancellation.Token);
                        mediaChanged = true;
                        await RefreshMediaAsync();
                    });
                    deleteScreenshot.Click += async (_, _) => await RunMediaActionAsync(
                        async () =>
                        {
                            await _app.NativeLibraryService.RemoveScreenshotAsync(
                                game.GameFileId,
                                screenshot.FileId,
                                _loadCancellation.Token);
                            mediaChanged = true;
                            await RefreshMediaAsync();
                        });
                    ToolTipService.SetToolTip(up, Strings["MoveUp"]);
                    ToolTipService.SetToolTip(down, Strings["MoveDown"]);
                    ToolTipService.SetToolTip(
                        deleteScreenshot,
                        Strings["DeleteScreenshot"]);
                    controls.Children.Add(up);
                    controls.Children.Add(down);
                    controls.Children.Add(asTitle);
                    controls.Children.Add(deleteScreenshot);
                    Grid.SetColumn(controls, 2);
                    row.Children.Add(controls);
                    mediaPanel.Children.Add(row);
                }
            }
            await RefreshMediaAsync();
            var validationText = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.IndianRed),
                Visibility = Visibility.Collapsed,
                Text = Strings["RequiredTitle"],
            };
            var content = new StackPanel
            {
                Width = 680,
                Spacing = 12,
            };
            content.Children.Add(new TextBlock
            {
                Text = game.FileName,
                Style = (Style)Application.Current.Resources["MetaTextStyle"],
            });
            content.Children.Add(titleBox);
            content.Children.Add(authorBox);
            content.Children.Add(descriptionBox);
            content.Children.Add(sourcePortBox);
            content.Children.Add(iwadBox);
            var collectionsExpander = new Expander
            {
                Header = Strings.Collections,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MinHeight = 160,
                    MaxHeight = 280,
                    Content = tagsPanel,
                },
            };
            content.Children.Add(collectionsExpander);
            content.Children.Add(new Expander
            {
                Header = Strings["Media"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MinHeight = 220,
                    MaxHeight = 430,
                    Content = mediaPanel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            });
            content.Children.Add(validationText);

            var improveMetadataButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Content = Strings["RescrapeIdGames"],
                IsEnabled = ViewModel.CanRefreshModMetadata,
            };
            ToolTipService.SetToolTip(
                improveMetadataButton,
                Strings["RescrapeIdGamesToolTip"]);
            AutomationProperties.SetHelpText(
                improveMetadataButton,
                Strings["RescrapeIdGamesToolTip"]);
            var requestRescrape = false;
            var dialogTitle = CreateDraggableDialogTitle(
                Strings["EditDialogTitle"],
                improveMetadataButton);
            dialogTitle.Width = 700;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = dialogTitle,
                Content = new ScrollViewer
                {
                    MaxHeight = 680,
                    Padding = new Thickness(0, 0, 18, 0),
                    Content = content,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = Strings["Save"],
                CloseButtonText = Strings["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(dialog);
            dialog.Resources["ContentDialogMinWidth"] = 740d;
            dialog.Resources["ContentDialogMaxWidth"] = 780d;
            improveMetadataButton.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    validationText.Visibility = Visibility.Visible;
                    titleBox.Focus(FocusState.Programmatic);
                    return;
                }
                requestRescrape = true;
                dialog.Hide();
            };
            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.Primary
                    || !string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    return;
                }

                args.Cancel = true;
                validationText.Visibility = Visibility.Visible;
                titleBox.Focus(FocusState.Programmatic);
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary && !requestRescrape)
            {
                if (mediaChanged)
                {
                    await ViewModel.RefreshAsync(_loadCancellation.Token);
                    SyncSelection();
                }
                return;
            }

            async Task SaveCurrentValuesAsync()
            {
                await ViewModel.SaveGameAsync(
                    game with
                    {
                        Title = titleBox.Text,
                        Author = authorBox.Text,
                        Description = descriptionBox.Text,
                        SourcePortId = (sourcePortBox.SelectedItem as NativeChoice)?.Id,
                        IwadId = (iwadBox.SelectedItem as NativeChoice)?.Id,
                    },
                    _loadCancellation.Token);
                await ViewModel.SaveSelectedCollectionsAsync(
                    tagChecks
                        .Where(item => item.Box.IsChecked == true)
                        .Select(item => item.Tag.TagId)
                        .ToHashSet(),
                    newTagBox.Text,
                    _loadCancellation.Token);
            }
            await SaveCurrentValuesAsync();
            if (requestRescrape)
            {
                await ShowIdGamesMetadataDialogAsync();
                await ShowEditDialogAsync();
                return;
            }
            SyncSelection();
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(_app.Localization.Get("EditFailed"), exception.Message);
        }
    }

    private async Task ShowMigrationDialogAsync(bool firstStart = false)
    {
        if (firstStart)
        {
            var welcome = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = Strings["MigrationFirstStart"],
                Content = new TextBlock
                {
                    Text = Strings["MigrationFirstStartPrompt"]
                        + Environment.NewLine
                        + Environment.NewLine
                        + Strings["MigrationPortableNotice"],
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                },
                PrimaryButtonText = Strings["Migrate"],
                CloseButtonText = Strings["Skip"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(welcome);
            if (await welcome.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.CommitButtonText = Strings["ChooseOriginalDoomLauncherFolder"];
        picker.FileTypeFilter.Add("*");
        if (_app.MainWindow is null)
            return;
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        if (!firstStart)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = EffectiveDialogTheme,
                Title = Strings["MigrationTitle"],
                Content = new TextBlock
                {
                    Text = Strings["MigrationWarning"]
                        + Environment.NewLine
                        + Environment.NewLine
                        + Strings["MigrationPortableNotice"],
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                },
                PrimaryButtonText = Strings["Migrate"],
                CloseButtonText = Strings["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
            };
            ApplyDialogTheme(confirm);
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        try
        {
            var result = await RunProgressDialogAsync(
                Strings["MigrationProgressTitle"],
                Strings["MigrationProgressStatus"],
                progress => _app.MigrationService.MigrateAsync(
                    folder.Path,
                    progress,
                    _loadCancellation.Token));
            await ViewModel.RefreshAsync(_loadCancellation.Token);
            SyncSelection();
            await ShowActionMessageAsync(
                Strings["MigrationComplete"],
                $"{result.CopiedFiles} {Strings["FilesCopied"]}\n{result.DatabasePath}",
                Strings["Close"]);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["MigrationFailed"], exception.Message);
        }
    }

    private async Task ShowFirstSetupWizardAsync()
    {
        var root = "Data";
        var overview = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = Strings["FirstSetupTitle"],
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings["FirstSetupIntro"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CreateSetupFolderHint(
                        "1",
                        $"{root}\\GameWads",
                        Strings["FirstSetupIwadsPlacement"]),
                    CreateSetupFolderHint(
                        "2",
                        $"{root}\\Sourceports\\<Sourceport>",
                        Strings["FirstSetupSourcePortsPlacement"]),
                    CreateSetupFolderHint(
                        "3",
                        $"{root}\\Mods",
                        Strings["FirstSetupModsPlacement"]),
                    CreateSetupFolderHint(
                        "4",
                        $"{root}\\Tools\\Obsidian",
                        Strings["FirstSetupObsidianPlacement"]),
                },
            },
            PrimaryButtonText = Strings["StartSetup"],
            CloseButtonText = Strings["Later"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(overview);
        overview.Resources["ContentDialogMinWidth"] = 680d;
        if (await overview.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!await ShowFirstSetupStepAsync(
                1,
                Strings["FirstSetupIwadsTitle"],
                Strings["FirstSetupIwadsHelp"],
                "Data\\GameWads",
                (cancellationToken, progress, _) =>
                    _app.FirstSetupService.ScanIwadsAsync(
                        cancellationToken,
                        progress)))
        {
            return;
        }
        if (!await ShowFirstSetupStepAsync(
                2,
                Strings["FirstSetupSourcePortsTitle"],
                Strings["FirstSetupSourcePortsHelp"],
                "Data\\Sourceports",
                (cancellationToken, progress, _) =>
                    _app.FirstSetupService.ScanSourcePortsAsync(
                        cancellationToken,
                        progress)))
        {
            return;
        }
        if (!await ShowFirstSetupStepAsync(
                3,
                Strings["FirstSetupModsTitle"],
                Strings["FirstSetupModsHelp"],
                "Data\\Mods",
                (cancellationToken, progress, decisions) =>
                    _app.FirstSetupService.ScanModsAsync(
                        cancellationToken,
                        progress,
                        decisions),
                inspectIwadsInMods: true))
        {
            return;
        }
        if (!await ShowFirstSetupStepAsync(
                4,
                Strings["FirstSetupObsidianTitle"],
                Strings["FirstSetupObsidianHelp"],
                "Data\\Tools\\Obsidian",
                (cancellationToken, progress, _) =>
                    _app.FirstSetupService.ScanObsidianAsync(
                        cancellationToken,
                        progress)))
        {
            return;
        }

        await _app.FirstSetupService.CompleteWizardAsync(_loadCancellation.Token);
        await ShowActionMessageAsync(
            Strings["FirstSetupComplete"],
            Strings["FirstSetupCompleteMessage"],
            Strings["Finish"]);
    }

    private async Task<bool> ShowFirstSetupStepAsync(
        int step,
        string title,
        string help,
        string directory,
        Func<
            CancellationToken,
            IProgress<double>?,
            IReadOnlyDictionary<string, IwadInModsAction>?,
            Task<SetupScanResult>> scan,
        bool inspectIwadsInMods = false)
    {
        var content = new StackPanel
        {
            Width = 600,
            Spacing = 14,
        };
        content.Children.Add(new TextBlock
        {
            Text = _app.Localization.Format("FirstSetupStep", step, 4),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["DoomAccentBrush"],
        });
        content.Children.Add(new TextBlock
        {
            Text = help,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(CreateSetupFolderHint(
            step.ToString(),
            directory,
            Strings["FirstSetupScanReady"]));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = title,
            Content = content,
            PrimaryButtonText = Strings["ScanAndContinue"],
            SecondaryButtonText = Strings["SkipStep"],
            CloseButtonText = Strings["Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyDialogTheme(dialog);
        dialog.Resources["ContentDialogMinWidth"] = 660d;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return false;
        if (result == ContentDialogResult.Secondary)
            return true;

        try
        {
            IReadOnlyDictionary<string, IwadInModsAction>? iwadDecisions = null;
            if (inspectIwadsInMods)
                iwadDecisions = await ResolveIwadsInModsAsync();
            var scanResult = await RunProgressDialogAsync(
                Strings["Scanning"],
                Strings["FirstSetupProgressStatus"],
                progress => scan(
                    _loadCancellation.Token,
                    progress,
                    iwadDecisions));
            var details = _app.Localization.Format(
                "FirstSetupScanResult",
                scanResult.Discovered,
                scanResult.Imported,
                scanResult.Updated,
                scanResult.Removed,
                scanResult.Skipped);
            if (scanResult.RemovedItems.Count > 0)
            {
                details += Environment.NewLine
                    + Environment.NewLine
                    + _app.Localization.Format(
                        "RemovedDefinitions",
                        string.Join(", ", scanResult.RemovedItems));
            }
            if (scanResult.Warnings.Count > 0)
            {
                details += Environment.NewLine
                    + Environment.NewLine
                    + _app.Localization.Format(
                        "FirstSetupWarnings",
                        scanResult.Warnings.Count)
                    + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        scanResult.Warnings.Take(5).Select(warning => $"• {warning}"));
            }
            await ShowActionMessageAsync(
                Strings["ScanComplete"],
                details,
                Strings["Next"]);
            return true;
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Strings["ScanFailed"], exception.Message);
            return false;
        }
    }

    private string FormatSetupScanResult(SetupScanResult scanResult)
    {
        var details = _app.Localization.Format(
            "FirstSetupScanResult",
            scanResult.Discovered,
            scanResult.Imported,
            scanResult.Updated,
            scanResult.Removed,
            scanResult.Skipped);
        if (scanResult.RemovedItems.Count > 0)
        {
            details += Environment.NewLine
                + Environment.NewLine
                + _app.Localization.Format(
                    "RemovedLibraryEntries",
                    string.Join(", ", scanResult.RemovedItems));
        }
        if (scanResult.Warnings.Count > 0)
        {
            details += Environment.NewLine
                + Environment.NewLine
                + _app.Localization.Format(
                    "FirstSetupWarnings",
                    scanResult.Warnings.Count)
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    scanResult.Warnings.Take(5).Select(warning => $"• {warning}"));
        }
        return details;
    }

    private static Border CreateSetupFolderHint(
        string step,
        string directory,
        string description)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(34),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["DoomAccentBrush"],
            Child = new TextBlock
            {
                Text = step,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            },
        };
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = directory,
            FontFamily = new FontFamily("Consolas"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources[
                "DoomSurfaceElevatedBrush"],
            Child = grid,
        };
    }

    private async Task<string?> PickDefinitionFolderAsync()
    {
        if (_app.MainWindow is null)
            return null;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task<string?> PickDefinitionFileAsync()
    {
        if (_app.MainWindow is null)
            return null;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<IReadOnlyList<string>> PickImageFilesAsync(bool multiple)
    {
        if (_app.MainWindow is null)
            return [];
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_app.MainWindow));
        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files.Select(file => file.Path).ToArray();
        }
        var file = await picker.PickSingleFileAsync();
        return file is null ? [] : [file.Path];
    }

    private string ConvertToPortableReference(
        string path,
        ISet<string> acknowledgedExternalPaths,
        InfoBar portableWarning)
    {
        var fullPath = Path.GetFullPath(path);
        var root = GetPortableRoot();
        if (IsInsideDirectory(fullPath, root))
        {
            return Path.GetRelativePath(root, fullPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        portableWarning.Message = _app.Localization.Format(
            "ExternalPathWarning",
            fullPath,
            root);
        portableWarning.IsOpen = true;
        acknowledgedExternalPaths.Add(fullPath);
        return fullPath;
    }

    private async Task WarnIfExternalReferenceAsync(
        string reference,
        ISet<string> acknowledgedExternalPaths)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return;
        var root = GetPortableRoot();
        var fullPath = Path.GetFullPath(
            Path.IsPathFullyQualified(reference)
                ? reference
                : Path.Combine(root, reference));
        if (IsInsideDirectory(fullPath, root)
            || acknowledgedExternalPaths.Contains(fullPath))
        {
            return;
        }

        await ShowExternalPathWarningAsync(fullPath, root);
        acknowledgedExternalPaths.Add(fullPath);
    }

    private async Task WarnIfExternalReferencesAsync(
        string references,
        ISet<string> acknowledgedExternalPaths)
    {
        foreach (var reference in references.Split(
                     [';', ','],
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            await WarnIfExternalReferenceAsync(
                reference,
                acknowledgedExternalPaths);
        }
    }

    private async Task ShowExternalPathWarningAsync(
        string path,
        string portableRoot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = EffectiveDialogTheme,
            Title = Strings["ExternalPathTitle"],
            Content = new TextBlock
            {
                MaxWidth = 560,
                Text = _app.Localization.Format(
                    "ExternalPathWarning",
                    path,
                    portableRoot),
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = Strings["Continue"],
        };
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
    }

    private static string GetPortableRoot()
    {
        var configuredDatabase = Environment.GetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDatabase))
        {
            return Path.GetDirectoryName(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configuredDatabase.Trim().Trim('"'))))!;
        }

        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        return string.Equals(
            Path.GetFileName(applicationDirectory),
            "WinUI",
            StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(applicationDirectory)?.FullName
                ?? applicationDirectory
            : applicationDirectory;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return fullPath.Equals(
                fullDirectory,
                StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static ToggleButton CreateDefinitionTabButton(
        string header,
        string glyph)
    {
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        headerPanel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new ToggleButton
        {
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Content = headerPanel,
        };
        AutomationProperties.SetName(button, header);
        return button;
    }

    private static DataTemplate CreateSourcePortDefinitionTemplate() =>
        (DataTemplate)XamlReader.Load(
            """
            <DataTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel Orientation="Horizontal" Spacing="0">
                    <TextBlock Text="{Binding Name}" />
                    <TextBlock Opacity="0.5" Text="{Binding VersionSuffix}" />
                </StackPanel>
            </DataTemplate>
            """);

    private static Button CreateDefinitionActionButton(
        string text,
        string glyph)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 14,
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static Grid CreateDefinitionTabContent(
        string help,
        ListView definitionList,
        params UIElement[] sections)
    {
        var editorPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(4, 4, 12, 8),
        };
        editorPanel.Children.Add(new TextBlock
        {
            Text = help,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var section in sections)
            editorPanel.Children.Add(section);

        var editorScroll = new ScrollViewer
        {
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = editorPanel,
        };
        definitionList.VerticalAlignment = VerticalAlignment.Stretch;
        var listHost = new Border
        {
            MinWidth = 250,
            Margin = new Thickness(4),
            Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources[
                "DoomSurfaceElevatedBrush"],
            BorderBrush = (Brush)Application.Current.Resources[
                "DoomStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = definitionList,
        };
        var layout = new Grid
        {
            ColumnSpacing = 12,
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(270),
        });
        Grid.SetColumn(listHost, 1);
        layout.Children.Add(editorScroll);
        layout.Children.Add(listHost);
        return layout;
    }

    private static Flyout CreateDefinitionDeleteFlyout(
        TextBlock warning,
        CheckBox deleteFiles,
        Button confirmButton)
    {
        var content = new StackPanel
        {
            Spacing = 12,
        };
        content.Children.Add(warning);
        content.Children.Add(deleteFiles);
        content.Children.Add(confirmButton);
        return new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            Content = content,
        };
    }

    private static Border CreateDefinitionSection(
        string header,
        params UIElement[] children)
    {
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(new TextBlock
        {
            Text = header,
            FontFamily = (FontFamily)Application.Current.Resources[
                "DoomHeadlineFontFamily"],
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var child in children)
            fields.Children.Add(child);

        return new Border
        {
            Padding = new Thickness(14),
            Background = (Brush)Application.Current.Resources[
                "DoomSurfaceElevatedBrush"],
            BorderBrush = (Brush)Application.Current.Resources[
                "DoomStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = fields,
        };
    }

    private Grid CreateDraggableDialogTitle(
        string title,
        FrameworkElement? trailingContent = null)
    {
        var titleText = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.Resources[
                "DoomHeadlineFontFamily"],
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var dragRegion = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = titleText,
        };
        dragRegion.PointerPressed += DialogTitle_PointerPressed;

        var grid = new Grid
        {
            MinWidth = 420,
            ColumnSpacing = 16,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        grid.Children.Add(dragRegion);
        if (trailingContent is not null)
        {
            Grid.SetColumn(trailingContent, 1);
            grid.Children.Add(trailingContent);
        }
        return grid;
    }

    private void DialogTitle_PointerPressed(
        object sender,
        PointerRoutedEventArgs args)
    {
        if (_app.MainWindow is null
            || sender is not UIElement element
            || !args.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            return;
        }
        ReleaseCapture();
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(
            _app.MainWindow);
        _ = SendMessage(
            windowHandle,
            WmNcLeftButtonDown,
            new IntPtr(HitTestCaption),
            IntPtr.Zero);
        args.Handled = true;
    }

    private void ApplyDialogTheme(ContentDialog dialog)
    {
        dialog.Opened += (_, _) => { _activeThemedDialog=dialog; FitDialogToViewport(dialog); };
        dialog.Closed += (_, _) => { if(ReferenceEquals(_activeThemedDialog,dialog)) _activeThemedDialog=null; };
        if (dialog.Title is string title)
            dialog.Title = CreateDraggableDialogTitle(title);
        dialog.RequestedTheme = EffectiveDialogTheme;
        var resources = Application.Current.Resources;
        foreach (var key in new[]
                 {
                     "AccentFillColorDefaultBrush",
                     "AccentFillColorSecondaryBrush",
                     "AccentFillColorTertiaryBrush",
                     "AccentTextFillColorPrimaryBrush",
                     "TextOnAccentFillColorPrimaryBrush",
                     "TextOnAccentFillColorSecondaryBrush",
                     "TextOnAccentFillColorDisabledBrush",
                     "AccentButtonBackground",
                     "AccentButtonBackgroundPointerOver",
                     "AccentButtonBackgroundPressed",
                     "AccentButtonForeground",
                     "AccentButtonForegroundPointerOver",
                     "AccentButtonForegroundPressed",
                     "ToggleButtonBackgroundChecked",
                     "ToggleButtonBackgroundCheckedPointerOver",
                     "ToggleButtonBackgroundCheckedPressed",
                     "ToggleButtonForegroundChecked",
                     "ToggleButtonForegroundCheckedPointerOver",
                     "ToggleButtonForegroundCheckedPressed",
                     "AppBarToggleButtonForegroundChecked",
                     "AppBarToggleButtonForegroundCheckedPointerOver",
                     "AppBarToggleButtonForegroundCheckedPressed",
                     "ToggleSwitchFillOn",
                     "ToggleSwitchFillOnPointerOver",
                     "ToggleSwitchFillOnPressed",
                     "CheckBoxCheckBackgroundFillChecked",
                     "CheckBoxCheckBackgroundFillCheckedPointerOver",
                     "CheckBoxCheckBackgroundFillCheckedPressed",
                     "CheckBoxCheckGlyphForegroundChecked",
                     "CheckBoxCheckGlyphForegroundCheckedPointerOver",
                     "CheckBoxCheckGlyphForegroundCheckedPressed",
                     "ComboBoxItemSelectedBackgroundThemeBrush",
                     "ComboBoxItemSelectedPointerOverBackgroundThemeBrush",
                     "ComboBoxItemSelectedForegroundThemeBrush",
                     "ComboBoxItemBackgroundSelected",
                     "ComboBoxItemBackgroundSelectedUnfocused",
                     "ComboBoxItemBackgroundSelectedPointerOver",
                     "ComboBoxItemBackgroundSelectedPressed",
                     "ComboBoxItemBorderBrushSelected",
                     "ComboBoxItemBorderBrushSelectedPointerOver",
                     "ComboBoxItemPillFillBrush",
                     "AccentFillColorSelectedTextBackgroundBrush",
                     "TextControlSelectionHighlightColor",
                     "TextSelectionHighlightColorThemeBrush",
                 })
        {
            if (resources.TryGetValue(key, out var value))
                dialog.Resources[key] = value;
        }

        if (resources.TryGetValue("DoomSurfaceBrush", out var background)
            && background is SolidColorBrush surface)
        {
            var color = surface.Color;
            dialog.Resources["ContentDialogBackground"] = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, color.R, color.G, color.B));
        }
        if (resources.TryGetValue("DoomStrokeBrush", out var border))
            dialog.Resources["ContentDialogBorderBrush"] = border;
    }

    private const uint WmNcLeftButtonDown = 0x00A1;
    private const int HitTestCaption = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private sealed class NewCollectionEditor(
        StackPanel content,
        TextBox name,
        CheckBox showAsFilter)
    {
        public StackPanel Content { get; } = content;
        public TextBox Name { get; } = name;
        public CheckBox ShowAsFilter { get; } = showAsFilter;
        public string? ArtworkPath { get; set; }
    }

    private sealed record DefinitionOption(string Code, string Label);
}
