using System.Text.Json;

namespace DoomLauncher.WinUI.Services;

public sealed class JsonUserLibraryStateStore : IUserLibraryStateStore
{
    public const string StateEnvironmentVariable = "DOOMLAUNCHER_USER_STATE";
    private const string StateFileName = "library-state.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private bool _hasKnownState;

    public JsonUserLibraryStateStore()
    {
        var configuredPath = Environment.GetEnvironmentVariable(StateEnvironmentVariable);
        _statePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DoomLauncher.WinUI",
                StateFileName)
            : Path.GetFullPath(configuredPath);
    }

    public string? RecoveryNoticeKey { get; private set; }

    public async Task<UserLibraryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try { var state = await ReadStateAsync(_statePath, cancellationToken); _hasKnownState = true; return state; }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                if (File.Exists(_statePath + ".bak"))
                {
                    var recovered = await ReadStateAsync(_statePath + ".bak", cancellationToken);
                    RecoveryNoticeKey = "StateRecovered";
                    _hasKnownState = true;
                    return recovered;
                }
                if (!_hasKnownState && error is (FileNotFoundException or DirectoryNotFoundException)
                    && Directory.Exists(Path.GetPathRoot(_statePath)))
                    return UserLibraryState.Empty;
                throw new IOException("Launcher settings could not be read. Check storage access; no settings have been reset.", error);
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<UserLibraryState> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<PersistedState>(
                stream,
                cancellationToken: cancellationToken);
            if (state is null) throw new JsonException("Settings file is empty.");
            var visibleColumns = NormalizeColumns(state?.VisibleColumns);
            return new UserLibraryState(
                new HashSet<int>(state?.FavoriteGameFileIds ?? []),
                new HashSet<int>(state?.FinishedGameFileIds ?? []),
                NormalizeTheme(state?.Theme),
                NormalizeLanguage(state?.Language),
                visibleColumns,
                state?.CollectionVisibleColumns is null
                    ? visibleColumns
                    : NormalizeColumns(state.CollectionVisibleColumns),
                NormalizeListDensity(state?.ListDensity),
                NormalizeListDensity(state?.AccordionDensity),
                NormalizeFilterTags(state?.LibraryFilterTags),
                NormalizeThemes(state?.TestedThemes, state?.Theme),
                Math.Max(0, state?.OriginalIwadLaunches ?? 0),
                Math.Max(0, state?.ImportedCollectionCount ?? 0),
                state?.AchievementNotificationsInitialized ?? false,
                NormalizeAchievementKeys(state?.NotifiedAchievementKeys),
                NormalizeAchievementKeys(state?.UnseenAchievementKeys),
                NormalizeCollectionNames(state?.CollapsedCollectionNames),
                NormalizeCollectionArtworkPaths(state?.CollectionArtworkPaths),
                NormalizeWindowDimension(state?.WindowWidth),
                NormalizeWindowDimension(state?.WindowHeight));
    }

    public async Task SaveAsync(
        UserLibraryState state,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_hasKnownState && !File.Exists(_statePath) && !File.Exists(_statePath + ".bak"))
                throw new IOException("The settings storage disappeared. Check the connection before saving again.");
            var directory = Path.GetDirectoryName(_statePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PersistedState(
                        state.FavoriteGameFileIds.Order().ToArray(),
                        state.FinishedGameFileIds.Order().ToArray(),
                        NormalizeTheme(state.Theme),
                        NormalizeLanguage(state.Language),
                        NormalizeColumns(state.VisibleColumns).ToArray(),
                        NormalizeColumns(state.CollectionVisibleColumns).ToArray(),
                        NormalizeListDensity(state.ListDensity),
                        NormalizeListDensity(state.AccordionDensity),
                        NormalizeFilterTags(state.LibraryFilterTags).ToArray(),
                        NormalizeThemes(state.TestedThemes, state.Theme).ToArray(),
                        Math.Max(0, state.OriginalIwadLaunches),
                        Math.Max(0, state.ImportedCollectionCount),
                        state.AchievementNotificationsInitialized,
                        NormalizeAchievementKeys(state.NotifiedAchievementKeys).ToArray(),
                        NormalizeAchievementKeys(state.UnseenAchievementKeys).ToArray(),
                        NormalizeCollectionNames(state.CollapsedCollectionNames).ToArray(),
                        NormalizeCollectionArtworkPaths(state.CollectionArtworkPaths)
                            .ToDictionary(
                                item => item.Key,
                                item => item.Value,
                                StringComparer.OrdinalIgnoreCase),
                        NormalizeWindowDimension(state.WindowWidth),
                        NormalizeWindowDimension(state.WindowHeight)),
                    cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_statePath))
            {
                var valid = true;
                try { await ReadStateAsync(_statePath, cancellationToken); }
                catch (JsonException) { valid = false; }
                if (valid)
                {
                    var backupTemp = temporaryPath + ".bak";
                    File.Copy(_statePath, backupTemp);
                    File.Move(backupTemp, _statePath + ".bak", overwrite: true);
                }
                else File.Copy(_statePath, _statePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
            }
            File.Move(temporaryPath, _statePath, overwrite: true);
            if (!File.Exists(_statePath + ".bak")) File.Copy(_statePath, _statePath + ".bak");
            _hasKnownState = true;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                if (File.Exists(temporaryPath + ".bak")) File.Delete(temporaryPath + ".bak");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NormalizeTheme(string? value)
    {
        var normalized = DatabaseTextSanitizer.SingleLine(value);
        return normalized.Length > 0 ? normalized : "Dark";
    }

    private static string NormalizeLanguage(string? value)
    {
        return value is "en-US" or "de-DE" or "fr-FR" or "es-ES"
            ? value
            : "en-US";
    }

    private static string NormalizeListDensity(string? value) =>
        value is "Normal" or "Compact" or "UltraCompact"
            ? value
            : "Normal";

    private static IReadOnlyList<string> NormalizeColumns(IReadOnlyList<string>? values)
    {
        var allowed = UserLibraryState.DefaultVisibleColumns.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var result = values?
            .Where(allowed.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return result is { Length: > 0 }
            ? result
            : UserLibraryState.DefaultVisibleColumns;
    }

    private static IReadOnlyList<string> NormalizeFilterTags(
        IReadOnlyList<string>? values) =>
        values?
            .Select(DatabaseTextSanitizer.SingleLine)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray()
        ?? [];

    private static IReadOnlySet<string> NormalizeThemes(
        IReadOnlyCollection<string>? values,
        string? currentTheme)
    {
        var result = new HashSet<string>(
            values?
                .Select(DatabaseTextSanitizer.SingleLine)
                .Where(value => value.Length > 0)
            ?? [],
            StringComparer.OrdinalIgnoreCase);
        result.Add(NormalizeTheme(currentTheme));
        return result;
    }

    private static IReadOnlySet<string> NormalizeAchievementKeys(
        IReadOnlyCollection<string>? values) =>
        new HashSet<string>(
            values?
                .Select(DatabaseTextSanitizer.SingleLine)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
            ?? [],
            StringComparer.Ordinal);

    private static IReadOnlySet<string> NormalizeCollectionNames(
        IReadOnlyCollection<string>? values) =>
        new HashSet<string>(
            values?
                .Select(DatabaseTextSanitizer.SingleLine)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
            ?? [],
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> NormalizeCollectionArtworkPaths(
        IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in values ?? new Dictionary<string, string>())
        {
            var name = DatabaseTextSanitizer.SingleLine(item.Key);
            var path = item.Value?.Trim() ?? string.Empty;
            if (path.StartsWith(
                    Path.Combine("UserData", "CollectionArtworks")
                        + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(
                    "Data",
                    "CollectionArtworks",
                    Path.GetFileName(path));
            }
            if (name.Length > 0 && path.Length > 0)
                result[name] = path;
        }
        return result;
    }

    private static int? NormalizeWindowDimension(int? value) =>
        value is >= 320 and <= 16384 ? value : null;

    private sealed record PersistedState(
        int[] FavoriteGameFileIds,
        int[]? FinishedGameFileIds = null,
        string? Theme = null,
        string? Language = null,
        string[]? VisibleColumns = null,
        string[]? CollectionVisibleColumns = null,
        string? ListDensity = null,
        string? AccordionDensity = null,
        string[]? LibraryFilterTags = null,
        string[]? TestedThemes = null,
        int? OriginalIwadLaunches = null,
        int? ImportedCollectionCount = null,
        bool? AchievementNotificationsInitialized = null,
        string[]? NotifiedAchievementKeys = null,
        string[]? UnseenAchievementKeys = null,
        string[]? CollapsedCollectionNames = null,
        Dictionary<string, string>? CollectionArtworkPaths = null,
        int? WindowWidth = null,
        int? WindowHeight = null);
}
