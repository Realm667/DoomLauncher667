using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DoomLauncher.WinUI.Models;

public sealed class LibraryGroup
{
    private static readonly BitmapImage CollectionPlaceholder = new()
    {
        DecodePixelWidth = 1024,
        UriSource = new Uri(
            "ms-appx:///Assets/Library/collection_placeholder.jpg"),
    };
    private readonly BitmapImage? _customArtwork;

    public LibraryGroup(
        string title,
        IReadOnlyList<LibraryItem> items,
        string progressToolTip = "",
        BitmapImage? artwork = null)
    {
        Title = title;
        Items = items;
        ProgressToolTip = progressToolTip;
        _customArtwork = artwork;
    }

    public LibraryGroup Self => this;
    public string Title { get; }
    public IReadOnlyList<LibraryItem> Items { get; }
    public string ProgressToolTip { get; }
    public int FinishedCount => Items.Count(item => item.IsFinished);
    public string ProgressText => $"{FinishedCount}/{Items.Count}";
    public BitmapImage? CustomArtwork => _customArtwork;
    public BitmapImage Artwork => _customArtwork ?? CollectionPlaceholder;
    public bool HasCustomArtwork => _customArtwork is not null;
    public Visibility MissingArtworkActionVisibility =>
        HasCustomArtwork ? Visibility.Collapsed : Visibility.Visible;
}
