using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DoomLauncher.WinUI.Models;

public sealed class IdGamesItem : INotifyPropertyChanged
{
    private bool _isDownloaded;
    private bool _isDownloading;
    private int? _libraryGameFileId;
    private string _actionText = string.Empty;

    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
    public required string FileName { get; init; }
    public required string Directory { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public double Rating { get; init; }
    public long SizeBytes { get; init; }
    public IdGamesItem Self => this;

    public string ReleaseDateText => ReleaseDate?.ToString("d") ?? "—";
    public string RatingText => $"{Math.Max(0, Rating):0.#}/5";
    public string SizeText => SizeBytes > 0
        ? $"{SizeBytes / 1024d / 1024d:0.#} MB"
        : string.Empty;
    public string MatchLabel => $"{Title} — {Author} ({FileName})";

    public int? LibraryGameFileId
    {
        get => _libraryGameFileId;
        set
        {
            if (_libraryGameFileId == value)
                return;
            _libraryGameFileId = value;
            OnPropertyChanged();
        }
    }

    public string ActionText
    {
        get => _actionText;
        set
        {
            if (_actionText == value)
                return;
            _actionText = value;
            OnPropertyChanged();
        }
    }

    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            if (_isDownloaded == value)
                return;
            _isDownloaded = value;
            NotifyDownloadState();
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value)
                return;
            _isDownloading = value;
            NotifyDownloadState();
        }
    }

    public bool CanDownload => !IsDownloaded && !IsDownloading;
    public bool IsActionEnabled => IsDownloaded || !IsDownloading;
    public string ActionGlyph => IsDownloaded ? "\uE8F1" : "\uE896";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyDownloadState()
    {
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(IsActionEnabled));
        OnPropertyChanged(nameof(ActionGlyph));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
