using System.ComponentModel;
using System.Runtime.CompilerServices;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.Models;

public sealed class ListColumnLayout : INotifyPropertyChanged
{
    private readonly HashSet<string> _visible = new(
        UserLibraryState.DefaultVisibleColumns,
        StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    public GridLength ArtworkWidth => Width("Artwork", 72);
    public GridLength TitleWidth => Width("Title", 240);
    public GridLength AuthorWidth => Width("Author", 160);
    public GridLength ReleaseDateWidth => Width("ReleaseDate", 132);
    public GridLength MapsWidth => Width("Maps", 80);
    public GridLength RatingWidth => Width("Rating", 90);
    public GridLength DownloadedWidth => Width("Downloaded", 120);
    public GridLength SourcePortWidth => Width("SourcePort", 140);
    public GridLength PlaytimeWidth => Width("Playtime", 110);
    public GridLength FinishedWidth => Width("Finished", 90);

    public IReadOnlySet<string> VisibleColumns => _visible;

    public void Apply(IEnumerable<string> columns)
    {
        _visible.Clear();
        foreach (var column in columns)
            _visible.Add(column);
        if (_visible.Count == 0)
            _visible.Add("Title");
        NotifyAll();
    }

    public bool Toggle(string column, bool visible)
    {
        if (visible)
            _visible.Add(column);
        else if (_visible.Count == 1 && _visible.Contains(column))
            return false;
        else
            _visible.Remove(column);
        NotifyAll();
        return true;
    }

    private GridLength Width(string column, double width) =>
        _visible.Contains(column) ? new GridLength(width) : new GridLength(0);

    private void NotifyAll()
    {
        foreach (var property in new[]
                 {
                     nameof(ArtworkWidth), nameof(TitleWidth), nameof(AuthorWidth),
                     nameof(ReleaseDateWidth), nameof(MapsWidth), nameof(RatingWidth),
                     nameof(DownloadedWidth), nameof(SourcePortWidth), nameof(PlaytimeWidth),
                     nameof(FinishedWidth),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
