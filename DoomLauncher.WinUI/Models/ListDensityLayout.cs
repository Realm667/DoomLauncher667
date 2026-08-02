using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.Models;

public sealed class ListDensityLayout : INotifyPropertyChanged
{
    private double _rowMinHeight = 76;
    private double _artworkWidth = 64;
    private double _artworkHeight = 48;
    private Thickness _libraryRowPadding = new(8);
    private Thickness _collectionRowPadding = new(12, 8, 12, 8);

    public double RowMinHeight
    {
        get => _rowMinHeight;
        private set => Set(ref _rowMinHeight, value);
    }

    public double ArtworkWidth
    {
        get => _artworkWidth;
        private set => Set(ref _artworkWidth, value);
    }

    public double ArtworkHeight
    {
        get => _artworkHeight;
        private set => Set(ref _artworkHeight, value);
    }

    public Thickness LibraryRowPadding
    {
        get => _libraryRowPadding;
        private set => Set(ref _libraryRowPadding, value);
    }

    public Thickness CollectionRowPadding
    {
        get => _collectionRowPadding;
        private set => Set(ref _collectionRowPadding, value);
    }

    public void Apply(string density)
    {
        var ultraCompact = string.Equals(
            density,
            "UltraCompact",
            StringComparison.OrdinalIgnoreCase);
        var compact = string.Equals(
            density,
            "Compact",
            StringComparison.OrdinalIgnoreCase)
            || ultraCompact;
        RowMinHeight = ultraCompact ? 42 : compact ? 54 : 76;
        ArtworkWidth = ultraCompact ? 36 : compact ? 48 : 64;
        ArtworkHeight = ultraCompact ? 27 : compact ? 36 : 48;
        LibraryRowPadding = ultraCompact
            ? new Thickness(8, 1, 8, 1)
            : compact
            ? new Thickness(8, 3, 8, 3)
            : new Thickness(8);
        CollectionRowPadding = ultraCompact
            ? new Thickness(12, 1, 12, 1)
            : compact
            ? new Thickness(12, 3, 12, 3)
            : new Thickness(12, 8, 12, 8);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
