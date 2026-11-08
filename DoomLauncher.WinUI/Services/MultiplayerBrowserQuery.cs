namespace DoomLauncher.WinUI.Services;

public enum ServerSortColumn { Name, Players, GameMode, Map, Iwad, Version, Ping, Password, Address }

public static class MultiplayerBrowserQuery
{
    public static IReadOnlyList<MultiplayerServer> Apply(IEnumerable<MultiplayerServer> servers,
        string search, bool populatedOnly, bool hideFull, ServerSortColumn column, bool descending)
    {
        var terms = search.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var filtered = servers.Where(server => (!populatedOnly || server.Players > 0)
            && (!hideFull || server.MaximumPlayers <= 0 || server.Players < server.MaximumPlayers)
            && terms.All(term => $"{server.Name} {server.GameMode} {server.Map} {server.Iwad} {server.Version} {server.Endpoint}"
                .Contains(term, StringComparison.CurrentCultureIgnoreCase)));
        // Keep numeric columns numeric (2 players must sort before 10, never after).
        Func<MultiplayerServer, IComparable> key = column switch
        {
            ServerSortColumn.Players => server => server.Players,
            ServerSortColumn.Ping => server => server.Ping,
            ServerSortColumn.Password => server => server.PasswordRequired,
            ServerSortColumn.GameMode => server => server.GameMode,
            ServerSortColumn.Map => server => server.Map,
            ServerSortColumn.Iwad => server => server.Iwad,
            ServerSortColumn.Version => server => server.Version,
            ServerSortColumn.Address => server => server.Endpoint.ToString(),
            _ => server => server.Name,
        };
        return (descending ? filtered.OrderByDescending(key) : filtered.OrderBy(key))
            .ThenBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(server => server.Endpoint.ToString(), StringComparer.Ordinal).ToArray();
    }
}
