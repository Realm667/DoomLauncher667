using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.Models;

public sealed class AccordionDensityLayout : INotifyPropertyChanged
{
    private Thickness _cardMargin = new(0, 0, 0, 18);
    private Thickness _headerPadding = new(16, 11, 16, 11);
    private double _headerMinHeight = 46;
    private double _titleFontSize = 18;

    public Thickness CardMargin
    {
        get => _cardMargin;
        private set => Set(ref _cardMargin, value);
    }

    public Thickness HeaderPadding
    {
        get => _headerPadding;
        private set => Set(ref _headerPadding, value);
    }

    public double HeaderMinHeight
    {
        get => _headerMinHeight;
        private set => Set(ref _headerMinHeight, value);
    }

    public double TitleFontSize
    {
        get => _titleFontSize;
        private set => Set(ref _titleFontSize, value);
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

        CardMargin = ultraCompact
            ? new Thickness(0, 0, 0, 8)
            : compact
            ? new Thickness(0, 0, 0, 12)
            : new Thickness(0, 0, 0, 18);
        HeaderPadding = ultraCompact
            ? new Thickness(12, 4, 12, 4)
            : compact
            ? new Thickness(14, 7, 14, 7)
            : new Thickness(16, 11, 16, 11);
        HeaderMinHeight = ultraCompact ? 32 : compact ? 38 : 46;
        TitleFontSize = ultraCompact ? 15 : compact ? 16 : 18;
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
