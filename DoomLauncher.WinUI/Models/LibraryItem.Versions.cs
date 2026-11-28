using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.Models;

public sealed partial class LibraryItem
{
    internal LibraryCatalogEntry CatalogEntry => _entry!;
    public string VersionGroupKey { get; } = "";
    public int? ReleaseOrder { get; }
    public LibraryItem? VersionGroupTarget { get; private set; }
    public bool IsVersionGroup => VersionGroupTarget is not null;
    public IReadOnlyList<LibraryItem> VersionMembers { get; private set; } = [];
    public bool VersionsExpanded { get; private set; }
    public Visibility VersionToggleVisibility => IsVersionGroup ? Visibility.Visible : Visibility.Collapsed;
    public string VersionChevron => VersionsExpanded ? "\uE70D" : "\uE76C";
    public string VersionToggleLabel => _localization?.Get(VersionsExpanded ? "CollapseVersions" : "ExpandVersions") ?? "";
    public string VersionGroupSummary { get; private set; } = "";
    public string SelectedVersionLabel { get; private set; } = "";
    public Thickness VersionTitleMargin => new(IsVersionGroup ? 37 : _versionChild ? 52 : 7, 0, 7, 0);
    private bool _versionChild;

    internal LibraryItem CreateVersionHeader(string key) => new(_entry! with { Title = key }, _localization!)
    { VersionGroupTarget = this };

    internal void SetVersionChild(bool child)
    {
        if (_versionChild == child) return;
        _versionChild = child; OnPropertyChanged(nameof(VersionTitleMargin));
    }

    internal void UpdateVersionHeader(IReadOnlyList<LibraryItem> members, int matches, bool expanded, LibraryItem selected)
    {
        IsFavorite = VersionGroupTarget!.IsFavorite;
        IsFinished = VersionGroupTarget.IsFinished;
        if (VersionGroupTarget._personal is not null) SetPersonalData(VersionGroupTarget._personal);
        VersionMembers = members; VersionsExpanded = expanded;
        VersionGroupSummary = "  · " + _localization!.Format(matches == members.Count ? "VersionCount" : "MatchingVersionCount", matches, members.Count);
        SelectedVersionLabel = members.Contains(selected) && !ReferenceEquals(selected, VersionGroupTarget) && !expanded
            ? _localization.Format("SelectedVersion", selected.Version) : "";
        SetIwadVersionVisible(true);
        foreach (var property in new[] { nameof(VersionChevron), nameof(VersionToggleLabel), nameof(VersionGroupSummary), nameof(SelectedVersionLabel), nameof(LibraryTitleToolTip) })
            OnPropertyChanged(property);
    }
}
