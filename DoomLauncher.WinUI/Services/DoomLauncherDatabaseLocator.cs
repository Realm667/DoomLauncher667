namespace DoomLauncher.WinUI.Services;

public interface IDoomLauncherDatabaseLocator
{
    string FindDatabase();
}

public sealed class DoomLauncherDatabaseLocator : IDoomLauncherDatabaseLocator
{
    public const string DatabaseFileName = "DoomLauncher.sqlite";
    public const string DatabaseEnvironmentVariable = "DOOMLAUNCHER_DATABASE";

    public string FindDatabase()
    {
        var configuredDatabase = Environment.GetEnvironmentVariable(
            DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDatabase))
        {
            var configuredPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configuredDatabase.Trim().Trim('"')));
            if (File.Exists(configuredPath))
                return configuredPath;
            throw CreateNotFoundException([configuredPath]);
        }

        var candidates = GetCandidates().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var database = candidates.FirstOrDefault(File.Exists);

        if (database is not null)
            return Path.GetFullPath(database);

        throw CreateNotFoundException(candidates);
    }

    private static FileNotFoundException CreateNotFoundException(
        IReadOnlyList<string> candidates) =>
        new(
            $"Keine DoomLauncher-Datenbank gefunden. Lege {DatabaseEnvironmentVariable} auf die gewünschte " +
            $"{DatabaseFileName} fest oder nutze die Migration beim ersten Start. Geprüft: " +
            string.Join(", ", candidates));

    private static IEnumerable<string> GetCandidates()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoomLauncher667",
            DatabaseFileName);

        yield return Path.Combine(AppContext.BaseDirectory, DatabaseFileName);
        yield return Path.Combine(Environment.CurrentDirectory, DatabaseFileName);
    }
}
