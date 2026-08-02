using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DoomLauncher.WinUI.Services;

public sealed class SqliteLaunchOptionsCatalog(
    IDoomLauncherDatabaseLocator databaseLocator,
    UiLocalization localization) : ILaunchOptionsCatalog
{
    public async Task<LaunchOptionsResult> LoadAsync(
        int gameFileId,
        CancellationToken cancellationToken = default)
    {
        var databasePath = databaseLocator.FindDatabase();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureGameExistsAsync(connection, gameFileId, cancellationToken);
        var sourcePorts = await LoadChoicesAsync(
            connection,
            """
            SELECT source.SourcePortID,
                   source.Name || CASE
                       WHEN NULLIF(TRIM(capability.Version), '') IS NULL THEN ''
                       ELSE ' · ' || TRIM(capability.Version)
                   END AS Name
            FROM SourcePorts source
            LEFT JOIN WinUI_SourcePortCapabilities capability
                ON capability.SourcePortID = source.SourcePortID
            WHERE COALESCE(source.Archived, 0) = 0
            ORDER BY source.Name COLLATE NOCASE;
            """,
            "SourcePortID",
            cancellationToken);
        var iwads = await LoadChoicesAsync(
            connection,
            """
            SELECT iwad.IWadID,
                   COALESCE(NULLIF(TRIM(iwad.Name), ''), iwad.FileName) || CASE
                       WHEN NULLIF(TRIM(metadata.Version), '') IS NULL THEN ''
                       ELSE ' · ' || TRIM(metadata.Version)
                   END AS Name
            FROM IWads iwad
            LEFT JOIN WinUI_IwadMetadata metadata
                ON metadata.IWadID = iwad.IWadID
            ORDER BY Name COLLATE NOCASE;
            """,
            "IWadID",
            cancellationToken);

        var maps = await LoadMapsAsync(connection, gameFileId, cancellationToken);
        sourcePorts.Insert(0, new LaunchOptionChoice(null, localization.Get("Automatic")));
        iwads.Insert(0, new LaunchOptionChoice(null, localization.Get("Automatic")));
        maps.Insert(0, new LaunchValueChoice(null, localization.Get("Automatic")));
        var skills = new List<LaunchValueChoice>
        {
            new(null, localization.Get("Automatic")),
            new("1", localization.Get("SkillOne")),
            new("2", localization.Get("SkillTwo")),
            new("3", localization.Get("SkillThree")),
            new("4", localization.Get("SkillFour")),
            new("5", localization.Get("SkillFive")),
        };
        return new LaunchOptionsResult(sourcePorts, iwads, maps, skills);
    }

    private static async Task EnsureGameExistsAsync(
        SqliteConnection connection,
        int gameFileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM GameFiles WHERE GameFileID = $gameFileId;";
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (count == 0)
            throw new InvalidOperationException(
                $"Der Bibliothekseintrag {gameFileId} wurde nicht gefunden.");
    }

    private static async Task<List<LaunchOptionChoice>> LoadChoicesAsync(
        SqliteConnection connection,
        string query,
        string idColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var choices = new List<LaunchOptionChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            choices.Add(new LaunchOptionChoice(
                GetNullableInt32(reader, idColumn),
                GetString(reader, "Name")));
        }

        return choices;
    }

    private static async Task<List<LaunchValueChoice>> LoadMapsAsync(
        SqliteConnection connection,
        int gameFileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Map FROM GameFiles WHERE GameFileID = $gameFileId;";
        command.Parameters.AddWithValue("$gameFileId", gameFileId);
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return MapNameExtractor.ParseStored(value)
            .Select(map => new LaunchValueChoice(map, map))
            .ToList();
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static string GetString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.CurrentCulture) ?? string.Empty;
    }
}
