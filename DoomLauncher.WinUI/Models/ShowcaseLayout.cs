using System.ComponentModel;

namespace DoomLauncher.WinUI.Models;

public sealed class ShowcaseLayout : INotifyPropertyChanged
{
    private double _spotlightHeight=153;
    public event PropertyChangedEventHandler? PropertyChanged;
    public double SpotlightHeight => _spotlightHeight;
    public void SetHeight(double heroHeight)
    {
        var height=(heroHeight-14)/2;
        if(Math.Abs(height-_spotlightHeight)<.5)return;
        _spotlightHeight=height;PropertyChanged?.Invoke(this,new(nameof(SpotlightHeight)));
    }
}
