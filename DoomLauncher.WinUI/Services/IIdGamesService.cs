using DoomLauncher.WinUI.Models;

namespace DoomLauncher.WinUI.Services;

public interface IIdGamesService
{
    Task<IReadOnlyList<IdGamesItem>> GetLatestAsync(
        int limit,
        CancellationToken cancellationToken = default);
    Task<IdGamesItem?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);
    Task<IdGamesItem?> RefreshByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IdGamesItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IdGamesItem>> FindMatchesAsync(
        string fileName,
        string title,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        IdGamesItem item,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
