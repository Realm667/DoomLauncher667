using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DoomLauncher.WinUI.Models;
using DoomLauncher.WinUI.Services;
using DoomLauncher.Modern.Core.Launch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DoomLauncher.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public async Task EnsureDiscoverLoadedAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isDiscoverLoaded || IsDiscoverLoading)
            return;
        await LoadDiscoverAsync(null, cancellationToken);
    }

    public Task SearchDiscoverAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        LoadDiscoverAsync(query, cancellationToken);

    public async Task LoadMoreDiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanLoadMoreDiscover)
            return;

        IsDiscoverLoading = true;
        try
        {
            _discoverLimit = Math.Min(_discoverLimit + DiscoverPageSize, 100);
            if (string.IsNullOrWhiteSpace(_discoverQuery))
            {
                _discoverResults = await idGamesService.GetLatestAsync(
                    _discoverLimit,
                    cancellationToken);
                MarkDownloadedItems(_discoverResults);
            }
            ReplaceItems(
                DiscoverItems,
                _discoverResults.Take(_discoverLimit));
            _hasMoreDiscover =
                _discoverResults.Count >= _discoverLimit
                && _discoverLimit < 100;
            NotifyDiscoverPagingChanged();
            IdGamesStatus = localization.Format(
                "IdGamesResults",
                DiscoverItems.Count);
            UpdateSectionSummary();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            IdGamesStatus = localization.Format(
                "IdGamesLoadFailed",
                GetInnermostMessage(exception));
        }
        finally
        {
            IsDiscoverLoading = false;
        }
    }

    public async Task DownloadIdGamesItemAsync(
        IdGamesItem item,
        ImportFileConflictResolution conflictResolution = ImportFileConflictResolution.Fail,
        CancellationToken cancellationToken = default)
    {
        if (!item.CanDownload)
            return;

        string? temporaryDirectory = null;
        string? temporaryPath = null;
        item.IsDownloading = true;
        IdGamesStatus = localization.Format("IdGamesDownloading", item.Title);
        try
        {
            IdGamesItem? detailedItem = null;
            if (item.Id > 0)
            {
                try
                {
                    detailedItem = await idGamesService.RefreshByIdAsync(
                        item.Id,
                        cancellationToken);
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException
                          && !string.IsNullOrWhiteSpace(item.FileName))
                {
                    // The fully populated Discover result is still a valid
                    // import source if the detail endpoint is temporarily down.
                }
            }
            var downloadItem = detailedItem ?? item;
            if (downloadItem is null
                || string.IsNullOrWhiteSpace(downloadItem.FileName))
            {
                throw new InvalidOperationException(
                    $"No downloadable /idgames archive was returned for ID {item.Id}.");
            }
            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"DoomLauncher-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            temporaryPath = Path.Combine(
                temporaryDirectory,
                Path.GetFileName(downloadItem.FileName));
            var progress = new Progress<double>(value =>
            {
                IdGamesStatus = localization.Format(
                    "IdGamesDownloadProgress",
                    item.Title,
                    value);
            });
            await idGamesService.DownloadAsync(
                downloadItem,
                temporaryPath,
                progress,
                cancellationToken);
            var imported = await nativeLibraryService.ImportIdGamesAsync(
                downloadItem,
                temporaryPath,
                conflictResolution,
                cancellationToken);
            if (imported.WasSkipped)
            {
                IdGamesStatus = localization.Get("ImportSkipped");
                return;
            }
            await ApplyIdGamesMetadataAsync(imported.GameFileId, downloadItem,
                downloadRemoteArtwork: false, progress: null, cancellationToken);
            item.IsDownloaded = true;
            item.LibraryGameFileId = imported.GameFileId;
            item.ActionText = localization.Get("OpenInLibrary");
            IdGamesStatus = localization.Format("IdGamesDownloaded", item.Title);
            await LoadCatalogAsync(
                forceRefresh: true,
                imported.GameFileId,
                cancellationToken);
            ShowLatestImports();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IdGamesStatus = string.Empty;
        }
        catch (Exception exception)
        {
            IdGamesStatus = localization.Format(
                "IdGamesDownloadFailed",
                GetInnermostMessage(exception));
        }
        finally
        {
            item.IsDownloading = false;
            if (!string.IsNullOrWhiteSpace(temporaryDirectory)
                && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    public Task<NativeImportConflict?> FindImportConflictAsync(
        string originalFileName,
        CancellationToken cancellationToken = default) =>
        nativeLibraryService.FindImportConflictAsync(
            originalFileName,
            cancellationToken);

    public async Task<string?> ResolveIdGamesArchiveFileNameAsync(
        IdGamesItem item,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(item.FileName))
            return Path.GetFileName(item.FileName);
        var resolved = await idGamesService.GetByIdAsync(item.Id, cancellationToken);
        return string.IsNullOrWhiteSpace(resolved?.FileName)
            ? null
            : Path.GetFileName(resolved.FileName);
    }

    private async Task LoadDiscoverAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        IsDiscoverLoading = true;
        _discoverQuery = query?.Trim() ?? string.Empty;
        _discoverLimit = DiscoverPageSize;
        IdGamesStatus = localization.Get("IdGamesConnecting");
        try
        {
            _discoverResults = string.IsNullOrWhiteSpace(_discoverQuery)
                ? await idGamesService.GetLatestAsync(
                    DiscoverPageSize,
                    cancellationToken)
                : await idGamesService.SearchAsync(
                    _discoverQuery,
                    cancellationToken);
            MarkDownloadedItems(_discoverResults);
            ReplaceItems(
                DiscoverItems,
                _discoverResults.Take(DiscoverPageSize));
            _hasMoreDiscover = _discoverResults.Count > DiscoverPageSize
                || (string.IsNullOrWhiteSpace(_discoverQuery)
                    && _discoverResults.Count == DiscoverPageSize);
            NotifyDiscoverPagingChanged();
            _isDiscoverLoaded = string.IsNullOrWhiteSpace(_discoverQuery);
            IdGamesStatus = DiscoverItems.Count == 0
                ? localization.Get("IdGamesNoResults")
                : localization.Format("IdGamesResults", DiscoverItems.Count);
            UpdateSectionSummary();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            IdGamesStatus = localization.Format(
                "IdGamesLoadFailed",
                GetInnermostMessage(exception));
        }
        finally
        {
            IsDiscoverLoading = false;
        }
    }

    private void MarkDownloadedItems(IEnumerable<IdGamesItem> items)
    {
        var localById = _catalog
            .Where(game => game.IdGamesId.HasValue)
            .GroupBy(game => game.IdGamesId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var localByFileName = _catalog
            .GroupBy(
                game => Path.GetFileName(game.FileName),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var local = localById.GetValueOrDefault(item.Id);
            if (local is null
                && !string.IsNullOrWhiteSpace(item.FileName))
            {
                localByFileName.TryGetValue(
                    Path.GetFileName(item.FileName),
                    out local);
            }
            item.LibraryGameFileId = local?.GameFileId;
            item.IsDownloaded = local is not null;
            item.ActionText = localization.Get(
                item.IsDownloaded ? "OpenInLibrary" : "Download");
        }
    }

    private void NotifyDiscoverPagingChanged()
    {
        OnPropertyChanged(nameof(LoadMoreDiscoverVisibility));
        OnPropertyChanged(nameof(CanLoadMoreDiscover));
    }

}
