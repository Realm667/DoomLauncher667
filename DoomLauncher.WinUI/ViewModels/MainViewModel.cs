using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.Modern.Core.Launch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DoomLauncher.WinUI.ViewModels;

public sealed class MainViewModel(
    ILibraryCatalog libraryCatalog,
    ILaunchOptionsCatalog launchOptionsCatalog,
    ILaunchService launchService,
    INativeLibraryService nativeLibraryService,
    IIdGamesService idGamesService,
    IUserLibraryStateStore userLibraryStateStore,
    UiLocalization localization) : INotifyPropertyChanged
{
    private const int DiscoverPageSize = 20;

    private IReadOnlyList<LibraryItem> _catalog = [];
    private LibraryItem _selectedGame = LibraryItem.Empty;
    private LaunchOptionChoice? _selectedSourcePortOption;
    private LaunchOptionChoice? _selectedIwadOption;
    private LaunchValueChoice? _selectedMapOption;
    private LaunchValueChoice? _selectedSkillOption;
    private string _searchText = string.Empty;
    private string _launchStatus = string.Empty;
    private string _launchErrorMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _librarySummary = localization.Get("Loading");
    private string _librarySourceName = string.Empty;
    private bool _isLoading;
    private bool _isLaunching;
    private bool _isGameRunning;
    private bool _isLaunchOptionsLoading;
    private bool _isInitialized;
    private bool _isDiscoverLoaded;
    private bool _isDiscoverLoading;
    private int? _launchOptionsGameFileId;
    private HashSet<int> _favoriteGameFileIds = [];
    private HashSet<int> _finishedGameFileIds = [];
    private IReadOnlyList<LibraryGroup> _homeGroups = [];
    private IReadOnlyList<LibraryGroup> _homeIwadGroups = [];
    private IReadOnlyList<LibraryGroup> _collectionGroups = [];
    private IReadOnlySet<string> _collectionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _collectionArtworkPaths =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private LibraryItem _homeHero = LibraryItem.Empty;
    private IReadOnlyList<LibraryItem> _homeSpotlights = [];
    private string _idGamesStatus = string.Empty;
    private LibrarySection _activeSection = LibrarySection.Library;
    private LibraryCategoryFilter _categoryFilter = LibraryCategoryFilter.All;
    private string? _collectionFilterTag;
    private LibrarySortOrder _sortOrder = LibrarySortOrder.Title;
    private bool _sortDescending;
    private LibrarySortOrder _collectionSortOrder = LibrarySortOrder.Title;
    private bool _collectionSortDescending;
    private int _filteredCount;
    private int _homeItemsPerGroup = 10;
    private IReadOnlySet<string> _configuredIwads =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IdGamesItem> _discoverResults = [];
    private string _discoverQuery = string.Empty;
    private int _discoverLimit = DiscoverPageSize;
    private bool _hasMoreDiscover;
    private HashSet<string> _testedThemes =
        new(StringComparer.OrdinalIgnoreCase) { "Dark" };
    private int _originalIwadLaunches;
    private int _importedCollectionCount;
    private bool _achievementNotificationsInitialized;
    private HashSet<string> _notifiedAchievementKeys =
        new(StringComparer.Ordinal);
    private HashSet<string> _unseenAchievementKeys =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _achievementStateGate = new(1, 1);
    private LibraryStatisticsData _statistics =
        new(0, 0, 0, 0, 0, [], new SessionStatisticsData(0, 0, 0, 0, 0, 0, 0, 0));

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AchievementItem>? AchievementUnlocked;

    public ObservableCollection<LibraryItem> Games { get; private set; } = [];
    public ObservableCollection<LaunchOptionChoice> SourcePortOptions { get; } = [];
    public ObservableCollection<LaunchOptionChoice> IwadOptions { get; } = [];
    public ObservableCollection<LaunchValueChoice> MapOptions { get; } = [];
    public ObservableCollection<LaunchValueChoice> SkillOptions { get; } = [];
    public ObservableCollection<IdGamesItem> DiscoverItems { get; } = [];
    public ObservableCollection<AchievementItem> Achievements { get; } = [];
    public ObservableCollection<AchievementGroup> AchievementGroups { get; } = [];
    public ObservableCollection<StatisticCardItem> AchievementSummary { get; } = [];
    public IReadOnlyList<LibraryItem> CatalogItems => _catalog;
    public IReadOnlyList<IwadLibraryStatistic> IwadStatistics => _statistics.ByIwad;

    public LibraryItem SelectedGame
    {
        get => _selectedGame;
        set
        {
            value ??= LibraryItem.Empty;
            if (ReferenceEquals(_selectedGame, value))
                return;

            _selectedGame = value;
            ResetLaunchOptions();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(IsSelectedFavorite));
            OnPropertyChanged(nameof(FavoriteButtonText));
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(FinishedButtonText));
            OnPropertyChanged(nameof(FinishedGlyph));
        }
    }

    public LaunchOptionChoice? SelectedSourcePortOption
    {
        get => _selectedSourcePortOption;
        set
        {
            if (ReferenceEquals(_selectedSourcePortOption, value))
                return;

            _selectedSourcePortOption = value;
            OnPropertyChanged();
        }
    }

    public LaunchOptionChoice? SelectedIwadOption
    {
        get => _selectedIwadOption;
        set
        {
            if (ReferenceEquals(_selectedIwadOption, value))
                return;

            _selectedIwadOption = value;
            OnPropertyChanged();
        }
    }

    public string LaunchStatus
    {
        get => _launchStatus;
        private set
        {
            if (_launchStatus == value)
                return;

            _launchStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLaunchStatus));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
                return;

            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        }
    }

    public string LaunchErrorMessage
    {
        get => _launchErrorMessage;
        private set
        {
            if (_launchErrorMessage == value)
                return;

            _launchErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLaunchError));
        }
    }

    public string LibrarySummary
    {
        get => _librarySummary;
        private set
        {
            if (_librarySummary == value)
                return;

            _librarySummary = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
                return;

            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(LoadingVisibility));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        }
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (_isLaunching == value)
                return;

            _isLaunching = value;
            OnPropertyChanged();
            NotifyLaunchStateChanged();
        }
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value)
                return;

            _isGameRunning = value;
            OnPropertyChanged();
            NotifyLaunchStateChanged();
        }
    }

    public bool IsLaunchOptionsLoading
    {
        get => _isLaunchOptionsLoading;
        private set
        {
            if (_isLaunchOptionsLoading == value)
                return;

            _isLaunchOptionsLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLaunchWithOptions));
            OnPropertyChanged(nameof(LaunchOptionsLoadingVisibility));
        }
    }

    public bool HasLaunchStatus => !string.IsNullOrWhiteSpace(LaunchStatus);
    public bool HasLaunchError => !string.IsNullOrWhiteSpace(LaunchErrorMessage);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public void DismissLaunchStatus()
    {
        LaunchStatus = string.Empty;
    }

    public bool IsEmpty => _isInitialized && !IsLoading && !HasError && _filteredCount == 0;
    public bool CanLaunch => !SelectedGame.IsPlaceholder && !IsLaunching && !IsGameRunning;
    public bool IsSelectedFavorite =>
        !SelectedGame.IsPlaceholder && _favoriteGameFileIds.Contains(SelectedGame.GameFileId);
    public string FavoriteButtonText =>
        IsSelectedFavorite ? localization.Get("RemoveFavorite") : localization.Get("AddFavorite");
    public string FavoriteGlyph => IsSelectedFavorite ? "\uE735" : "\uE734";
    public string FinishedButtonText => SelectedGame.IsFinished
        ? localization.Get("MarkUnfinished")
        : localization.Get("MarkFinished");
    public string FinishedGlyph => SelectedGame.IsFinished ? "\uE73E" : "\uE739";
    public string SearchPlaceholder => localization.Get(
        _activeSection == LibrarySection.Discover ? "IdGamesSearch" : "Search");
    public LibrarySection ActiveSection => _activeSection;
    public IReadOnlyList<LibraryGroup> HomeGroups
    {
        get => _homeGroups;
        private set
        {
            _homeGroups = value;
            OnPropertyChanged();
        }
    }

    public LaunchValueChoice? SelectedMapOption
    {
        get => _selectedMapOption;
        set
        {
            if (ReferenceEquals(_selectedMapOption, value))
                return;
            _selectedMapOption = value;
            OnPropertyChanged();
        }
    }

    public LaunchValueChoice? SelectedSkillOption
    {
        get => _selectedSkillOption;
        set
        {
            if (ReferenceEquals(_selectedSkillOption, value))
                return;
            _selectedSkillOption = value;
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<LibraryGroup> HomeIwadGroups
    {
        get => _homeIwadGroups;
        private set
        {
            _homeIwadGroups = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HomeIwadGroupsVisibility));
        }
    }
    public LibraryItem HomeHero
    {
        get => _homeHero;
        private set
        {
            _homeHero = value;
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<LibraryItem> HomeSpotlights
    {
        get => _homeSpotlights;
        private set
        {
            _homeSpotlights = value;
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<LibraryGroup> CollectionGroups
    {
        get => _collectionGroups;
        private set
        {
            _collectionGroups = value;
            OnPropertyChanged();
        }
    }
    public string IdGamesStatus
    {
        get => _idGamesStatus;
        private set
        {
            if (_idGamesStatus == value)
                return;
            _idGamesStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIdGamesStatus));
        }
    }
    public bool HasIdGamesStatus => !string.IsNullOrWhiteSpace(IdGamesStatus);
    public Visibility HomeIwadGroupsVisibility =>
        HomeIwadGroups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string TotalEntriesValue => _catalog.Count.ToString("N0");
    public string TotalMapsValue => _catalog.Sum(game => game.MapCount).ToString("N0");
    public string TotalPlayedHoursValue =>
        (_catalog.Sum(game => game.MinutesPlayed) / 60d).ToString("N1");
    public string PlayedEntriesValue => _statistics.Played.ToString("N0");
    public string UnplayedEntriesValue => _statistics.Unplayed.ToString("N0");
    public string FinishedEntriesValue => _statistics.Finished.ToString("N0");
    public int UnseenAchievementCount => _unseenAchievementKeys.Count;
    public Visibility AchievementBadgeVisibility =>
        UnseenAchievementCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDiscoverLoading
    {
        get => _isDiscoverLoading;
        private set
        {
            if (_isDiscoverLoading == value)
                return;
            _isDiscoverLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiscoverLoadingVisibility));
            OnPropertyChanged(nameof(CanLoadMoreDiscover));
        }
    }
    public Visibility HomeVisibility =>
        _activeSection == LibrarySection.Home ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiscoverVisibility =>
        _activeSection == LibrarySection.Discover ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CollectionsVisibility =>
        _activeSection == LibrarySection.Collections ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AchievementsVisibility =>
        _activeSection == LibrarySection.Achievements ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DebugVisibility =>
        _activeSection == LibrarySection.Debug ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibraryVisibility =>
        _activeSection is LibrarySection.Home
            or LibrarySection.Discover
            or LibrarySection.Collections
            or LibrarySection.Achievements
            or LibrarySection.Debug
            ? Visibility.Collapsed
            : Visibility.Visible;
    public Visibility DiscoverLoadingVisibility =>
        IsDiscoverLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadMoreDiscoverVisibility =>
        _hasMoreDiscover ? Visibility.Visible : Visibility.Collapsed;
    public bool CanLoadMoreDiscover => _hasMoreDiscover && !IsDiscoverLoading;
    public string SectionTitle => _activeSection switch
    {
        LibrarySection.Home => localization.Get("Home"),
        LibrarySection.Favorites => localization.Get("Favorites"),
        LibrarySection.Recent => localization.Get("Recent"),
        LibrarySection.Downloads => localization.Get("Downloads"),
        LibrarySection.Discover => localization.Get("Discover"),
        LibrarySection.Collections => localization.Get("Collections"),
        LibrarySection.Achievements => localization.Get("Achievements"),
        LibrarySection.Debug => localization.Get("Debug"),
        _ => localization.Get("MyLibrary"),
    };
    public string EmptyStateMessage => !string.IsNullOrWhiteSpace(_searchText)
        ? localization.Get("AdjustSearch")
        : _activeSection switch
        {
            LibrarySection.Favorites => localization.Get("FavoritesEmpty"),
            LibrarySection.Recent => localization.Get("RecentEmpty"),
            LibrarySection.Downloads => localization.Get("DownloadsEmpty"),
            LibrarySection.Collections => localization.Get("CollectionsEmpty"),
            LibrarySection.Discover => localization.Get("DiscoverEmpty"),
            _ => localization.Get("LibraryEmpty"),
        };
    public bool CanLaunchWithOptions =>
        CanLaunch && !IsLaunchOptionsLoading;
    public string LaunchButtonText => IsLaunching
        ? localization.Get("Launching")
        : IsGameRunning
            ? localization.Get("GameRunning")
            : localization.Get("Play");
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LaunchingVisibility =>
        IsLaunching || IsGameRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LaunchOptionsLoadingVisibility =>
        IsLaunchOptionsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStateVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public LibrarySortOrder SortOrder => _sortOrder;
    public bool SortDescending => _sortDescending;
    public LibrarySortOrder CollectionSortOrder => _collectionSortOrder;
    public bool CollectionSortDescending => _collectionSortDescending;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadCatalogAsync(forceRefresh: false, preferredGameFileId: null, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        int? preferredGameFileId = SelectedGame.IsPlaceholder
            ? null
            : SelectedGame.GameFileId;
        return LoadCatalogAsync(
            forceRefresh: true,
            preferredGameFileId,
            cancellationToken);
    }

    public void Filter(string searchText)
    {
        _searchText = searchText.Trim();
        OnPropertyChanged(nameof(EmptyStateMessage));
        ApplyFilter();
    }

    public void SetSection(LibrarySection section)
    {
        if (_activeSection == section)
            return;

        _activeSection = section;
        if (section == LibrarySection.Achievements)
            MarkAchievementNotificationsSeen();
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(ActiveSection));
        OnPropertyChanged(nameof(HomeVisibility));
        OnPropertyChanged(nameof(DiscoverVisibility));
        OnPropertyChanged(nameof(CollectionsVisibility));
        OnPropertyChanged(nameof(AchievementsVisibility));
        OnPropertyChanged(nameof(DebugVisibility));
        OnPropertyChanged(nameof(LibraryVisibility));
        OnPropertyChanged(nameof(SearchPlaceholder));

        if (section == LibrarySection.Home)
            RebuildHomeGroups();
        else if (section == LibrarySection.Collections)
            RebuildCollectionGroups();
        else if (section != LibrarySection.Discover)
            ApplyFilter();
        else
            UpdateSectionSummary();
    }

    public Task<DatabaseHealthReport> CheckDatabaseHealthAsync(
        bool repair,
        CancellationToken cancellationToken = default) =>
        nativeLibraryService.CheckDatabaseHealthAsync(repair, cancellationToken);

    public async Task ExportPortableBundleAsync(
        IReadOnlyCollection<int> gameFileIds,
        string destinationPath,
        bool includeGeneralMetadata,
        bool includePersonalMetadata,
        bool includeScreenshots,
        bool includeTitleArtwork,
        bool includeCollections,
        CancellationToken cancellationToken = default)
    {
        var state = await userLibraryStateStore.LoadAsync(cancellationToken);
        await nativeLibraryService.ExportPortableBundleAsync(
            gameFileIds,
            destinationPath,
            new PortableBundleExportOptions(
                includeGeneralMetadata,
                includePersonalMetadata,
                includeScreenshots,
                includeTitleArtwork,
                includeCollections,
                state.FavoriteGameFileIds,
                state.CollectionArtworkPaths,
                new HashSet<string>(
                    state.LibraryFilterTags,
                    StringComparer.OrdinalIgnoreCase)),
            cancellationToken);
    }

    public async Task<PortableBundleImportResult> ImportPortableBundleAsync(
        string sourcePath,
        PortableBundleImportOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await nativeLibraryService.ImportPortableBundleAsync(
            sourcePath,
            options,
            cancellationToken);
        if (result.Collections.Count > 0
            || result.FavoriteGameFileIds.Count > 0
            || result.CollectionArtworkPaths.Count > 0
            || result.LibraryFilterCollections.Count > 0)
        {
            var state = await userLibraryStateStore.LoadAsync(cancellationToken);
            _importedCollectionCount = result.Collections.Count > 0
                ? state.ImportedCollectionCount + 1
                : state.ImportedCollectionCount;
            var favorites = new HashSet<int>(state.FavoriteGameFileIds);
            favorites.UnionWith(result.FavoriteGameFileIds);
            var artworkPaths = new Dictionary<string, string>(
                state.CollectionArtworkPaths,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in result.CollectionArtworkPaths)
                artworkPaths[pair.Key] = pair.Value;
            var libraryFilters = new HashSet<string>(
                state.LibraryFilterTags,
                StringComparer.OrdinalIgnoreCase);
            libraryFilters.UnionWith(result.LibraryFilterCollections);
            await userLibraryStateStore.SaveAsync(
                state with
                {
                    ImportedCollectionCount = _importedCollectionCount,
                    FavoriteGameFileIds = favorites,
                    CollectionArtworkPaths = artworkPaths,
                    LibraryFilterTags = libraryFilters
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                },
                cancellationToken);
        }
        await LoadCatalogAsync(true, null, cancellationToken);
        return result;
    }

    public Task<PortableBundleInspection> InspectPortableBundleAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        nativeLibraryService.InspectPortableBundleAsync(
            sourcePath,
            cancellationToken);

    public async Task DeleteCollectionAsync(
        int tagId,
        CancellationToken cancellationToken = default)
    {
        await nativeLibraryService.DeleteCollectionAsync(tagId, cancellationToken);
        await LoadCatalogAsync(
            true,
            SelectedGame.IsPlaceholder ? null : SelectedGame.GameFileId,
            cancellationToken);
    }

    public async Task CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await nativeLibraryService.CreateCollectionAsync(name, cancellationToken);
        await LoadCatalogAsync(
            true,
            SelectedGame.IsPlaceholder ? null : SelectedGame.GameFileId,
            cancellationToken);
    }

    public void SetCategoryFilter(LibraryCategoryFilter filter)
    {
        if (_categoryFilter == filter && _collectionFilterTag is null)
            return;

        _collectionFilterTag = null;
        _categoryFilter = filter;
        ApplyFilter();
    }

    public void SelectGame(int gameFileId)
    {
        var game = Games.FirstOrDefault(item => item.GameFileId == gameFileId)
            ?? _catalog.FirstOrDefault(item => item.GameFileId == gameFileId);
        if (game is not null)
            SelectedGame = game;
    }

    public void SetCollectionFilter(string? tag)
    {
        var normalized = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        if (string.Equals(_collectionFilterTag, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _collectionFilterTag = normalized;
        _categoryFilter = LibraryCategoryFilter.All;
        ApplyFilter();
    }

    public void SetCollectionArtworkPaths(
        IReadOnlyDictionary<string, string> artworkPaths)
    {
        _collectionArtworkPaths = new Dictionary<string, string>(
            artworkPaths,
            StringComparer.OrdinalIgnoreCase);
        RebuildCollectionGroups();
    }

    public void SetSortOrder(LibrarySortOrder sortOrder, bool toggleDirection = false)
    {
        if (toggleDirection && _sortOrder == sortOrder)
            _sortDescending = !_sortDescending;
        else if (_sortOrder == sortOrder && !_sortDescending)
            return;
        else
        {
            _sortOrder = sortOrder;
            _sortDescending = false;
        }
        ApplyFilter();
    }

    public void SetCollectionSortOrder(
        LibrarySortOrder sortOrder,
        bool toggleDirection = false)
    {
        if (toggleDirection && _collectionSortOrder == sortOrder)
            _collectionSortDescending = !_collectionSortDescending;
        else if (_collectionSortOrder == sortOrder
                 && !_collectionSortDescending)
            return;
        else
        {
            _collectionSortOrder = sortOrder;
            _collectionSortDescending = false;
        }
        RebuildCollectionGroups();
    }

    public async Task SetFinishedAsync(
        int gameFileId,
        bool isFinished,
        CancellationToken cancellationToken = default)
    {
        if (isFinished)
            _finishedGameFileIds.Add(gameFileId);
        else
            _finishedGameFileIds.Remove(gameFileId);

        var item = _catalog.FirstOrDefault(game => game.GameFileId == gameFileId);
        if (item is not null)
            item.IsFinished = isFinished;

        await nativeLibraryService.SetGameFinishedAsync(
            gameFileId,
            isFinished,
            cancellationToken);
        _statistics = _statistics with
        {
            Finished = Math.Max(0, _statistics.Finished + (isFinished ? 1 : -1)),
        };
        OnPropertyChanged(nameof(FinishedButtonText));
        OnPropertyChanged(nameof(FinishedGlyph));
        RebuildHomeGroups();
        RebuildCollectionGroups();
        RebuildAchievements();
        NotifyStatisticsChanged();
        if (_sortOrder == LibrarySortOrder.Finished)
            ApplyFilter(gameFileId);
    }

    public Task ToggleSelectedFinishedAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            return Task.CompletedTask;
        return SetFinishedAsync(
            SelectedGame.GameFileId,
            !SelectedGame.IsFinished,
            cancellationToken);
    }

    public Task<GameCollectionsData> LoadSelectedCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            throw new InvalidOperationException(localization.Get("SelectEntry"));
        return nativeLibraryService.LoadGameCollectionsAsync(
            SelectedGame.GameFileId,
            cancellationToken);
    }

    public async Task SaveSelectedCollectionsAsync(
        IReadOnlySet<int> tagIds,
        string? newCollectionName,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            return;
        var gameFileId = SelectedGame.GameFileId;
        await nativeLibraryService.SaveGameCollectionsAsync(
            gameFileId,
            tagIds,
            newCollectionName,
            cancellationToken);
        await LoadCatalogAsync(true, gameFileId, cancellationToken);
    }

    public async Task AddGamesToCollectionAsync(
        string collectionName,
        IReadOnlySet<int> gameFileIds,
        CancellationToken cancellationToken = default)
    {
        await nativeLibraryService.AddGamesToCollectionAsync(
            collectionName,
            gameFileIds,
            cancellationToken);
        int? selectedId = SelectedGame.IsPlaceholder
            ? null
            : SelectedGame.GameFileId;
        await LoadCatalogAsync(true, selectedId, cancellationToken);
    }

    public async Task EnsureDiscoverLoadedAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isDiscoverLoaded || IsDiscoverLoading)
            return;
        await LoadDiscoverAsync(null, cancellationToken);
    }

    public Task SearchDiscoverAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        LoadDiscoverAsync(query, cancellationToken);

    public async Task LoadMoreDiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanLoadMoreDiscover)
            return;

        IsDiscoverLoading = true;
        try
        {
            _discoverLimit = Math.Min(_discoverLimit + DiscoverPageSize, 100);
            if (string.IsNullOrWhiteSpace(_discoverQuery))
            {
                _discoverResults = await idGamesService.GetLatestAsync(
                    _discoverLimit,
                    cancellationToken);
                MarkDownloadedItems(_discoverResults);
            }
            ReplaceItems(
                DiscoverItems,
                _discoverResults.Take(_discoverLimit));
            _hasMoreDiscover =
                _discoverResults.Count >= _discoverLimit
                && _discoverLimit < 100;
            NotifyDiscoverPagingChanged();
            IdGamesStatus = localization.Format(
                "IdGamesResults",
                DiscoverItems.Count);
            UpdateSectionSummary();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            IdGamesStatus = localization.Format(
                "IdGamesLoadFailed",
                GetInnermostMessage(exception));
        }
        finally
        {
            IsDiscoverLoading = false;
        }
    }

    public async Task DownloadIdGamesItemAsync(
        IdGamesItem item,
        ImportFileConflictResolution conflictResolution = ImportFileConflictResolution.Fail,
        CancellationToken cancellationToken = default)
    {
        if (!item.CanDownload)
            return;

        string? temporaryDirectory = null;
        string? temporaryPath = null;
        item.IsDownloading = true;
        IdGamesStatus = localization.Format("IdGamesDownloading", item.Title);
        try
        {
            var downloadItem = string.IsNullOrWhiteSpace(item.FileName)
                ? await idGamesService.GetByIdAsync(item.Id, cancellationToken)
                : item;
            if (downloadItem is null
                || string.IsNullOrWhiteSpace(downloadItem.FileName))
            {
                throw new InvalidOperationException(
                    $"No downloadable /idgames archive was returned for ID {item.Id}.");
            }
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"DoomLauncher-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            temporaryPath = Path.Combine(
                temporaryDirectory,
                Path.GetFileName(downloadItem.FileName));
            var progress = new Progress<double>(value =>
            {
                IdGamesStatus = localization.Format(
                    "IdGamesDownloadProgress",
                    item.Title,
                    value);
            });
            await idGamesService.DownloadAsync(
                downloadItem,
                temporaryPath,
                progress,
                cancellationToken);
            var imported = await nativeLibraryService.ImportIdGamesAsync(
                downloadItem,
                temporaryPath,
                conflictResolution,
                cancellationToken);
            if (imported.WasSkipped)
            {
                IdGamesStatus = localization.Get("ImportSkipped");
                return;
            }
            try
            {
                await nativeLibraryService.TryImportTitlePicAsync(
                    imported.GameFileId,
                    temporaryPath,
                    cancellationToken);
            }
            catch
            {
                // Artwork is optional; a valid /idgames import must remain usable.
            }
            item.IsDownloaded = true;
            item.LibraryGameFileId = imported.GameFileId;
            item.ActionText = localization.Get("OpenInLibrary");
            IdGamesStatus = localization.Format("IdGamesDownloaded", item.Title);
            await LoadCatalogAsync(
                forceRefresh: true,
                imported.GameFileId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IdGamesStatus = string.Empty;
        }
        catch (Exception exception)
        {
            IdGamesStatus = localization.Format(
                "IdGamesDownloadFailed",
                GetInnermostMessage(exception));
        }
        finally
        {
            item.IsDownloading = false;
            if (!string.IsNullOrWhiteSpace(temporaryDirectory)
                && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    public Task<NativeImportConflict?> FindImportConflictAsync(
        string originalFileName,
        CancellationToken cancellationToken = default) =>
        nativeLibraryService.FindImportConflictAsync(
            originalFileName,
            cancellationToken);

    public async Task<string?> ResolveIdGamesArchiveFileNameAsync(
        IdGamesItem item,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(item.FileName))
            return Path.GetFileName(item.FileName);
        var resolved = await idGamesService.GetByIdAsync(item.Id, cancellationToken);
        return string.IsNullOrWhiteSpace(resolved?.FileName)
            ? null
            : Path.GetFileName(resolved.FileName);
    }

    private async Task LoadDiscoverAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        IsDiscoverLoading = true;
        _discoverQuery = query?.Trim() ?? string.Empty;
        _discoverLimit = DiscoverPageSize;
        IdGamesStatus = localization.Get("IdGamesConnecting");
        try
        {
            _discoverResults = string.IsNullOrWhiteSpace(_discoverQuery)
                ? await idGamesService.GetLatestAsync(
                    DiscoverPageSize,
                    cancellationToken)
                : await idGamesService.SearchAsync(
                    _discoverQuery,
                    cancellationToken);
            MarkDownloadedItems(_discoverResults);
            ReplaceItems(
                DiscoverItems,
                _discoverResults.Take(DiscoverPageSize));
            _hasMoreDiscover = _discoverResults.Count > DiscoverPageSize
                || (string.IsNullOrWhiteSpace(_discoverQuery)
                    && _discoverResults.Count == DiscoverPageSize);
            NotifyDiscoverPagingChanged();
            _isDiscoverLoaded = string.IsNullOrWhiteSpace(_discoverQuery);
            IdGamesStatus = DiscoverItems.Count == 0
                ? localization.Get("IdGamesNoResults")
                : localization.Format("IdGamesResults", DiscoverItems.Count);
            UpdateSectionSummary();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            IdGamesStatus = localization.Format(
                "IdGamesLoadFailed",
                GetInnermostMessage(exception));
        }
        finally
        {
            IsDiscoverLoading = false;
        }
    }

    private void MarkDownloadedItems(IEnumerable<IdGamesItem> items)
    {
        var localById = _catalog
            .Where(game => game.IdGamesId.HasValue)
            .GroupBy(game => game.IdGamesId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var localByFileName = _catalog
            .GroupBy(
                game => Path.GetFileName(game.FileName),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var local = localById.GetValueOrDefault(item.Id);
            if (local is null
                && !string.IsNullOrWhiteSpace(item.FileName))
            {
                localByFileName.TryGetValue(
                    Path.GetFileName(item.FileName),
                    out local);
            }
            item.LibraryGameFileId = local?.GameFileId;
            item.IsDownloaded = local is not null;
            item.ActionText = localization.Get(
                item.IsDownloaded ? "OpenInLibrary" : "Download");
        }
    }

    private void NotifyDiscoverPagingChanged()
    {
        OnPropertyChanged(nameof(LoadMoreDiscoverVisibility));
        OnPropertyChanged(nameof(CanLoadMoreDiscover));
    }

    public async Task ToggleSelectedFavoriteAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            return;

        var gameFileId = SelectedGame.GameFileId;
        var wasFavorite = _favoriteGameFileIds.Contains(gameFileId);
        if (wasFavorite)
            _favoriteGameFileIds.Remove(gameFileId);
        else
            _favoriteGameFileIds.Add(gameFileId);
        var item = _catalog.FirstOrDefault(game => game.GameFileId == gameFileId);
        if (item is not null)
            item.IsFavorite = !wasFavorite;

        OnPropertyChanged(nameof(IsSelectedFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
        OnPropertyChanged(nameof(FavoriteGlyph));

        try
        {
            var currentState = await userLibraryStateStore.LoadAsync(cancellationToken);
            await userLibraryStateStore.SaveAsync(
                currentState with { FavoriteGameFileIds = new HashSet<int>(_favoriteGameFileIds) },
                cancellationToken);
        }
        catch (Exception exception)
        {
            if (wasFavorite)
                _favoriteGameFileIds.Add(gameFileId);
            else
                _favoriteGameFileIds.Remove(gameFileId);
            if (item is not null)
                item.IsFavorite = wasFavorite;

            OnPropertyChanged(nameof(IsSelectedFavorite));
            OnPropertyChanged(nameof(FavoriteButtonText));
            OnPropertyChanged(nameof(FavoriteGlyph));
            LaunchErrorMessage = localization.Format("FavoriteSaveFailed", exception.Message);
            return;
        }

        if (_activeSection == LibrarySection.Favorites)
            ApplyFilter();
        RebuildHomeGroups();
        RebuildAchievements();
    }

    public Task ImportFileAsync(
        string filePath,
        ImportFileConflictResolution conflictResolution,
        CancellationToken cancellationToken = default)
    {
        return ImportNativeFileAsync(filePath, conflictResolution, cancellationToken);
    }

    public Task<GameEditData> LoadSelectedGameForEditAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            throw new InvalidOperationException(localization.Get("SelectEntry"));
        return nativeLibraryService.LoadGameAsync(SelectedGame.GameFileId, cancellationToken);
    }

    public async Task SaveGameAsync(
        GameEditData game,
        CancellationToken cancellationToken = default)
    {
        await nativeLibraryService.UpdateGameAsync(game, cancellationToken);
        LaunchErrorMessage = string.Empty;
        LaunchStatus = localization.Format("GameSaved", game.Title);
        await LoadCatalogAsync(true, game.GameFileId, cancellationToken);
    }

    public Task<IReadOnlyList<IdGamesItem>> FindSelectedIdGamesMatchesAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            throw new InvalidOperationException(localization.Get("SelectEntry"));
        return idGamesService.FindMatchesAsync(
            SelectedGame.FileName,
            SelectedGame.Title,
            cancellationToken);
    }

    public async Task ApplySelectedIdGamesMetadataAsync(
        IdGamesItem item,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder)
            throw new InvalidOperationException(localization.Get("SelectEntry"));
        var gameFileId = SelectedGame.GameFileId;
        var artworkImported = await ApplyIdGamesMetadataAsync(
            gameFileId,
            item,
            downloadRemoteArtwork: true,
            progress,
            cancellationToken);
        LaunchErrorMessage = string.Empty;
        LaunchStatus = localization.Format(
            artworkImported
                ? "IdGamesMetadataUpdatedArtwork"
                : "IdGamesMetadataUpdated",
            item.Title);
        await LoadCatalogAsync(true, gameFileId, cancellationToken);
    }

    public async Task<IdGamesBulkRefreshResult> RefreshLinkedIdGamesMetadataAsync(
        IProgress<IdGamesBulkRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var linkedItems = _catalog
            .Where(item => item.IdGamesId.HasValue)
            .GroupBy(item => item.GameFileId)
            .Select(group => group.First())
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var updated = 0;
        var artworkImported = 0;
        var failed = 0;
        for (var index = 0; index < linkedItems.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = linkedItems[index];
            progress?.Report(new IdGamesBulkRefreshProgress(
                index,
                linkedItems.Length,
                game.Title));
            try
            {
                var item = await idGamesService.RefreshByIdAsync(
                    game.IdGamesId!.Value,
                    cancellationToken);
                if (item is null)
                {
                    failed++;
                    continue;
                }
                if (await ApplyIdGamesMetadataAsync(
                        game.GameFileId,
                        item,
                        downloadRemoteArtwork: false,
                        progress: null,
                        cancellationToken))
                {
                    artworkImported++;
                }
                updated++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed++;
            }
        }
        progress?.Report(new IdGamesBulkRefreshProgress(
            linkedItems.Length,
            linkedItems.Length,
            string.Empty));
        int? selectedId = SelectedGame.IsPlaceholder
            ? null
            : SelectedGame.GameFileId;
        await LoadCatalogAsync(true, selectedId, cancellationToken);
        return new IdGamesBulkRefreshResult(
            linkedItems.Length,
            updated,
            artworkImported,
            failed);
    }

    private async Task<bool> ApplyIdGamesMetadataAsync(
        int gameFileId,
        IdGamesItem item,
        bool downloadRemoteArtwork,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(0.08);
        await nativeLibraryService.UpdateGameFromIdGamesAsync(
            gameFileId,
            item,
            cancellationToken);
        progress?.Report(0.55);
        var artworkImported = false;
        try
        {
            var localArchive =
                await nativeLibraryService.ResolveManagedGameFileAsync(
                    gameFileId,
                    cancellationToken);
            if (!string.IsNullOrWhiteSpace(localArchive)
                && File.Exists(localArchive))
            {
                artworkImported =
                    await nativeLibraryService.TryImportTitlePicAsync(
                        gameFileId,
                        localArchive,
                        cancellationToken);
                progress?.Report(0.78);
            }
            if (!artworkImported && downloadRemoteArtwork)
            {
                var temporaryPath = Path.Combine(
                    Path.GetTempPath(),
                    $"DoomLauncher-artwork-{Guid.NewGuid():N}-{Path.GetFileName(item.FileName)}");
                try
                {
                    await idGamesService.DownloadAsync(
                        item,
                        temporaryPath,
                        progress: null,
                        cancellationToken);
                    artworkImported =
                        await nativeLibraryService.TryImportTitlePicAsync(
                            gameFileId,
                            temporaryPath,
                            cancellationToken);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
        }
        catch
        {
            // Metadata remains authoritative even if optional artwork is absent
            // or the remote archive cannot be inspected.
        }
        progress?.Report(1.0);
        return artworkImported;
    }

    public Task<LauncherSettingsData> LoadNativeSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        return nativeLibraryService.LoadSettingsAsync(cancellationToken);
    }

    public Task RefreshLocalizationAsync(CancellationToken cancellationToken = default)
    {
        NotifyLocalizationChanged();
        RebuildHomeGroups();
        RebuildCollectionGroups();
        NotifyStatisticsChanged();
        return RefreshAsync(cancellationToken);
    }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(FavoriteButtonText));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(FinishedButtonText));
        OnPropertyChanged(nameof(SearchPlaceholder));
    }

    public async Task SaveNativeSettingsAsync(
        LauncherSettingsData settings,
        CancellationToken cancellationToken = default)
    {
        await nativeLibraryService.UpdateSettingsAsync(settings, cancellationToken);
        _homeItemsPerGroup = Math.Clamp(settings.HomeItemsPerGroup, 1, 20);
        LaunchErrorMessage = string.Empty;
        LaunchStatus = localization.Get("SettingsSaved");
        await LoadCatalogAsync(true, SelectedGame.IsPlaceholder ? null : SelectedGame.GameFileId, cancellationToken);
    }

    private async Task ImportNativeFileAsync(
        string filePath,
        ImportFileConflictResolution conflictResolution,
        CancellationToken cancellationToken)
    {
        LaunchStatus = string.Empty;
        LaunchErrorMessage = string.Empty;
        try
        {
            var result = await nativeLibraryService.ImportAsync(
                filePath,
                conflictResolution,
                cancellationToken);
            if (result.WasSkipped)
            {
                LaunchStatus = localization.Get("ImportSkipped");
                return;
            }
            LaunchStatus = localization.Format("NativeImported", result.FileName);
            await LoadCatalogAsync(true, result.GameFileId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            LaunchErrorMessage = localization.Format("ImportFailed", exception.Message);
        }
    }

    public async Task LaunchSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLaunch)
            return;

        var selectedGame = SelectedGame;
        await LaunchAsync(
            selectedGame,
            new GameLaunchRequest(
                selectedGame.GameFileId,
                selectedGame.Title,
                Map: string.Empty,
                Skill: string.Empty),
            cancellationToken);
    }

    public async Task LoadLaunchOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame.IsPlaceholder
            || IsLaunchOptionsLoading
            || _launchOptionsGameFileId == SelectedGame.GameFileId)
        {
            return;
        }

        var gameFileId = SelectedGame.GameFileId;
        IsLaunchOptionsLoading = true;
        LaunchErrorMessage = string.Empty;

        try
        {
            var result = await launchOptionsCatalog.LoadAsync(gameFileId, cancellationToken);
            if (SelectedGame.GameFileId != gameFileId)
                return;

            ReplaceItems(SourcePortOptions, result.SourcePorts);
            ReplaceItems(IwadOptions, result.Iwads);
            ReplaceItems(MapOptions, result.Maps);
            ReplaceItems(SkillOptions, result.Skills);
            _launchOptionsGameFileId = gameFileId;
            SelectedSourcePortOption = SourcePortOptions.FirstOrDefault();
            SelectedIwadOption = IwadOptions.FirstOrDefault();
            SelectedMapOption = MapOptions.FirstOrDefault();
            SelectedSkillOption = SkillOptions.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            LaunchErrorMessage = localization.Format("OptionsLoadFailed", exception.Message);
        }
        finally
        {
            IsLaunchOptionsLoading = false;
        }
    }

    public async Task LaunchSelectedWithOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanLaunchWithOptions)
            return;

        var selectedGame = SelectedGame;
        await LaunchAsync(
            selectedGame,
            new GameLaunchRequest(
                selectedGame.GameFileId,
                selectedGame.Title,
                SelectedSourcePortOption?.Id,
                SelectedIwadOption?.Id,
                SelectedMapOption?.Value,
                SelectedSkillOption?.Value),
            cancellationToken);
    }

    private async Task LaunchAsync(
        LibraryItem selectedGame,
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        LaunchStatus = string.Empty;
        LaunchErrorMessage = string.Empty;
        IsLaunching = true;

        try
        {
            var result = await launchService.LaunchAsync(request, cancellationToken);

            if (string.Equals(
                    selectedGame.Category,
                    "IWAD",
                    StringComparison.OrdinalIgnoreCase))
            {
                var state = await userLibraryStateStore.LoadAsync(cancellationToken);
                _originalIwadLaunches = state.OriginalIwadLaunches + 1;
                await userLibraryStateStore.SaveAsync(
                    state with { OriginalIwadLaunches = _originalIwadLaunches },
                    cancellationToken);
            }

            IsLaunching = false;
            IsGameRunning = true;
            LaunchStatus = localization.Format(
                "ProcessRunning",
                result.Message,
                result.Session.ProcessId);

            await result.Session.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var preferredGameFileId = SelectedGame.IsPlaceholder
                ? selectedGame.GameFileId
                : SelectedGame.GameFileId;
            var refreshed = await LoadCatalogAsync(
                forceRefresh: true,
                preferredGameFileId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            LaunchStatus = refreshed
                ? localization.Format("GameEndedUpdated", selectedGame.Title)
                : localization.Format("GameEndedRefreshFailed", selectedGame.Title);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            LaunchStatus = string.Empty;
            LaunchErrorMessage = exception.Message;
        }
        finally
        {
            IsLaunching = false;
            IsGameRunning = false;
        }
    }

    private async Task<bool> LoadCatalogAsync(
        bool forceRefresh,
        int? preferredGameFileId,
        CancellationToken cancellationToken)
    {
        if ((!forceRefresh && _isInitialized) || IsLoading)
            return true;

        var previousCatalog = _catalog;
        var previousSummary = LibrarySummary;
        var hadLoadedCatalog = _isInitialized;
        var loadSucceeded = false;

        IsLoading = true;
        ErrorMessage = string.Empty;
        if (forceRefresh)
            LibrarySummary = localization.Get("Updating");

        try
        {
            var userState = await userLibraryStateStore.LoadAsync(cancellationToken);
            _collectionArtworkPaths = new Dictionary<string, string>(
                userState.CollectionArtworkPaths,
                StringComparer.OrdinalIgnoreCase);
            _testedThemes = new HashSet<string>(
                userState.TestedThemes,
                StringComparer.OrdinalIgnoreCase);
            _originalIwadLaunches = userState.OriginalIwadLaunches;
            _importedCollectionCount = userState.ImportedCollectionCount;
            if (!_isInitialized)
            {
                _achievementNotificationsInitialized =
                    userState.AchievementNotificationsInitialized;
                _notifiedAchievementKeys = new HashSet<string>(
                    userState.NotifiedAchievementKeys,
                    StringComparer.Ordinal);
                _unseenAchievementKeys = new HashSet<string>(
                    userState.UnseenAchievementKeys,
                    StringComparer.Ordinal);
                NotifyAchievementBadgeChanged();
                _favoriteGameFileIds = new HashSet<int>(userState.FavoriteGameFileIds);
                _finishedGameFileIds = new HashSet<int>(userState.FinishedGameFileIds);
                await nativeLibraryService.MigrateFinishedStateAsync(
                    _finishedGameFileIds,
                    cancellationToken);
                if (_finishedGameFileIds.Count > 0)
                {
                    await userLibraryStateStore.SaveAsync(
                        userState with { FinishedGameFileIds = new HashSet<int>() },
                        cancellationToken);
                }
            }

            LibraryItem.ClearArtworkCache();
            var result = await libraryCatalog.LoadAsync(cancellationToken);
            _catalog = result.Entries
                .Select(entry => new LibraryItem(
                    entry,
                    localization,
                    _finishedGameFileIds.Contains(entry.GameFileId),
                    _favoriteGameFileIds.Contains(entry.GameFileId)))
                .ToArray();
            _finishedGameFileIds = _catalog
                .Where(game => game.IsFinished)
                .Select(game => game.GameFileId)
                .ToHashSet();
            _librarySourceName = Path.GetFileName(result.Source);
            _homeItemsPerGroup = result.HomeItemsPerGroup;
            _configuredIwads = result.ConfiguredIwads;
            _collectionNames = result.CollectionNames;
            ApplyFilter(preferredGameFileId);
            RebuildHomeGroups();
            RebuildCollectionGroups();
            _statistics = await nativeLibraryService.LoadStatisticsAsync(cancellationToken);
            RebuildAchievements();
            NotifyStatisticsChanged();
            _isInitialized = true;
            loadSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (forceRefresh)
                LibrarySummary = previousSummary;
        }
        catch (Exception exception)
        {
            if (hadLoadedCatalog)
            {
                _catalog = previousCatalog;
                LibrarySummary = previousSummary;
                ErrorMessage = localization.Format("LibraryRefreshFailed", exception.Message);
            }
            else
            {
                _catalog = [];
                Games = [];
                OnPropertyChanged(nameof(Games));
                SelectedGame = LibraryItem.Empty;
                ErrorMessage = exception.Message;
                LibrarySummary = localization.Get("NotAvailable");
                _isInitialized = true;
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        }

        return loadSucceeded;
    }

    private void ApplyFilter(int? preferredGameFileId = null)
    {
        var currentGameFileId = preferredGameFileId
            ?? (SelectedGame.IsPlaceholder ? null : SelectedGame.GameFileId);
        IEnumerable<LibraryItem> matches = _catalog;
        matches = _activeSection switch
        {
            LibrarySection.Favorites => matches.Where(
                game => _favoriteGameFileIds.Contains(game.GameFileId)),
            LibrarySection.Recent => matches
                .Where(game => game.LastPlayedAt.HasValue),
            LibrarySection.Downloads => matches.Where(
                game => game.IsDownloaded && game.Category == "Mod"),
            _ => matches,
        };
        matches = _categoryFilter switch
        {
            LibraryCategoryFilter.Iwads => matches.Where(game => game.Category == "IWAD"),
            LibraryCategoryFilter.Mods => matches.Where(game => game.Category == "Mod"),
            LibraryCategoryFilter.Unplayed => matches.Where(game => game.MinutesPlayed == 0),
            _ => matches,
        };
        if (_collectionFilterTag is not null)
        {
            matches = matches.Where(game => game.Tags.Contains(
                _collectionFilterTag,
                StringComparer.OrdinalIgnoreCase));
        }
        matches = string.IsNullOrWhiteSpace(_searchText)
            ? matches
            : matches
                .Where(game =>
                    game.Title.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase)
                    || game.Subtitle.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase)
                    || game.Category.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase)
                    || game.Tags.Any(tag =>
                        tag.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase))
                    || game.FileName.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();

        matches = _activeSection switch
        {
            LibrarySection.Recent => matches
                .OrderByDescending(game => game.LastPlayedAt)
                .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
            LibrarySection.Downloads => matches
                .OrderByDescending(game => game.DownloadedAt)
                .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => ApplySelectedSort(matches),
        };
        var filteredGames = matches.ToArray();
        _filteredCount = filteredGames.Length;
        Games = new ObservableCollection<LibraryItem>(filteredGames);
        OnPropertyChanged(nameof(Games));

        UpdateSectionSummary();

        SelectedGame = filteredGames.FirstOrDefault(game => game.GameFileId == currentGameFileId)
            ?? filteredGames.FirstOrDefault()
            ?? LibraryItem.Empty;

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    private IOrderedEnumerable<LibraryItem> ApplySelectedSort(
        IEnumerable<LibraryItem> matches) =>
        _sortOrder switch
        {
            LibrarySortOrder.Author => Order(
                matches, game => game.Author, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortOrder.ReleaseDate => Order(matches, game => game.ReleaseDateAt),
            LibrarySortOrder.Maps => Order(matches, game => game.MapCount),
            LibrarySortOrder.Rating => Order(matches, game => game.RatingValue),
            LibrarySortOrder.Downloaded => Order(matches, game => game.DownloadedAt),
            LibrarySortOrder.SourcePort => Order(
                matches, game => game.SourcePort, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortOrder.Finished => Order(matches, game => game.IsFinished),
            LibrarySortOrder.LastPlayed => Order(matches, game => game.LastPlayedAt),
            LibrarySortOrder.Playtime => Order(matches, game => game.MinutesPlayed),
            LibrarySortOrder.Year => Order(
                matches, game => game.Year, StringComparer.CurrentCultureIgnoreCase),
            _ => Order(matches, game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        };

    private void RebuildHomeGroups()
    {
        var mods = _catalog.Where(game => game.Category == "Mod").ToArray();
        var randomUnplayed = mods
            .Where(game => game.MinutesPlayed == 0)
            .OrderBy(_ => Random.Shared.Next())
            .Take(_homeItemsPerGroup)
            .ToArray();
        var newest = mods
            .Where(game => game.ReleaseDateAt.HasValue)
            .OrderByDescending(game => game.ReleaseDateAt)
            .Take(_homeItemsPerGroup)
            .ToArray();
        var favorites = mods
            .Where(game => _favoriteGameFileIds.Contains(game.GameFileId))
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(_homeItemsPerGroup)
            .ToArray();
        var showcase = favorites
            .Concat(newest)
            .Concat(randomUnplayed)
            .DistinctBy(game => game.GameFileId)
            .Take(1)
            .ToArray();
        HomeHero = showcase.FirstOrDefault() ?? LibraryItem.Empty;
        HomeSpotlights = mods
            .Where(game => game.GameFileId != HomeHero.GameFileId)
            .OrderBy(_ => Random.Shared.Next())
            .Take(2)
            .ToArray();

        var groups = new List<LibraryGroup>
        {
            new(localization.Get("RandomUnplayed"), randomUnplayed),
            new(localization.Get("NewestMods"), newest),
        };
        if (favorites.Length > 0)
            groups.Add(new LibraryGroup(localization.Get("FavoriteMods"), favorites));
        HomeGroups = groups.Where(group => group.Items.Count > 0).ToArray();

        HomeIwadGroups = mods
            .Where(game => !string.IsNullOrWhiteSpace(game.Iwad)
                && _configuredIwads.Contains(game.Iwad))
            .GroupBy(game => game.Iwad, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new LibraryGroup(
                localization.Format("ModsForIwad", group.Key),
                group.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Take(_homeItemsPerGroup)
                    .ToArray()))
            .Where(group => group.Items.Count > 0)
            .ToArray();
        UpdateSectionSummary();
    }

    private void RebuildCollectionGroups()
    {
        var assignedNames = _catalog.SelectMany(game => game.Tags);
        CollectionGroups = _collectionNames
            .Concat(assignedNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
            {
                var items = ApplyCollectionSort(_catalog
                        .Where(game => game.Tags.Contains(
                            name,
                            StringComparer.OrdinalIgnoreCase)))
                    .ToArray();
                return new LibraryGroup(
                    name,
                    items,
                    localization.Format(
                        "CollectionProgressTooltip",
                        items.Count(item => item.IsFinished),
                        items.Length),
                    LoadCollectionArtwork(name));
            })
            .ToArray();
        UpdateSectionSummary();
    }

    private IOrderedEnumerable<LibraryItem> ApplyCollectionSort(
        IEnumerable<LibraryItem> matches) =>
        _collectionSortOrder switch
        {
            LibrarySortOrder.Author => OrderCollection(
                matches, game => game.Author, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortOrder.ReleaseDate => OrderCollection(
                matches, game => game.ReleaseDateAt),
            LibrarySortOrder.Maps => OrderCollection(matches, game => game.MapCount),
            LibrarySortOrder.Rating => OrderCollection(
                matches, game => game.RatingValue),
            LibrarySortOrder.Downloaded => OrderCollection(
                matches, game => game.DownloadedAt),
            LibrarySortOrder.SourcePort => OrderCollection(
                matches, game => game.SourcePort,
                StringComparer.CurrentCultureIgnoreCase),
            LibrarySortOrder.Finished => OrderCollection(
                matches, game => game.IsFinished),
            LibrarySortOrder.LastPlayed => OrderCollection(
                matches, game => game.LastPlayedAt),
            LibrarySortOrder.Playtime => OrderCollection(
                matches, game => game.MinutesPlayed),
            LibrarySortOrder.Year => OrderCollection(
                matches, game => game.Year, StringComparer.CurrentCultureIgnoreCase),
            _ => OrderCollection(
                matches, game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        };

    private BitmapImage? LoadCollectionArtwork(string collectionName)
    {
        if (!_collectionArtworkPaths.TryGetValue(collectionName, out var reference)
            || string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        try
        {
            var root = GetPortableRoot();
            var fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(reference)
                    ? reference
                    : Path.Combine(root, reference));
            if (!File.Exists(fullPath))
                return null;

            return new BitmapImage
            {
                DecodePixelWidth = 1200,
                UriSource = new Uri(fullPath),
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return null;
        }
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

    private IOrderedEnumerable<LibraryItem> Order<TKey>(
        IEnumerable<LibraryItem> source,
        Func<LibraryItem, TKey> keySelector,
        IComparer<TKey>? comparer = null)
    {
        var ordered = _sortDescending
            ? source.OrderByDescending(keySelector, comparer)
            : source.OrderBy(keySelector, comparer);
        return ordered.ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase);
    }

    private IOrderedEnumerable<LibraryItem> OrderCollection<TKey>(
        IEnumerable<LibraryItem> source,
        Func<LibraryItem, TKey> keySelector,
        IComparer<TKey>? comparer = null)
    {
        var ordered = _collectionSortDescending
            ? source.OrderByDescending(keySelector, comparer)
            : source.OrderBy(keySelector, comparer);
        return ordered.ThenBy(
            game => game.Title,
            StringComparer.CurrentCultureIgnoreCase);
    }

    private void NotifyLaunchStateChanged()
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanLaunchWithOptions));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(LaunchingVisibility));
    }

    private void NotifyStatisticsChanged()
    {
        OnPropertyChanged(nameof(TotalEntriesValue));
        OnPropertyChanged(nameof(TotalMapsValue));
        OnPropertyChanged(nameof(TotalPlayedHoursValue));
        OnPropertyChanged(nameof(PlayedEntriesValue));
        OnPropertyChanged(nameof(UnplayedEntriesValue));
        OnPropertyChanged(nameof(FinishedEntriesValue));
        OnPropertyChanged(nameof(IwadStatistics));
    }

    private void RebuildAchievements()
    {
        var sessions = _statistics.Sessions;
        var all = new List<AchievementItem>();
        var groups = new List<AchievementGroup>();
        void AddGroup(
            string key,
            string title,
            params AchievementItem[] items)
        {
            var keyedItems = items
                .Select((item, index) => item with
                {
                    Key = $"{key}.{index + 1}",
                })
                .ToArray();
            groups.Add(new AchievementGroup(title, keyedItems));
            all.AddRange(keyedItems);
        }
        AchievementItem Tier(
            string titleKey,
            string descriptionKey,
            string glyph,
            int progress,
            int goal) => new(
                localization.Format(titleKey, goal),
                localization.Format(descriptionKey, goal),
                glyph,
                progress,
                goal);

        AddGroup(
            "exploration",
            localization.Get("AchievementGroupExploration"),
            new AchievementItem(
                localization.Get("AchievementFirstRun"),
                localization.Get("AchievementFirstRunDescription"),
                "\uE768",
                _statistics.Played,
                1),
            new AchievementItem(
                localization.Get("AchievementExplorer"),
                localization.Get("AchievementExplorerDescription"),
                "\uE7FC",
                sessions.Maps,
                25),
            new AchievementItem(
                localization.Get("AchievementSecretHunter"),
                localization.Get("AchievementSecretHunterDescription"),
                "\uE72A",
                sessions.Secrets,
                100),
            Tier("AchievementHoursTier", "AchievementHoursTierDescription", "\uE823",
                _statistics.TotalMinutesPlayed / 60, 10),
            Tier("AchievementHoursTier", "AchievementHoursTierDescription", "\uE823",
                _statistics.TotalMinutesPlayed / 60, 20),
            Tier("AchievementHoursTier", "AchievementHoursTierDescription", "\uE823",
                _statistics.TotalMinutesPlayed / 60, 50),
            Tier("AchievementHoursTier", "AchievementHoursTierDescription", "\uE823",
                _statistics.TotalMinutesPlayed / 60, 100));

        AddGroup(
            "progress",
            localization.Get("AchievementGroupProgress"),
            Tier("AchievementFinishedTier", "AchievementFinishedTierDescription", "\uE73E",
                _statistics.Finished, 10),
            Tier("AchievementFinishedTier", "AchievementFinishedTierDescription", "\uE73E",
                _statistics.Finished, 50),
            Tier("AchievementFinishedTier", "AchievementFinishedTierDescription", "\uE73E",
                _statistics.Finished, 100),
            new AchievementItem(
                localization.Get("AchievementAllSkills"),
                localization.Get("AchievementAllSkillsDescription"),
                "\uE7C1",
                sessions.DistinctSkills,
                5),
            new AchievementItem(
                localization.Get("AchievementIwadCenturion"),
                localization.Get("AchievementIwadCenturionDescription"),
                "\uE768",
                _originalIwadLaunches,
                100));

        AddGroup(
            "combat",
            localization.Get("AchievementGroupCombat"),
            Tier("AchievementKillsTier", "AchievementKillsTierDescription", "\uE7BA",
                sessions.Kills, 500),
            Tier("AchievementKillsTier", "AchievementKillsTierDescription", "\uE7BA",
                sessions.Kills, 2000),
            Tier("AchievementKillsTier", "AchievementKillsTierDescription", "\uE7BA",
                sessions.Kills, 5000),
            Tier("AchievementKillsTier", "AchievementKillsTierDescription", "\uE7BA",
                sessions.Kills, 10000),
            Tier("AchievementItemsTier", "AchievementItemsTierDescription", "\uE8B7",
                sessions.Items, 500),
            Tier("AchievementItemsTier", "AchievementItemsTierDescription", "\uE8B7",
                sessions.Items, 2000),
            Tier("AchievementItemsTier", "AchievementItemsTierDescription", "\uE8B7",
                sessions.Items, 5000),
            Tier("AchievementItemsTier", "AchievementItemsTierDescription", "\uE8B7",
                sessions.Items, 10000));

        AddGroup(
            "library",
            localization.Get("AchievementGroupLibrary"),
            Tier("AchievementLibraryTier", "AchievementLibraryTierDescription", "\uE8F1",
                _catalog.Count, 10),
            Tier("AchievementLibraryTier", "AchievementLibraryTierDescription", "\uE8F1",
                _catalog.Count, 25),
            Tier("AchievementLibraryTier", "AchievementLibraryTierDescription", "\uE8F1",
                _catalog.Count, 50),
            Tier("AchievementLibraryTier", "AchievementLibraryTierDescription", "\uE8F1",
                _catalog.Count, 100),
            Tier("AchievementIdGamesTier", "AchievementIdGamesTierDescription", "\uE896",
                _statistics.IdGamesDownloads, 10),
            Tier("AchievementIdGamesTier", "AchievementIdGamesTierDescription", "\uE896",
                _statistics.IdGamesDownloads, 25),
            Tier("AchievementIdGamesTier", "AchievementIdGamesTierDescription", "\uE896",
                _statistics.IdGamesDownloads, 50),
            Tier("AchievementIdGamesTier", "AchievementIdGamesTierDescription", "\uE896",
                _statistics.IdGamesDownloads, 100));

        AddGroup(
            "community",
            localization.Get("AchievementGroupCommunity"),
            new AchievementItem(
                localization.Get("AchievementThemes"),
                localization.Get("AchievementThemesDescription"),
                "\uE790",
                _testedThemes.Count,
                Math.Max(1, ThemeManager.GetAvailableThemes().Count)),
            new AchievementItem(
                localization.Get("AchievementCollectionImported"),
                localization.Get("AchievementCollectionImportedDescription"),
                "\uE8B5",
                _importedCollectionCount,
                1),
            new AchievementItem(
                localization.Get("AchievementFavorites"),
                localization.Get("AchievementFavoritesDescription"),
                "\uE734",
                _favoriteGameFileIds.Count,
                10));

        ReplaceItems(Achievements, all);
        ReplaceItems(AchievementGroups, groups);
        ReplaceItems(AchievementSummary, new[]
        {
            new StatisticCardItem(
                localization.Get("UnlockedAchievements"),
                $"{all.Count(item => item.IsUnlocked):N0} / {all.Count:N0}",
                "\uE73E"),
            new StatisticCardItem(
                localization.Get("PlayedHours"),
                (_statistics.TotalMinutesPlayed / 60d).ToString("N1"),
                "\uE823"),
            new StatisticCardItem(
                localization.Get("FinishedMods"),
                _statistics.Finished.ToString("N0"),
                "\uE7E7"),
            new StatisticCardItem(
                localization.Get("CombatStats"),
                sessions.Kills.ToString("N0"),
                "\uE7BA"),
            new StatisticCardItem(
                localization.Get("CollectedItems"),
                sessions.Items.ToString("N0"),
                "\uE719"),
            new StatisticCardItem(
                localization.Get("FoundSecrets"),
                sessions.Secrets.ToString("N0"),
                "\uE721"),
        });
        ProcessAchievementUnlocks(all);
        UpdateSectionSummary();
    }

    public void TriggerDebugAchievementNotification()
    {
        AchievementUnlocked?.Invoke(new AchievementItem(
            localization.Get("DebugAchievementTitle"),
            localization.Get("DebugAchievementDescription"),
            "\uE73E",
            1,
            1,
            "debug.notification"));
    }

    private void ProcessAchievementUnlocks(
        IReadOnlyCollection<AchievementItem> achievements)
    {
        var unlocked = achievements
            .Where(item => item.IsUnlocked && item.Key.Length > 0)
            .ToArray();
        var stateChanged = false;
        if (!_achievementNotificationsInitialized)
        {
            _notifiedAchievementKeys.UnionWith(
                unlocked.Select(item => item.Key));
            _achievementNotificationsInitialized = true;
            stateChanged = true;
        }
        else
        {
            foreach (var achievement in unlocked)
            {
                if (!_notifiedAchievementKeys.Add(achievement.Key))
                    continue;
                _unseenAchievementKeys.Add(achievement.Key);
                stateChanged = true;
                AchievementUnlocked?.Invoke(achievement);
            }
        }
        if (!stateChanged)
            return;
        NotifyAchievementBadgeChanged();
        _ = PersistAchievementNotificationStateAsync();
    }

    private void MarkAchievementNotificationsSeen()
    {
        if (_unseenAchievementKeys.Count == 0)
            return;
        _unseenAchievementKeys.Clear();
        NotifyAchievementBadgeChanged();
        _ = PersistAchievementNotificationStateAsync();
    }

    private void NotifyAchievementBadgeChanged()
    {
        OnPropertyChanged(nameof(UnseenAchievementCount));
        OnPropertyChanged(nameof(AchievementBadgeVisibility));
    }

    private async Task PersistAchievementNotificationStateAsync()
    {
        await _achievementStateGate.WaitAsync();
        try
        {
            var state = await userLibraryStateStore.LoadAsync();
            await userLibraryStateStore.SaveAsync(state with
            {
                AchievementNotificationsInitialized =
                    _achievementNotificationsInitialized,
                NotifiedAchievementKeys = new HashSet<string>(
                    _notifiedAchievementKeys,
                    StringComparer.Ordinal),
                UnseenAchievementKeys = new HashSet<string>(
                    _unseenAchievementKeys,
                    StringComparer.Ordinal),
            });
        }
        catch
        {
            // A notification persistence failure must not interrupt the launcher.
        }
        finally
        {
            _achievementStateGate.Release();
        }
    }

    private void UpdateSectionSummary()
    {
        LibrarySummary = _activeSection switch
        {
            LibrarySection.Home => localization.Format(
                "HomeSummary",
                _catalog.Count(game => game.Category == "Mod"),
                _catalog.Count(game => game.Category == "Mod"
                    && game.MinutesPlayed == 0)),
            LibrarySection.Discover => localization.Format(
                "DiscoverSummary",
                DiscoverItems.Count),
            LibrarySection.Favorites => localization.Format(
                "FavoritesSummary",
                _filteredCount),
            LibrarySection.Recent => localization.Format(
                "RecentSummary",
                _filteredCount),
            LibrarySection.Downloads => localization.Format(
                "LatestSummary",
                _filteredCount),
            LibrarySection.Collections => localization.Format(
                "CollectionsSummary",
                CollectionGroups.Count,
                CollectionGroups.Sum(group => group.Items.Count)),
            LibrarySection.Achievements => localization.Format(
                "AchievementsSummary",
                Achievements.Count(item => item.IsUnlocked),
                Achievements.Count),
            LibrarySection.Debug => localization.Get("DebugSummary"),
            _ => _filteredCount == _catalog.Count
                ? localization.Format(
                    "Entries",
                    _catalog.Count,
                    _librarySourceName)
                : localization.Format(
                    "OfEntries",
                    _filteredCount,
                    _catalog.Count,
                    _librarySourceName),
        };
    }

    private void ResetLaunchOptions()
    {
        _launchOptionsGameFileId = null;
        SourcePortOptions.Clear();
        IwadOptions.Clear();
        MapOptions.Clear();
        SkillOptions.Clear();
        _selectedSourcePortOption = null;
        _selectedIwadOption = null;
        _selectedMapOption = null;
        _selectedSkillOption = null;
        OnPropertyChanged(nameof(SelectedSourcePortOption));
        OnPropertyChanged(nameof(SelectedIwadOption));
        OnPropertyChanged(nameof(SelectedMapOption));
        OnPropertyChanged(nameof(SelectedSkillOption));
        OnPropertyChanged(nameof(CanLaunchWithOptions));
    }

    private static void ReplaceItems<T>(
        ObservableCollection<T> target,
        IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private static string GetInnermostMessage(Exception exception)
    {
        while (exception.InnerException is not null)
            exception = exception.InnerException;
        return exception.Message;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record IdGamesBulkRefreshProgress(
    int Completed,
    int Total,
    string Title);

public sealed record IdGamesBulkRefreshResult(
    int Total,
    int Updated,
    int ArtworkImported,
    int Failed);

public enum LibrarySection
{
    Home,
    Library,
    Discover,
    Favorites,
    Recent,
    Downloads,
    Collections,
    Achievements,
    Debug,
}

public enum LibraryCategoryFilter
{
    All,
    Iwads,
    Mods,
    Unplayed,
}

public enum LibrarySortOrder
{
    Title,
    Author,
    ReleaseDate,
    Maps,
    Rating,
    Downloaded,
    SourcePort,
    Finished,
    LastPlayed,
    Playtime,
    Year,
}
