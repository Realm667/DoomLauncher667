using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoomLauncher.WinUI.Models;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed class IdGamesService : IIdGamesService, IDisposable
{
    private const string DefaultApi =
        "https://www.doomworld.com/idgames/api/api.php";
    private const string DefaultMirror =
        "https://youfailit.net/pub/idgames/";
    private readonly IDoomLauncherDatabaseLocator _databaseLocator;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<int, IdGamesItem> _detailCache = new();

    public IdGamesService(IDoomLauncherDatabaseLocator databaseLocator)
    {
        _databaseLocator = databaseLocator;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DoomLauncher-667", "0.8"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<IReadOnlyList<IdGamesItem>> GetLatestAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            $"action=latestfiles&limit={Math.Clamp(limit, 1, 100)}&out=json",
            cancellationToken,
            allowIncomplete: true);

    public async Task<IdGamesItem?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (_detailCache.TryGetValue(id, out var cached))
            return cached;

        var settings = await LoadSettingsAsync(cancellationToken);
        var item = await GetDetailedItemAsync(
            settings.ApiUrl,
            id,
            cancellationToken);
        if (item is not null)
            _detailCache[id] = item;
        return item;
    }

    public async Task<IdGamesItem?> RefreshByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        _detailCache.TryRemove(id, out _);
        return await GetByIdAsync(id, cancellationToken);
    }

    private async Task<IdGamesItem?> GetDetailedItemAsync(
        string apiUrl,
        int id,
        CancellationToken cancellationToken)
    {
        var requestUri = $"{apiUrl}?action=get&id={id}&out=json";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("content", out var content))
            return null;

        var file = content.TryGetProperty("file", out var nestedFile)
            ? nestedFile
            : content;
        var result = new List<IdGamesItem>(1);
        if (file.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in file.EnumerateArray())
                AddFile(result, entry, allowIncomplete: false);
        }
        else
        {
            AddFile(result, file, allowIncomplete: false);
        }
        return result.SingleOrDefault();
    }

    public Task<IReadOnlyList<IdGamesItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        return query.Length < 3
            ? Task.FromResult<IReadOnlyList<IdGamesItem>>([])
            : QueryAsync(
                $"action=search&type=title&query={Uri.EscapeDataString(query)}&out=json",
                cancellationToken);
    }

    public async Task<IReadOnlyList<IdGamesItem>> FindMatchesAsync(
        string fileName,
        string title,
        CancellationToken cancellationToken = default)
    {
        var archiveName = Path.GetFileName(fileName);
        var archiveStem = Path.GetFileNameWithoutExtension(archiveName);
        var candidates = new List<IdGamesItem>();
        var simplifiedStem = SimplifyArchiveStem(archiveStem);
        foreach (var fileQuery in new[] { archiveStem, simplifiedStem }
                     .Where(query => query.Length >= 2)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.AddRange(await QueryAsync(
                $"action=search&type=filename&query={Uri.EscapeDataString(fileQuery)}&out=json",
                cancellationToken));
        }
        var simplifiedTitle = SimplifyTitle(title);
        foreach (var titleQuery in new[] { title.Trim(), simplifiedTitle }
                     .Where(query => query.Length >= 3)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            candidates.AddRange(await SearchAsync(titleQuery, cancellationToken));
        }

        return candidates
            .DistinctBy(item => item.Id)
            .OrderByDescending(item => item.FileName.Equals(
                archiveName,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Title.Equals(
                title,
                StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(item => SimplifyArchiveStem(
                Path.GetFileNameWithoutExtension(item.FileName)).Equals(
                    simplifiedStem,
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => SimplifyTitle(item.Title).Equals(
                simplifiedTitle,
                StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(item => item.ReleaseDate)
            .Take(20)
            .ToArray();
    }

    private static string SimplifyArchiveStem(string value) =>
        Regex.Replace(
            value.Trim(),
            @"(?:[_\-.](?:(?:r?c|v|rev|beta)\d+(?:\.\d+)*|\d+(?:\.\d+)*))+$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string SimplifyTitle(string value)
    {
        var result = value.Trim();
        var quotedBy = Regex.Match(
            result,
            @"^[""“”](?<title>.+?)[""“”]\s+by\s+.+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (quotedBy.Success)
            return quotedBy.Groups["title"].Value.Trim();
        result = result.Trim('"', '“', '”');
        return Regex.Replace(
            result,
            @"\s+by\s+[^:]+$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }

    public async Task DownloadAsync(
        IdGamesItem item,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var relativePath =
            $"{item.Directory.Trim('/', '\\')}/{item.FileName}".Replace('\\', '/');
        var uri = new Uri(new Uri(settings.MirrorUrl), relativePath);

        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            if (total > 0)
                progress?.Report(written * 100d / total.Value);
        }
        progress?.Report(100);
    }

    private async Task<IReadOnlyList<IdGamesItem>> QueryAsync(
        string query,
        CancellationToken cancellationToken,
        bool allowIncomplete = false)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var requestUri = $"{settings.ApiUrl}?{query}";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("content", out var content)
            || !content.TryGetProperty("file", out var files))
        {
            return [];
        }

        var result = new List<IdGamesItem>();
        if (files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
                AddFile(result, file, allowIncomplete);
        }
        else if (files.ValueKind == JsonValueKind.Object)
        {
            AddFile(result, files, allowIncomplete);
        }

        IReadOnlyList<IdGamesItem> selected = allowIncomplete
            ? result.Take(100).ToArray()
            : result
                .OrderByDescending(item => item.ReleaseDate)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(100)
                .ToArray();
        return await EnrichMetadataAsync(
            settings.ApiUrl,
            selected,
            cancellationToken);
    }

    private async Task<IReadOnlyList<IdGamesItem>> EnrichMetadataAsync(
        string apiUrl,
        IReadOnlyList<IdGamesItem> items,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(4);
        var tasks = items.Select(async item =>
        {
            if (item.Id <= 0
                || (item.ReleaseDate.HasValue && item.Rating > 0))
            {
                return item;
            }
            if (_detailCache.TryGetValue(item.Id, out var cached))
                return cached;

            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var detailed = await GetDetailedItemAsync(
                    apiUrl,
                    item.Id,
                    cancellationToken);
                if (detailed is null)
                    return item;
                _detailCache[item.Id] = detailed;
                return detailed;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One incomplete API record must not make the whole Discover page fail.
                return item;
            }
            finally
            {
                concurrency.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static void AddFile(
        ICollection<IdGamesItem> result,
        JsonElement file,
        bool allowIncomplete)
    {
        var fileName = GetString(file, "filename") ?? string.Empty;
        if ((!allowIncomplete && string.IsNullOrWhiteSpace(fileName))
            || (!string.IsNullOrWhiteSpace(fileName)
                && !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var title = DatabaseTextSanitizer.SingleLine(GetString(file, "title"));
        result.Add(new IdGamesItem
        {
            Id = GetInt32(file, "id"),
            Title = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(fileName)
                : title,
            Author = DatabaseTextSanitizer.SingleLine(GetString(file, "author"))
                is { Length: > 0 } author ? author : "—",
            Description = DatabaseTextSanitizer.Multiline(
                (GetString(file, "description") ?? string.Empty)
                    .Replace("<br>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)),
            FileName = fileName,
            Directory = GetString(file, "dir") ?? string.Empty,
            ReleaseDate = GetDate(file, "date"),
            Rating = GetDouble(file, "rating"),
            SizeBytes = GetInt64(file, "size"),
        });
    }

    private async Task<IdGamesSettings> LoadSettingsAsync(
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databaseLocator.FindDatabase(),
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Name, Value
            FROM Configuration
            WHERE Name IN ('IdGamesUrl', 'ApiPage', 'MirrorUrl');
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        var api = CombineApi(
            values.GetValueOrDefault("IdGamesUrl"),
            values.GetValueOrDefault("ApiPage"));
        var mirror = values.GetValueOrDefault("MirrorUrl");
        if (!Uri.TryCreate(mirror, UriKind.Absolute, out var mirrorUri)
            || mirrorUri.Scheme is not ("http" or "https"))
        {
            mirror = DefaultMirror;
        }
        else if (mirrorUri.Scheme == "http")
        {
            mirror = new UriBuilder(mirrorUri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
        }

        return new IdGamesSettings(api, EnsureTrailingSlash(mirror));
    }

    private static string CombineApi(string? root, string? page)
    {
        if (!Uri.TryCreate(root, UriKind.Absolute, out var rootUri))
            return DefaultApi;
        if (rootUri.Scheme == "http")
            rootUri = new UriBuilder(rootUri) { Scheme = "https", Port = -1 }.Uri;
        if (string.IsNullOrWhiteSpace(page))
            return DefaultApi;
        return new Uri(rootUri, page).AbsoluteUri;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString()
            : null;

    private static int GetInt32(JsonElement element, string property) =>
        int.TryParse(GetString(element, property), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long GetInt64(JsonElement element, string property) =>
        long.TryParse(GetString(element, property), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double GetDouble(JsonElement element, string property) =>
        double.TryParse(GetString(element, property), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static DateTime? GetDate(JsonElement element, string property) =>
        DateTime.TryParse(GetString(element, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var value) ? value : null;

    public void Dispose() => _httpClient.Dispose();

    private sealed record IdGamesSettings(string ApiUrl, string MirrorUrl);
}
