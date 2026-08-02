using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DoomLauncher.WinUI.Models;

public sealed class TileLayout : INotifyPropertyChanged
{
    private double _cardWidth = 184;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double CardWidth => _cardWidth;
    public double ArtworkHeight => Math.Round(_cardWidth * 0.75);
    public double CardHeight => ArtworkHeight + 112;
    public double PanelItemWidth => CardWidth + 16;
    public double PanelItemHeight => CardHeight + 16;

    public void SetWidth(double width)
    {
        width = Math.Clamp(width, 144, 280);
        if (Math.Abs(_cardWidth - width) < 0.5)
            return;

        _cardWidth = width;
        foreach (var property in new[]
                 {
                     nameof(CardWidth), nameof(ArtworkHeight), nameof(CardHeight),
                     nameof(PanelItemWidth), nameof(PanelItemHeight),
                 })
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(property));
        }
    }
}
