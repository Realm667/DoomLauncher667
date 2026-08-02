using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.Models;

public sealed class LibraryItem : INotifyPropertyChanged
{
    private static readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> ArtworkCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _artworkPath =
        "ms-appx:///Assets/Library/grayscale/doom2.png";
    private readonly string _detailArtworkPath =
        "ms-appx:///Assets/Library/grayscale/doom2.png";
    private BitmapImage? _artwork;
    private BitmapImage? _detailArtwork;
    private IReadOnlyList<BitmapImage>? _images;
    private int _currentImageIndex;
    private bool _isFavorite;
    private bool _isFinished;
    private readonly UiLocalization? _localization;
    private readonly bool _usesDoomPixelAspect;

    public static LibraryItem Empty { get; } = new();
    public LibraryItem Self => this;

    public static void ClearArtworkCache() => ArtworkCache.Clear();

    public LibraryItem(
        LibraryCatalogEntry entry,
        UiLocalization localization,
        bool isFinished = false,
        bool isFavorite = false)
    {
        _localization = localization;
        GameFileId = entry.GameFileId;
        FileName = entry.FileName;
        Title = entry.Title;
        Subtitle = entry.Author;
        Author = entry.Author;
        Category = entry.Category;
        Year = entry.Year;
        ReleaseDate = entry.ReleaseDate;
        Maps = entry.Maps;
        Rating = entry.Rating;
        Downloaded = entry.Downloaded;
        Description = entry.Description;
        DescriptionPreview = CreateDescriptionPreview(entry.Description);
        SourcePort = entry.SourcePort;
        Iwad = entry.Iwad;
        Playtime = entry.Playtime;
        LastPlayed = entry.LastPlayed;
        MinutesPlayed = entry.MinutesPlayed;
        LastPlayedAt = entry.LastPlayedAt;
        ReleaseDateAt = entry.ReleaseDateAt;
        DownloadedAt = entry.DownloadedAt;
        MapCount = entry.MapCount;
        RatingValue = entry.RatingValue;
        IdGamesId = entry.IdGamesId;
        IsDownloaded = entry.IsDownloaded;
        IsIdGamesDownload = entry.IsIdGamesDownload;
        Tags = entry.Tags;
        TagsText = entry.Tags.Count == 0
            ? localization.Get("NoCollection")
            : string.Join(" · ", entry.Tags);
        IsTotalConversion =
            entry.Title.Contains("total conversion", StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains("total conversion", StringComparison.OrdinalIgnoreCase)
            || entry.Tags.Any(tag =>
                tag.Contains("total conversion", StringComparison.OrdinalIgnoreCase));
        Meta = $"{entry.Category} · {entry.Year}";
        _artworkPath = entry.ArtworkPath;
        _detailArtworkPath = entry.DetailArtworkPath;
        _usesDoomPixelAspect = entry.UsesDoomPixelAspect;
        ScreenshotPaths = entry.ScreenshotPaths;
        _isFinished = isFinished || entry.IsFinished;
        _isFavorite = isFavorite;
    }

    private LibraryItem()
    {
        IsPlaceholder = true;
        Title = "No selection";
        Subtitle = "Doom Launcher";
        Category = "Library";
        Year = "—";
        Meta = "No entries yet";
        Description = "Import a game or mod, or migrate an existing DoomLauncher library.";
        DescriptionPreview = Description;
        SourcePort = "—";
        Iwad = "—";
        Playtime = "—";
        LastPlayed = "—";
    }

    public int GameFileId { get; }
    public string FileName { get; } = string.Empty;
    public string Title { get; }
    public string Subtitle { get; }
    public string Author { get; } = string.Empty;
    public string Category { get; }
    public string Year { get; }
    public string ReleaseDate { get; } = "—";
    public string Maps { get; } = "—";
    public string Rating { get; } = "—";
    public string Downloaded { get; } = "—";
    public string Meta { get; }
    public string Description { get; }
    public string DescriptionPreview { get; } = string.Empty;
    public bool HasLongDescription => DescriptionPreview.Length < Description.Length;
    public Visibility ReadMoreVisibility =>
        HasLongDescription ? Visibility.Visible : Visibility.Collapsed;
    public string SourcePort { get; }
    public string Iwad { get; }
    public string Playtime { get; }
    public string LastPlayed { get; }
    public int MinutesPlayed { get; }
    public DateTime? LastPlayedAt { get; }
    public DateTime? ReleaseDateAt { get; }
    public DateTime? DownloadedAt { get; }
    public int MapCount { get; }
    public double RatingValue { get; }
    public int? IdGamesId { get; }
    public bool IsDownloaded { get; }
    public bool IsIdGamesDownload { get; }
    public bool IsTotalConversion { get; }
    public IReadOnlyList<string> Tags { get; } = [];
    public IReadOnlyList<string> ScreenshotPaths { get; } = [];
    public string TagsText { get; } = string.Empty;
    public BitmapImage Artwork => _artwork ??= GetArtwork(_artworkPath);
    public BitmapImage DetailArtwork => _detailArtwork ??=
        GetArtwork(_detailArtworkPath, 1600);
    public Stretch ArtworkStretch =>
        _usesDoomPixelAspect ? Stretch.Fill : Stretch.UniformToFill;
    public IReadOnlyList<BitmapImage> Images => _images ??=
        new[] { _detailArtworkPath }
            .Concat(ScreenshotPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => GetArtwork(path, 1600))
            .ToArray();
    public BitmapImage CurrentImage => Images[_currentImageIndex];
    public Stretch CurrentImageStretch =>
        _currentImageIndex == 0 && _usesDoomPixelAspect
            ? Stretch.Fill
            : Stretch.UniformToFill;
    public bool HasMultipleImages => Images.Count > 1;
    public Visibility SliderVisibility =>
        HasMultipleImages ? Visibility.Visible : Visibility.Collapsed;
    public string ImageCounter => $"{_currentImageIndex + 1} / {Images.Count}";
    public bool IsFinished
    {
        get => _isFinished;
        set
        {
            if (_isFinished == value)
                return;
            _isFinished = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FinishedText));
            OnPropertyChanged(nameof(FinishedBadgeVisibility));
            OnPropertyChanged(nameof(FinishedOpacity));
        }
    }
    public string FinishedText => _localization?.Get(IsFinished ? "Yes" : "No")
        ?? (IsFinished ? "Yes" : "No");
    public Visibility FinishedBadgeVisibility =>
        IsFinished ? Visibility.Visible : Visibility.Collapsed;
    public double FinishedOpacity => IsFinished ? 0.5 : 1.0;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteBadgeVisibility));
        }
    }
    public Visibility FavoriteBadgeVisibility =>
        IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPlaceholder { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ShowPreviousImage()
    {
        if (!HasMultipleImages)
            return;
        _currentImageIndex = (_currentImageIndex - 1 + Images.Count) % Images.Count;
        NotifyImageChanged();
    }

    public void ShowNextImage()
    {
        if (!HasMultipleImages)
            return;
        _currentImageIndex = (_currentImageIndex + 1) % Images.Count;
        NotifyImageChanged();
    }

    private void NotifyImageChanged()
    {
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(CurrentImageStretch));
        OnPropertyChanged(nameof(ImageCounter));
    }

    private static string CreateDescriptionPreview(string description)
    {
        const int maximumLength = 620;
        var value = description.Trim();
        if (value.Length <= maximumLength)
            return value;
        var cut = value.LastIndexOfAny(
            [' ', '\r', '\n', '\t'],
            maximumLength);
        if (cut < maximumLength / 2)
            cut = maximumLength;
        return value[..cut].TrimEnd() + "…";
    }

    private static BitmapImage GetArtwork(
        string artworkPath,
        int decodePixelWidth = 512)
    {
        var cacheKey = $"{decodePixelWidth}|{artworkPath}";
        if (ArtworkCache.TryGetValue(cacheKey, out var weakReference)
            && weakReference.TryGetTarget(out var cached))
        {
            return cached;
        }

        var image = new BitmapImage
        {
            DecodePixelWidth = decodePixelWidth,
            UriSource = new Uri(artworkPath),
        };
        ArtworkCache[cacheKey] = new WeakReference<BitmapImage>(image);
        return image;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
