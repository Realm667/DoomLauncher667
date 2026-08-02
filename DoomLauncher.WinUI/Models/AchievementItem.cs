using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.Models;

public sealed record AchievementItem(
    string Title,
    string Description,
    string Glyph,
    int Progress,
    int Goal,
    string Key = "")
{
    public bool IsUnlocked => Progress >= Goal;
    public double ProgressPercent => Goal <= 0
        ? 0
        : Math.Min(100, Progress * 100d / Goal);
    public string ProgressText => $"{Math.Min(Progress, Goal):N0} / {Goal:N0}";
    public double Opacity => IsUnlocked ? 1 : 0.55;
    public Visibility UnlockedVisibility =>
        IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record AchievementGroup(
    string Title,
    IReadOnlyList<AchievementItem> Items);

public sealed record StatisticCardItem(
    string Label,
    string Value,
    string Glyph);
