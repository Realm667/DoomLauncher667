using System.Collections.ObjectModel;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using Microsoft.UI.Xaml;

namespace DoomLauncher.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<LibraryItem> ListRows { get; } = new ReconciledCollection<LibraryItem>();
    private bool _groupIwadVersions = true;
    private bool _versionListMode = true;
    public void SetVersionListMode(bool isList) { _versionListMode = isList; OnPropertyChanged(nameof(VersionGroupingVisibility)); }
    private HashSet<string> _expandedVersionGroups = new(StringComparer.Ordinal);
    private IReadOnlyList<LibraryItem> _versionScope = [];
    private readonly Dictionary<string, LibraryItem> _versionHeaders = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _versionStateGate = new(1, 1);
    private string _lastVersionQuery = "";
    private readonly HashSet<string> _searchCollapsedGroups = new(StringComparer.Ordinal);
    public event Action? VersionPreferencesChanged;
    public UserLibraryState WithVersionPreferences(UserLibraryState state) => state with
    { GroupIwadVersions = _groupIwadVersions, ExpandedIwadVersionGroups = new HashSet<string>(_expandedVersionGroups, StringComparer.Ordinal) };
    public bool GroupIwadVersions => _groupIwadVersions;
    public bool VersionGroupingActive => _groupIwadVersions && _activeSection == LibrarySection.Library && _categoryFilter == LibraryCategoryFilter.Iwads;
    public Visibility VersionGroupingVisibility => _versionListMode && _activeSection == LibrarySection.Library && _categoryFilter == LibraryCategoryFilter.Iwads ? Visibility.Visible : Visibility.Collapsed;
    public LibraryItem SelectedListRow => ListRows.FirstOrDefault(row => ReferenceEquals(row, SelectedGame))
        ?? ListRows.FirstOrDefault(row => row.VersionMembers.Contains(SelectedGame)) ?? LibraryItem.Empty;

    public async Task SetVersionGroupingAsync(bool enabled)
    {
        _groupIwadVersions = enabled; RebuildVersionRows();
        VersionPreferencesChanged?.Invoke();
        OnPropertyChanged(nameof(GroupIwadVersions));
        await SaveVersionStateAsync();
    }

    public async Task ToggleVersionGroupAsync(LibraryItem row, bool? expanded = null)
    {
        if (!row.IsVersionGroup) return;
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            if (expanded ?? !row.VersionsExpanded) _searchCollapsedGroups.Remove(row.VersionGroupKey);
            else _searchCollapsedGroups.Add(row.VersionGroupKey);
            RebuildVersionRows(); return;
        }
        if (expanded ?? !row.VersionsExpanded) _expandedVersionGroups.Add(row.VersionGroupKey);
        else _expandedVersionGroups.Remove(row.VersionGroupKey);
        VersionPreferencesChanged?.Invoke();
        RebuildVersionRows(); await SaveVersionStateAsync();
    }

    private async Task SaveVersionStateAsync()
    {
        await _versionStateGate.WaitAsync();
        try
        {
            var state = await userLibraryStateStore.LoadAsync();
            await userLibraryStateStore.SaveAsync(state with
            { GroupIwadVersions = _groupIwadVersions, ExpandedIwadVersionGroups = new HashSet<string>(_expandedVersionGroups, StringComparer.Ordinal) });
        }
        finally { _versionStateGate.Release(); }
    }

    private void UpdateVersionSelection()
    {
        foreach (var header in _versionHeaders.Values)
            header.UpdateVersionHeader(header.VersionMembers, Games.Count(g => g.VersionGroupKey == header.VersionGroupKey),
                header.VersionsExpanded, SelectedGame);
        OnPropertyChanged(nameof(LaunchButtonText));
    }

    private void RebuildVersionRows()
    {
        OnPropertyChanged(nameof(VersionGroupingVisibility));
        foreach (var item in _catalog) item.SetVersionChild(false);
        if (!VersionGroupingActive) { CollectionReconciler.Apply(ListRows, Games.ToArray()); return; }
        var byId = _versionScope.ToDictionary(item => item.GameFileId);
        var groups = IwadVersionGroups.Build(_versionScope.Select(item => item.CatalogEntry));
        if (_lastVersionQuery != _searchText) { _searchCollapsedGroups.Clear(); _lastVersionQuery = _searchText; }
        foreach (var key in _versionHeaders.Keys.Where(key => !groups.ContainsKey(key)).ToArray()) _versionHeaders.Remove(key);
        var visibleIds = Games.Select(g => g.GameFileId).ToHashSet();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var units = new List<LibraryItem>();
        var children = new Dictionary<LibraryItem,LibraryItem[]>();
        foreach (var item in Games)
        {
            if (!groups.TryGetValue(item.VersionGroupKey, out var definitions)) { units.Add(item); continue; }
            if (!emitted.Add(item.VersionGroupKey)) continue;
            var members = definitions.Select(entry => byId[entry.GameFileId]).ToArray();
            var latest = members[0];
            if (!_versionHeaders.TryGetValue(item.VersionGroupKey, out var header) || !ReferenceEquals(header.VersionGroupTarget, latest))
                _versionHeaders[item.VersionGroupKey] = header = latest.CreateVersionHeader(item.VersionGroupKey);
            var matches = members.Where(g => visibleIds.Contains(g.GameFileId)).ToArray();
            var expanded = !string.IsNullOrWhiteSpace(_searchText) ? !_searchCollapsedGroups.Contains(item.VersionGroupKey) : _expandedVersionGroups.Contains(item.VersionGroupKey);
            header.UpdateVersionHeader(members, matches.Length, expanded, SelectedGame); units.Add(header);
            if (expanded) children[header] = matches;
        }
        var rows = new List<LibraryItem>();
        foreach (var unit in ApplySelectedSort(units))
        {
            rows.Add(unit);
            if (children.TryGetValue(unit, out var members))
                foreach (var child in members) { child.SetVersionChild(true); rows.Add(child); }
        }
        CollectionReconciler.Apply(ListRows, rows);
    }
}
