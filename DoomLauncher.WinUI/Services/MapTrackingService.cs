using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace DoomLauncher.WinUI.Services;

internal sealed record MapTrackingContext(string HelperPath, string LogPath, string SessionToken,
    string ArchivePath, string ArchiveHash, IReadOnlySet<string> Maps);

internal static class MapTrackingService
{
    internal const string ResourceName = "DoomLauncher667.MapTracker.zs";
    internal static bool SupportsEngine(string executable)
    {
        // Inspect the actual bundled engine API rather than trusting a display
        // label or a filename alone. Old ZDoom/Zandronum do not pass this probe.
        if (!File.Exists(executable)) return false;
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        try
        {
            foreach (var baseName in new[] { name, "gzdoom", "uzdoom", "lzdoom", "vkdoom", "qzdoom" }.Distinct())
            {
                var path = Path.Combine(Path.GetDirectoryName(executable)!, baseName + ".pk3");
                if (!File.Exists(path)) continue;
                using var zip = ZipFile.OpenRead(path);
                string Read(string entry)
                {
                    var item = zip.GetEntry(entry); if (item is null || item.Length > 1024 * 1024) return "";
                    using var reader = new StreamReader(item.Open()); return reader.ReadToEnd();
                }
                var api = Read("zscript/doombase.zs"); var events = Read("zscript/events.zs");
                if (api.Contains("LevelInfo info") && api.Contains("int clusterflags")
                    && events.Contains("UiTick()") && events.Contains("NextMap") && events.Contains("OnEngineInitialize()")) return true;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return false;
    }

    internal static async Task<MapTrackingContext?> PrepareAsync(string executable, string sessionDirectory,
        string archive, string iwadPath, CancellationToken token)
    {
        if (!SupportsEngine(executable) || !File.Exists(archive)) return null;
        IReadOnlyList<string> maps;
        try
        {
            maps = await MapNameExtractor.ExtractAsync(archive, token);
            if (maps.Count == 0) maps = await MapNameExtractor.ExtractAsync(iwadPath, token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        if (maps.Count == 0) return null;
        Directory.CreateDirectory(sessionDirectory);
        var helper = Path.Combine(sessionDirectory, "dl667-map-tracker-v1.pk3");
        CreateHelper(helper);
        return new(helper, Path.Combine(sessionDirectory, "map-tracker.log"), Guid.NewGuid().ToString("N"),
            archive, await SaveGameService.HashAsync(archive, token), maps.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal static void CreateHelper(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var entry = zip.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content);
        }
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Built-in map tracker resource is missing.");
        using var reader = new StreamReader(stream);
        Add("ZSCRIPT", reader.ReadToEnd());
        Add("MAPINFO", "gameinfo { AddEventHandlers = \"DL667MapTrackerV1\" }\n");
        Add("CVARINFO", "server string dl667_track_session = \"\";\n");
    }

    internal static IReadOnlySet<string> ParseCompletions(IEnumerable<string> lines, string session, IReadOnlySet<string> maps)
    {
        var ready = false; var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = "DL667_TRACK_V1|" + session + "|";
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var payload = line[prefix.Length..];
            if (payload == "ready") { ready = true; continue; }
            if (!ready || !payload.StartsWith("complete|", StringComparison.Ordinal)) continue;
            var map = payload[9..];
            if (map.Length is > 0 and <= 64 && map.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && maps.Contains(map)) result.Add(map.ToUpperInvariant());
        }
        return result;
    }

    internal static async Task ImportAsync(MapTrackingContext context, LibraryFeatureStore store, int gameId)
    {
        if (!File.Exists(context.LogPath) || new FileInfo(context.LogPath).Length > 16 * 1024 * 1024
            || !File.Exists(context.ArchivePath) || await SaveGameService.HashAsync(context.ArchivePath) != context.ArchiveHash) return;
        using var log = new StreamReader(new FileStream(context.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var completed = ParseCompletions((await log.ReadToEndAsync()).Split('\n'), context.SessionToken, context.Maps);
        await store.RecordValidatedMapsAsync(gameId, completed);
    }
}
