using DoomLauncher.WinUI.Services;
using DoomLauncher.Modern.Core.Launch;

namespace DoomLauncher.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public async Task LaunchFeatureRequestAsync(GameLaunchRequest request, CancellationToken token = default)
    {
        if (!CanLaunch) return;
        var game = _catalog.FirstOrDefault(item => item.GameFileId == request.GameFileId);
        if (game is null) throw new InvalidOperationException("Library entry no longer exists.");
        await LaunchAsync(game, request, token);
    }
    private readonly LibraryFeatureStore _features = featureStore ?? new(new DoomLauncherDatabaseLocator());
    private IReadOnlyDictionary<int, PersonalModData> _personalData = new Dictionary<int, PersonalModData>();
    public PersonalModData SelectedPersonalData => _personalData.GetValueOrDefault(SelectedGame.GameFileId) ?? PersonalModData.Empty(SelectedGame.GameFileId);
    public double PersonalRatingValue => SelectedPersonalData.Rating ?? -1;
    public string QueueActionText => localization.Get(SelectedPersonalData.QueuePosition.HasValue ? "RemovePlayLater" : "AddPlayLater");
    private IReadOnlySet<int> _trackingGameIds = new HashSet<int>();
    public bool CanTrackSelectedGame => !SelectedGame.IsPlaceholder && _trackingGameIds.Contains(SelectedGame.GameFileId);
    public Microsoft.UI.Xaml.Visibility MapProgressVisibility => CanTrackSelectedGame
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string MapsActionText => localization.Get(CanTrackSelectedGame ? "MapsAndProgress" : "Maps");
    public string SelectedMapProgressText => SelectedGame.MapCount > 0
        ? localization.Format("MapProgressSummary", SelectedPersonalData.CompletedMaps.Count, SelectedGame.MapCount)
        : localization.Format("VerifiedMapsCount", SelectedPersonalData.CompletedMaps.Count);

    private void NotifyPersonalSelection()
    {
        OnPropertyChanged(nameof(PersonalRatingValue)); OnPropertyChanged(nameof(QueueActionText));
        OnPropertyChanged(nameof(SelectedMapProgressText)); OnPropertyChanged(nameof(SelectedPersonalData));
        OnPropertyChanged(nameof(CanTrackSelectedGame)); OnPropertyChanged(nameof(MapProgressVisibility));
        OnPropertyChanged(nameof(MapsActionText));
    }

    public async Task SetPersonalRatingAsync(int gameId, int? rating, CancellationToken token = default)
    { await _features.SetRatingAsync(gameId, rating, token); await ReloadPersonalDataAsync(token); }

    public async Task ToggleQueueAsync(CancellationToken token = default)
    {
        var gameId = SelectedGame.GameFileId; if (SelectedGame.IsPlaceholder) return;
        var current = SelectedPersonalData;
        await _features.SetQueuedAsync(gameId, !current.QueuePosition.HasValue, current.AutoDequeue, token);
        await ReloadPersonalDataAsync(token);
    }

    public async Task ReloadPersonalDataAsync(CancellationToken token = default)
    {
        _personalData = await _features.LoadAsync(token);
        foreach (var item in _catalog) item.SetPersonalData(_personalData.GetValueOrDefault(item.GameFileId) ?? PersonalModData.Empty(item.GameFileId));
        NotifyPersonalSelection();
        if (_activeSection == LibrarySection.Queue) ApplyFilter();
    }
}
